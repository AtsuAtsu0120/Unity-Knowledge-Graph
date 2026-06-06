using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ukg.Core;

namespace Ukg.Extractors;

/// <summary>
/// Roslyn の <c>Compilation</c>/<c>SemanticModel</c> で C# の型・メソッド・関連を抽出する（P2, ADR-004）。
/// プロジェクト内型はシンボルで厳密解決し、Unity固有結合(GetComponent&lt;T&gt;等)は
/// エンジンアセンブリ非依存で動くよう構文パターンで検出する。
/// </summary>
public sealed class CSharpExtractor
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Temp", "Logs", "obj", "Build", "Builds", ".git", ".nuget-packages", ".nuget-http"
    };

    // GetComponent 系（型引数の型へ USES_COMPONENT を張る Unity メソッド群）
    private static readonly HashSet<string> ComponentAccessors = new(StringComparer.Ordinal)
    {
        "GetComponent", "GetComponents", "GetComponentInChildren", "GetComponentsInChildren",
        "GetComponentInParent", "GetComponentsInParent", "AddComponent", "TryGetComponent",
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

        // パース（並列, P5）
        var files = EnumerateCs(roots).ToList();
        var parsed = new ConcurrentBag<(SyntaxTree Tree, string Rel, string Hash)>();
        Parallel.ForEach(files, file =>
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { return; }
            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            var tree = CSharpSyntaxTree.ParseText(text, path: rel);
            parsed.Add((tree, rel, Hashing.Sha1(text)));
        });
        var trees = parsed.OrderBy(t => t.Rel, StringComparer.Ordinal).ToList();
        var hashByTree = trees.ToDictionary(t => t.Tree, t => t.Hash);
        var relByTree = trees.ToDictionary(t => t.Tree, t => t.Rel);

        // Compilation（BCL 参照込み）。Unity アセンブリは無くてもよい。
        var compilation = CSharpCompilation.Create("Assembly-CSharp",
            trees.Select(t => t.Tree),
            BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = new ExtractionResult();
        var namespaces = new HashSet<string>();
        var classByFile = new List<ScriptClass>();

        // 宣言シンボル集合（プロジェクト内判定用）と型→キーの索引
        var declaredTypes = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var typeDecls = new List<(BaseTypeDeclarationSyntax Node, INamedTypeSymbol Symbol, SemanticModel Model, string Rel, string Hash)>();

        foreach (var (tree, rel, hash) in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(node) is not INamedTypeSymbol sym) continue;
                if (!LabelOf(sym).Ok) continue;
                var key = Fqn(sym);
                if (!declaredTypes.ContainsKey(sym)) declaredTypes[sym] = key;
                typeDecls.Add((node, sym, model, rel, hash));
            }
        }

        foreach (var (node, sym, model, rel, hash) in typeDecls)
        {
            var (label, _) = LabelOf(sym);
            var key = Fqn(sym);
            var ns = sym.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() : "";
            var doc = DocSummary(sym);
            bool isMono = InheritsByName(node, "MonoBehaviour");
            bool isSo = InheritsByName(node, "ScriptableObject");

            var signature = TypeSignature(node, sym);
            var refNames = new List<string>();

            var props = new List<(string, object?)>
            {
                (Schema.PropName, sym.Name),
                (Schema.PropPath, rel),
                (Schema.PropContentHash, hash),
            };
            if (!string.IsNullOrEmpty(ns)) props.Add(("namespace", ns));
            if (!string.IsNullOrEmpty(doc)) props.Add((Schema.PropDoc, doc));
            if (!string.IsNullOrEmpty(signature)) props.Add((Schema.PropSignature, signature));
            if (isMono) props.Add(("isMonoBehaviour", true));
            if (isSo) props.Add(("isScriptableObject", true));

            classByFile.Add(new ScriptClass(rel, sym.Name, label, key));

            // IN_NAMESPACE
            if (!string.IsNullOrEmpty(ns))
            {
                namespaces.Add(ns);
                result.AddEdge(new GraphEdge(Schema.InNamespace, label, key, Schema.Namespace, ns));
            }

            // 継承 / 実装（シンボル解決, プロジェクト内のみ）
            if (sym.BaseType is { } bt && declaredTypes.TryGetValue(bt.OriginalDefinition, out var btKey))
                result.AddEdge(new GraphEdge(Schema.Inherits, label, key, LabelOf(bt).Label, btKey));
            foreach (var iface in sym.Interfaces)
                if (declaredTypes.TryGetValue(iface.OriginalDefinition, out var ifKey))
                    result.AddEdge(new GraphEdge(Schema.Implements, label, key, Schema.Interface, ifKey));

            // REFERENCES（フィールド/プロパティ + メソッドの引数/戻り値）
            var referenced = new HashSet<string>();
            foreach (var refType in ReferencedTypes(sym))
            {
                if (!declaredTypes.TryGetValue(refType.OriginalDefinition, out var tKey)) continue;
                if (tKey == key) continue;
                if (!referenced.Add(tKey)) continue;
                refNames.Add(refType.Name);
                result.AddEdge(new GraphEdge(Schema.References, label, key, LabelOf(refType).Label, tKey));
            }

            // メソッドノード・CALLS・Unity 結合
            ExtractMethods(node, sym, model, key, label, declaredTypes, result);
            ExtractUnityCoupling(node, model, key, label, declaredTypes, result, referenced);

            // 埋め込みテキスト（意味検索の素）
            props.Add((Schema.PropEmbedText, BuildEmbedText(sym.Name, ns, doc, signature, refNames)));

            result.AddNode(GraphNode.Create(label, key, props.ToArray()));
        }

        foreach (var ns in namespaces)
            result.AddNode(GraphNode.Create(Schema.Namespace, ns, (Schema.PropName, ns)));

        return new CSharpGraph(result, classByFile, hashByTree.Values.ToList());
    }

    // ---- メソッド / 呼び出しグラフ ----

    private void ExtractMethods(
        BaseTypeDeclarationSyntax typeNode, INamedTypeSymbol typeSym, SemanticModel model,
        string typeKey, string typeLabel, Dictionary<ISymbol, string> declared, ExtractionResult result)
    {
        foreach (var m in typeNode.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            // 入れ子型のメソッドは当該型の走査に任せる
            if (m.FirstAncestorOrSelf<BaseTypeDeclarationSyntax>() != typeNode) continue;
            if (model.GetDeclaredSymbol(m) is not IMethodSymbol ms) continue;
            var mKey = MethodKey(ms);
            var doc = DocSummary(ms);

            var props = new List<(string, object?)>
            {
                (Schema.PropName, ms.Name),
                (Schema.PropSignature, ms.ToDisplayString()),
                (Schema.PropPath, typeNode.SyntaxTree.FilePath),
            };
            if (!string.IsNullOrEmpty(doc)) props.Add((Schema.PropDoc, doc));
            props.Add((Schema.PropEmbedText, BuildEmbedText(ms.Name, typeSym.Name, doc, ms.ToDisplayString(), Array.Empty<string>())));
            result.AddNode(GraphNode.Create(Schema.Method, mKey, props.ToArray()));
            result.AddEdge(new GraphEdge(Schema.DeclaredIn, Schema.Method, mKey, typeLabel, typeKey));

            // CALLS: 本体の呼び出しをシンボル解決
            if (m.Body is null && m.ExpressionBody is null) continue;
            foreach (var inv in m.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol target) continue;
                if (target.MethodKind != MethodKind.Ordinary) continue;
                if (!declared.ContainsKey(target.ContainingType.OriginalDefinition)) continue;
                var targetKey = MethodKey(target.OriginalDefinition);
                if (targetKey == mKey) continue;
                result.AddEdge(new GraphEdge(Schema.Calls, Schema.Method, mKey, Schema.Method, targetKey));
            }
        }
    }

    // ---- Unity 結合（構文パターン, ADR-004）----

    private void ExtractUnityCoupling(
        BaseTypeDeclarationSyntax typeNode, SemanticModel model, string typeKey, string typeLabel,
        Dictionary<ISymbol, string> declared, ExtractionResult result, HashSet<string> already)
    {
        void LinkType(TypeSyntax t)
        {
            if (model.GetTypeInfo(t).Type is not INamedTypeSymbol ts) return;
            if (!declared.TryGetValue(ts.OriginalDefinition, out var tKey) || tKey == typeKey) return;
            if (!already.Add("uc:" + tKey)) return;
            result.AddEdge(new GraphEdge(Schema.UsesComponent, typeLabel, typeKey, LabelOf(ts).Label, tKey));
        }

        // GetComponent<T>() 系
        foreach (var inv in typeNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = inv.Expression switch
            {
                MemberAccessExpressionSyntax ma => ma.Name,
                GenericNameSyntax g => g,
                IdentifierNameSyntax id => (SimpleNameSyntax)id,
                _ => null
            };
            if (name is GenericNameSyntax gen && ComponentAccessors.Contains(gen.Identifier.Text)
                && gen.TypeArgumentList.Arguments.Count == 1)
                LinkType(gen.TypeArgumentList.Arguments[0]);
        }

        // [RequireComponent(typeof(T))]
        foreach (var attr in typeNode.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (attr.Name.ToString() is not ("RequireComponent" or "RequireComponentAttribute")) continue;
            if (attr.ArgumentList is null) continue;
            foreach (var arg in attr.ArgumentList.Arguments)
                if (arg.Expression is TypeOfExpressionSyntax to)
                    LinkType(to.Type);
        }
    }

    // ---- ヘルパ ----

    private static IEnumerable<ITypeSymbol> ReferencedTypes(INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol f when !f.IsImplicitlyDeclared: yield return f.Type; break;
                case IPropertySymbol p: yield return p.Type; break;
                case IMethodSymbol { MethodKind: MethodKind.Ordinary } m:
                    yield return m.ReturnType;
                    foreach (var par in m.Parameters) yield return par.Type;
                    break;
            }
        }
    }

    private static string Fqn(INamedTypeSymbol sym) =>
        sym.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(
            SymbolDisplayGlobalNamespaceStyle.Omitted)).Replace("global::", "");

    private static string MethodKey(IMethodSymbol m)
    {
        var paramTypes = string.Join(",", m.Parameters.Select(p => p.Type.Name));
        return $"{Fqn(m.ContainingType)}.{m.Name}({paramTypes})";
    }

    private static (string Label, bool Ok) LabelOf(INamedTypeSymbol sym) => sym.TypeKind switch
    {
        TypeKind.Interface => (Schema.Interface, true),
        TypeKind.Struct => (Schema.Struct, true),
        TypeKind.Enum => (Schema.Enum, true),
        TypeKind.Class => (Schema.Class, true),
        _ => (Schema.Class, false),
    };

    private static (string Label, bool Ok) LabelOf(ITypeSymbol sym) =>
        sym is INamedTypeSymbol n ? LabelOf(n) : (Schema.Class, false);

    private static bool InheritsByName(BaseTypeDeclarationSyntax node, string baseName)
    {
        if (node is not TypeDeclarationSyntax tds || tds.BaseList is null) return false;
        foreach (var bt in tds.BaseList.Types)
        {
            var n = bt.Type switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                GenericNameSyntax g => g.Identifier.Text,
                _ => null
            };
            if (n == baseName) return true;
        }
        return false;
    }

    private static string? DocSummary(ISymbol sym)
    {
        var xml = sym.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var start = xml.IndexOf("<summary>", StringComparison.Ordinal);
        var end = xml.IndexOf("</summary>", StringComparison.Ordinal);
        string text = start >= 0 && end > start
            ? xml.Substring(start + 9, end - start - 9)
            : xml;
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length == 0 ? null : (text.Length > 400 ? text[..400] : text);
    }

    private static string TypeSignature(BaseTypeDeclarationSyntax node, INamedTypeSymbol sym)
    {
        var bases = (node as TypeDeclarationSyntax)?.BaseList?.Types.Select(b => b.Type.ToString());
        var baseStr = bases is not null && bases.Any() ? " : " + string.Join(", ", bases) : "";
        return $"{sym.TypeKind.ToString().ToLowerInvariant()} {sym.Name}{baseStr}";
    }

    private static string BuildEmbedText(string name, string ns, string? doc, string? signature, IReadOnlyList<string> refs)
    {
        var parts = new List<string> { name };
        if (!string.IsNullOrEmpty(ns)) parts.Add(ns);
        if (!string.IsNullOrEmpty(signature)) parts.Add(signature);
        if (!string.IsNullOrEmpty(doc)) parts.Add(doc);
        if (refs.Count > 0) parts.Add("uses " + string.Join(" ", refs));
        return string.Join(". ", parts);
    }

    private static IReadOnlyList<MetadataReference> BclReferences()
    {
        var refs = new List<MetadataReference>();
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (tpa is not null)
            foreach (var path in tpa.Split(Path.PathSeparator))
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    try { refs.Add(MetadataReference.CreateFromFile(path)); } catch { }
        return refs;
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
}

/// <summary>C# 抽出の出力。SCRIPT_OF 橋渡し用にファイル単位のクラス一覧も公開する。</summary>
public sealed record CSharpGraph(ExtractionResult Result, IReadOnlyList<ScriptClass> Classes, IReadOnlyList<string> FileHashes);

/// <summary>ファイルに属するクラス（.cs Asset と Class ノードの SCRIPT_OF 接続に使う）。</summary>
public sealed record ScriptClass(string File, string Name, string Label, string Key);
