using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ukg.Core;

namespace Ukg.Extractors;

/// <summary>
/// Roslyn の構文解析で C# の型宣言と関連を抽出する。
/// v1 は単一 Compilation を組まず、プロジェクト内の宣言名で型参照を解決する構文ベース方式。
/// （横断的な厳密解決が必要になれば Assembly-CSharp.csproj を Compilation 化して昇格できる）
/// </summary>
public sealed class CSharpExtractor
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Temp", "Logs", "obj", "Build", "Builds", ".git", ".nuget-packages", ".nuget-http"
    };

    public CSharpGraph Extract(string projectRoot)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var roots = new List<string>();
        foreach (var sub in new[] { "Assets", "Packages" })
        {
            var p = Path.Combine(projectRoot, sub);
            if (Directory.Exists(p)) roots.Add(p);
        }
        if (roots.Count == 0) roots.Add(projectRoot);

        // パス1: 全 .cs を解析して型宣言を収集
        var decls = new List<TypeDecl>();
        foreach (var file in EnumerateCs(roots))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var tree = CSharpSyntaxTree.ParseText(text);
            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var (label, ok) = LabelOf(node);
                if (!ok) continue;
                var ns = NamespaceOf(node);
                var key = KeyOf(node, ns);
                decls.Add(new TypeDecl(node, node.Identifier.Text, ns, key, label, rel));
            }
        }

        // 名前 -> 宣言 の索引（型参照の解決に使う）
        var byName = new Dictionary<string, List<TypeDecl>>(StringComparer.Ordinal);
        foreach (var d in decls)
        {
            if (!byName.TryGetValue(d.Name, out var list)) byName[d.Name] = list = new();
            list.Add(d);
        }

        var result = new ExtractionResult();
        var namespaces = new HashSet<string>();
        var classByFile = new List<ScriptClass>(); // SCRIPT_OF 橋渡し用

        // パス2: ノードとエッジを生成
        foreach (var d in decls)
        {
            bool isMono = false, isSo = false;
            ScanBases(d, ref isMono, ref isSo);

            var props = new List<(string, object?)>
            {
                (Schema.PropName, d.Name),
                (Schema.PropPath, d.File),
            };
            if (!string.IsNullOrEmpty(d.Namespace)) props.Add(("namespace", d.Namespace));
            if (isMono) props.Add(("isMonoBehaviour", true));
            if (isSo) props.Add(("isScriptableObject", true));
            result.AddNode(GraphNode.Create(d.Label, d.Key, props.ToArray()));

            classByFile.Add(new ScriptClass(d.File, d.Name, d.Label, d.Key));

            // IN_NAMESPACE
            if (!string.IsNullOrEmpty(d.Namespace))
            {
                namespaces.Add(d.Namespace);
                result.AddEdge(new GraphEdge(Schema.InNamespace, d.Label, d.Key, Schema.Namespace, d.Namespace));
            }

            // 継承 / 実装
            if (d.Node is TypeDeclarationSyntax tds && tds.BaseList is not null)
            {
                foreach (var bt in tds.BaseList.Types)
                {
                    var name = TypeName(bt.Type);
                    if (name is null) continue;
                    var target = Resolve(byName, name, d.Namespace);
                    if (target is null || target.Key == d.Key) continue;
                    var rel = target.Label == Schema.Interface ? Schema.Implements : Schema.Inherits;
                    result.AddEdge(new GraphEdge(rel, d.Label, d.Key, target.Label, target.Key));
                }
            }

            // REFERENCES（フィールド/プロパティ型）
            if (d.Node is TypeDeclarationSyntax tdecl)
            {
                var referenced = new HashSet<string>();
                foreach (var member in tdecl.Members)
                {
                    var typeSyntax = member switch
                    {
                        FieldDeclarationSyntax f => f.Declaration.Type,
                        PropertyDeclarationSyntax p => p.Type,
                        _ => null
                    };
                    if (typeSyntax is null) continue;
                    foreach (var simple in TypeIdentifiers(typeSyntax))
                    {
                        var target = Resolve(byName, simple, d.Namespace);
                        if (target is null || target.Key == d.Key) continue;
                        if (!referenced.Add(target.Key)) continue;
                        result.AddEdge(new GraphEdge(Schema.References, d.Label, d.Key, target.Label, target.Key));
                    }
                }
            }
        }

        // Namespace ノード
        foreach (var ns in namespaces)
            result.AddNode(GraphNode.Create(Schema.Namespace, ns, (Schema.PropName, ns)));

        return new CSharpGraph(result, classByFile);
    }

    private static void ScanBases(TypeDecl d, ref bool isMono, ref bool isSo)
    {
        if (d.Node is not TypeDeclarationSyntax tds || tds.BaseList is null) return;
        foreach (var bt in tds.BaseList.Types)
        {
            var n = TypeName(bt.Type);
            if (n == "MonoBehaviour") isMono = true;
            else if (n == "ScriptableObject") isSo = true;
        }
    }

    private static TypeDecl? Resolve(Dictionary<string, List<TypeDecl>> byName, string simpleName, string currentNs)
    {
        if (!byName.TryGetValue(simpleName, out var list) || list.Count == 0) return null;
        if (list.Count == 1) return list[0];
        // 同一名前空間を優先
        var sameNs = list.FirstOrDefault(d => d.Namespace == currentNs);
        return sameNs ?? list[0];
    }

    /// <summary>型構文の代表名（最も外側の型名）を返す。</summary>
    private static string? TypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => TypeName(q.Right),
        AliasQualifiedNameSyntax a => TypeName(a.Name),
        NullableTypeSyntax n => TypeName(n.ElementType),
        ArrayTypeSyntax arr => TypeName(arr.ElementType),
        _ => null
    };

    /// <summary>型構文に現れる全ての識別子名（ジェネリック引数含む）を列挙する。</summary>
    private static IEnumerable<string> TypeIdentifiers(TypeSyntax type)
    {
        foreach (var node in type.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case GenericNameSyntax g: yield return g.Identifier.Text; break;
                case IdentifierNameSyntax id: yield return id.Identifier.Text; break;
            }
        }
    }

    private static (string Label, bool Ok) LabelOf(BaseTypeDeclarationSyntax node) => node switch
    {
        InterfaceDeclarationSyntax => (Schema.Interface, true),
        StructDeclarationSyntax => (Schema.Struct, true),
        EnumDeclarationSyntax => (Schema.Enum, true),
        RecordDeclarationSyntax r => (r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? Schema.Struct : Schema.Class, true),
        ClassDeclarationSyntax => (Schema.Class, true),
        _ => ("", false)
    };

    private static string NamespaceOf(SyntaxNode node)
    {
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
        {
            switch (cur)
            {
                case FileScopedNamespaceDeclarationSyntax f: return f.Name.ToString();
                case NamespaceDeclarationSyntax n: return n.Name.ToString();
            }
        }
        return "";
    }

    private static string KeyOf(BaseTypeDeclarationSyntax node, string ns)
    {
        // 入れ子の型を外側から連結し、名前空間を前置して安定キーを作る
        var names = new Stack<string>();
        names.Push(node.Identifier.Text);
        for (var cur = node.Parent; cur is not null; cur = cur.Parent)
            if (cur is BaseTypeDeclarationSyntax outer)
                names.Push(outer.Identifier.Text);
        var typeChain = string.Join(".", names);
        return string.IsNullOrEmpty(ns) ? typeChain : $"{ns}.{typeChain}";
    }

    private IEnumerable<string> EnumerateCs(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files, subdirs;
                try { files = Directory.GetFiles(dir, "*.cs"); subdirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var f in files) yield return f;
                foreach (var sd in subdirs)
                    if (!IgnoredDirs.Contains(Path.GetFileName(sd)))
                        stack.Push(sd);
            }
        }
    }

    private sealed record TypeDecl(
        BaseTypeDeclarationSyntax Node, string Name, string Namespace, string Key, string Label, string File);
}

/// <summary>C# 抽出の出力。SCRIPT_OF 橋渡し用にファイル単位のクラス一覧も公開する。</summary>
public sealed record CSharpGraph(ExtractionResult Result, IReadOnlyList<ScriptClass> Classes);

/// <summary>ファイルに属するクラス（.cs Asset と Class ノードの SCRIPT_OF 接続に使う）。</summary>
public sealed record ScriptClass(string File, string Name, string Label, string Key);
