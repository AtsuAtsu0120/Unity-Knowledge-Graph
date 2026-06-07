using System.Text.RegularExpressions;
using Ukg.Core;

namespace Ukg.Extractors;

/// <summary>
/// Unity Addressables のグループ定義（AddressableAssetGroup の .asset）を解析し、
/// AddressableGroup / AddressableEntry ノードと HAS_ENTRY / ADDRESSES エッジを抽出する。
/// Addressables を使っていないプロジェクト（グループ .asset が無い）では空の結果を返す（ADR-015）。
/// Unity YAML は独自タグを含むため、エントリ(guid/address)は正規表現で拾う。
/// </summary>
public sealed class AddressableExtractor
{
    private static readonly Regex GuidRe = new(@"m_GUID:\s*([0-9a-fA-F]{32})", RegexOptions.Compiled);
    private static readonly Regex AddrRe = new(@"m_Address:\s*(.+)", RegexOptions.Compiled);
    private static readonly Regex NameRe = new(@"m_Name:\s*(.+)", RegexOptions.Compiled);

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library", "Temp", "Logs", "obj", "Build", "Builds", ".git", ".nuget-packages", ".nuget-http"
    };

    public ExtractionResult Extract(string projectRoot)
    {
        projectRoot = Path.GetFullPath(projectRoot);
        var result = new ExtractionResult();

        var roots = new List<string>();
        foreach (var sub in new[] { "Assets", "Packages" })
        {
            var p = Path.Combine(projectRoot, sub);
            if (Directory.Exists(p)) roots.Add(p);
        }
        if (roots.Count == 0) roots.Add(projectRoot);

        foreach (var file in EnumerateAssets(roots))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            // Addressable グループ定義のみ対象（エントリ列を持つ .asset）
            if (!text.Contains("m_SerializeEntries")) continue;

            var entries = ParseEntries(text);
            if (entries.Count == 0) continue; // 設定アセット等（エントリ無し）はスキップ

            var groupName = NameRe.Match(text) is { Success: true } nm
                ? nm.Groups[1].Value.Trim()
                : Path.GetFileNameWithoutExtension(file);
            var groupKey = "addrgroup:" + groupName;
            result.AddNode(GraphNode.Create(Schema.AddressableGroup, groupKey,
                (Schema.PropName, groupName),
                (Schema.PropPath, Rel(projectRoot, file))));

            foreach (var (guid, address) in entries)
            {
                var entryKey = "addr:" + address;
                result.AddNode(GraphNode.Create(Schema.AddressableEntry, entryKey,
                    (Schema.PropName, address),
                    (Schema.PropAddress, address)));
                result.AddEdge(new GraphEdge(Schema.HasEntry,
                    Schema.AddressableGroup, groupKey, Schema.AddressableEntry, entryKey));
                // アドレス → 実アセット（Asset ノードが存在すれば接続される）
                result.AddEdge(new GraphEdge(Schema.Addresses,
                    Schema.AddressableEntry, entryKey, Schema.Asset, guid));
            }
        }

        return result;
    }

    /// <summary>m_SerializeEntries 内の (guid, address) を抽出する。guid の直後区間に m_Address があるものをエントリとみなす。</summary>
    private static List<(string Guid, string Address)> ParseEntries(string text)
    {
        var list = new List<(string, string)>();
        var seen = new HashSet<string>();
        var guids = GuidRe.Matches(text);
        for (int i = 0; i < guids.Count; i++)
        {
            int start = guids[i].Index;
            int end = i + 1 < guids.Count ? guids[i + 1].Index : text.Length;
            var seg = text.Substring(start, end - start);
            var am = AddrRe.Match(seg);
            if (!am.Success) continue; // address を持たない guid 参照はエントリではない
            var address = am.Groups[1].Value.Trim().Trim('"', '\'');
            if (address.Length == 0) continue;
            var guid = guids[i].Groups[1].Value.ToLowerInvariant();
            if (seen.Add(address)) list.Add((guid, address));
        }
        return list;
    }

    private static IEnumerable<string> EnumerateAssets(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] files, subdirs;
                try { files = Directory.GetFiles(dir, "*.asset"); subdirs = Directory.GetDirectories(dir); }
                catch { continue; }
                foreach (var f in files) yield return f;
                foreach (var d in subdirs)
                    if (!IgnoredDirs.Contains(Path.GetFileName(d)))
                        stack.Push(d);
            }
        }
    }

    private static string Rel(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
