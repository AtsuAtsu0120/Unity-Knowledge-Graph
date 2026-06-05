using System.Text.RegularExpressions;
using Ukg.Core;

namespace Ukg.Extractors;

/// <summary>
/// Unity の .meta / YAML アセットを解析し、Asset ノードと DEPENDS_ON エッジを抽出する。
/// Unity の YAML は独自タグ（!u!）を含み標準パーサと相性が悪いため、GUID 参照は正規表現で拾う。
/// </summary>
public sealed class AssetExtractor
{
    private static readonly Regex GuidLine = new(@"guid:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);

    // GUID 参照を解析して依存を張る対象（テキスト YAML 形式のアセット）
    private static readonly HashSet<string> ReferencingExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim",
        ".overrideController", ".playable", ".mask", ".preset", ".spriteatlas"
    };

    // スキャンから除外するディレクトリ
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Temp", "Logs", "obj", "Build", "Builds", ".git", ".nuget-packages", ".nuget-http"
    };

    public AssetGraph Extract(string projectRoot)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var roots = new List<string>();
        foreach (var sub in new[] { "Assets", "Packages" })
        {
            var p = Path.Combine(projectRoot, sub);
            if (Directory.Exists(p)) roots.Add(p);
        }
        if (roots.Count == 0) roots.Add(projectRoot); // フォールバック: ルート全体

        // 1) 全 .meta を走査して guid <-> path を構築（フォルダの meta は除外）
        var pathByGuid = new Dictionary<string, string>();
        var guidByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in EnumerateFiles(roots, "*.meta"))
        {
            var assetPath = meta[..^".meta".Length];
            if (Directory.Exists(assetPath)) continue; // フォルダの .meta はスキップ
            if (!File.Exists(assetPath)) continue;

            var guid = ReadGuid(meta);
            if (guid is null) continue;
            var rel = Rel(projectRoot, assetPath);
            pathByGuid[guid] = rel;
            guidByPath[rel] = guid;
        }

        // 2) Asset ノードを生成
        var result = new ExtractionResult();
        foreach (var (guid, rel) in pathByGuid)
        {
            result.AddNode(GraphNode.Create(Schema.Asset, guid,
                (Schema.PropName, Path.GetFileName(rel)),
                (Schema.PropPath, rel),
                (Schema.PropGuid, guid)));
        }

        // 3) 参照系アセットを開いて guid 参照 → DEPENDS_ON
        foreach (var (guid, rel) in pathByGuid)
        {
            var ext = Path.GetExtension(rel);
            if (!ReferencingExtensions.Contains(ext)) continue;

            var abs = Path.Combine(projectRoot, rel);
            foreach (var refGuid in ReadGuidReferences(abs))
            {
                if (refGuid == guid) continue;                 // 自己参照は除外
                if (!pathByGuid.ContainsKey(refGuid)) continue; // プロジェクト外の guid は張らない
                result.AddEdge(new GraphEdge(Schema.DependsOn, Schema.Asset, guid, Schema.Asset, refGuid));
            }
        }

        return new AssetGraph(result, pathByGuid, guidByPath);
    }

    private static string? ReadGuid(string metaPath)
    {
        try
        {
            foreach (var line in File.ReadLines(metaPath))
            {
                var m = GuidLine.Match(line);
                if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
            }
        }
        catch { /* 読み取り不能はスキップ */ }
        return null;
    }

    private static IEnumerable<string> ReadGuidReferences(string assetPath)
    {
        string text;
        try { text = File.ReadAllText(assetPath); }
        catch { yield break; }

        var seen = new HashSet<string>();
        foreach (Match m in GuidLine.Matches(text))
        {
            var g = m.Groups[1].Value.ToLowerInvariant();
            if (seen.Add(g)) yield return g;
        }
    }

    private static IEnumerable<string> EnumerateFiles(IEnumerable<string> roots, string pattern)
    {
        foreach (var root in roots)
        foreach (var file in SafeEnumerate(root, pattern))
            yield return file;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, subdirs;
            try { files = Directory.GetFiles(dir, pattern); subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var f in files) yield return f;
            foreach (var d in subdirs)
                if (!IgnoredDirs.Contains(Path.GetFileName(d)))
                    stack.Push(d);
        }
    }

    /// <summary>プロジェクトルートからの相対パス（フォワードスラッシュ正規化）。</summary>
    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>
/// AssetExtractor の出力。SCRIPT_OF 橋渡しのため guid<->path マップも公開する。
/// </summary>
public sealed record AssetGraph(
    ExtractionResult Result,
    IReadOnlyDictionary<string, string> PathByGuid,
    IReadOnlyDictionary<string, string> GuidByPath);
