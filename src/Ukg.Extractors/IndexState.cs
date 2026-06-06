using System.Text.Json;
using Ukg.Core;

namespace Ukg.Extractors;

/// <summary>
/// インデックス時点のファイル内容ハッシュ集合（ADR-009）。グラフの鮮度判定・増分スキップ・
/// 構造変更の検出に使う。グラフ内の <c>UkgMeta</c> ノードに JSON で永続化する。
/// </summary>
public sealed class IndexState
{
    public int Version { get; init; } = 1;
    public string? IndexedAt { get; init; }
    /// <summary>相対パス → SHA1（グラフに影響する .cs / .meta / 参照系アセット）。</summary>
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.Ordinal);

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Temp", "Logs", "obj", "Build", "Builds", ".git", ".nuget-packages", ".nuget-http"
    };

    private static readonly HashSet<string> TrackedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".meta",
        ".prefab", ".unity", ".asset", ".mat", ".controller", ".anim",
        ".overrideController", ".playable", ".mask", ".preset", ".spriteatlas"
    };

    /// <summary>プロジェクト配下の追跡対象ファイルを走査し現在のハッシュ集合を作る。</summary>
    public static IndexState Compute(string projectRoot, string nowIso)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var roots = new List<string>();
        foreach (var sub in new[] { "Assets", "Packages" })
        {
            var p = Path.Combine(projectRoot, sub);
            if (Directory.Exists(p)) roots.Add(p);
        }
        if (roots.Count == 0) roots.Add(projectRoot);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        foreach (var file in Enumerate(root))
        {
            if (!TrackedExtensions.Contains(Path.GetExtension(file))) continue;
            string text;
            try { text = File.ReadAllText(file); } catch { continue; }
            var rel = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            files[rel] = Hashing.Sha1(text);
        }
        return new IndexState { IndexedAt = nowIso, Files = files };
    }

    public StateDiff DiffFrom(IndexState? previous)
    {
        var prev = previous?.Files ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var added = new List<string>();
        var changed = new List<string>();
        var removed = new List<string>();
        foreach (var (path, hash) in Files)
        {
            if (!prev.TryGetValue(path, out var old)) added.Add(path);
            else if (old != hash) changed.Add(path);
        }
        foreach (var path in prev.Keys)
            if (!Files.ContainsKey(path)) removed.Add(path);
        added.Sort(StringComparer.Ordinal); changed.Sort(StringComparer.Ordinal); removed.Sort(StringComparer.Ordinal);
        return new StateDiff(added, changed, removed);
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static IndexState? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<IndexState>(json); } catch { return null; }
    }

    private static IEnumerable<string> Enumerate(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, subdirs;
            try { files = Directory.GetFiles(dir); subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in subdirs)
                if (!IgnoredDirs.Contains(Path.GetFileName(d)))
                    stack.Push(d);
        }
    }
}

/// <summary>状態差分。added/changed/removed は相対パス。</summary>
public sealed record StateDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed)
{
    public bool HasChanges => Added.Count > 0 || Changed.Count > 0 || Removed.Count > 0;
    public IEnumerable<string> AddedAndChanged => Added.Concat(Changed);
}
