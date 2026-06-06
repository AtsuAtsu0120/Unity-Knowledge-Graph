namespace Ukg.Core;

/// <summary>
/// 同期ラベル伝播法(LPA)によるコミュニティ検出（ADR-005）。
/// 走査順を固定し決定論的に動かすためテスト可能。要約は構成要素から機械生成する
/// （LLM 接続時は <see cref="ISummarizer"/> を差し替え）。
/// </summary>
public static class CommunityDetector
{
    /// <summary>
    /// 無向グラフとしてLPAを回し、コミュニティ集合を返す。
    /// </summary>
    public static List<CommunityResult> Detect(
        IReadOnlyList<(string Key, string Label, string Name)> nodes,
        IReadOnlyList<(string A, string B)> edges,
        ISummarizer summarizer,
        int maxIterations = 20,
        int minSize = 2)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++) index[nodes[i].Key] = i;

        // 隣接（無向）
        var adj = new List<int>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) adj[i] = new List<int>();
        foreach (var (a, b) in edges)
        {
            if (!index.TryGetValue(a, out var ai) || !index.TryGetValue(b, out var bi) || ai == bi) continue;
            adj[ai].Add(bi);
            adj[bi].Add(ai);
        }

        // 初期ラベル＝自分自身
        var labels = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) labels[i] = i;

        // 反復: 近傍の最頻ラベルへ。タイは最小ラベルで決定論化。
        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (adj[i].Count == 0) continue;
                var counts = new Dictionary<int, int>();
                foreach (var nb in adj[i])
                {
                    counts.TryGetValue(labels[nb], out var c);
                    counts[labels[nb]] = c + 1;
                }
                int best = labels[i], bestCount = -1;
                foreach (var (lab, c) in counts)
                    if (c > bestCount || (c == bestCount && lab < best)) { best = lab; bestCount = c; }
                if (best != labels[i]) { labels[i] = best; changed = true; }
            }
            if (!changed) break;
        }

        // ラベルごとに集約
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!groups.TryGetValue(labels[i], out var list)) groups[labels[i]] = list = new();
            list.Add(i);
        }

        var result = new List<CommunityResult>();
        int cid = 0;
        // 安定した採番（メンバ最小キー順）
        foreach (var g in groups.Values
                     .Where(g => g.Count >= minSize)
                     .OrderBy(g => nodes[g.Min()].Key, StringComparer.Ordinal))
        {
            var memberKeys = g.Select(i => nodes[i].Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var memberNames = g.Select(i => nodes[i].Name).ToList();
            var id = $"c{cid++}";
            var (name, summary) = summarizer.Summarize(memberNames, CountInternalEdges(g, adj, index, nodes));
            result.Add(new CommunityResult(id, name, summary, memberKeys));
        }
        return result;
    }

    private static int CountInternalEdges(
        List<int> group, List<int>[] adj, Dictionary<string, int> index,
        IReadOnlyList<(string Key, string Label, string Name)> nodes)
    {
        var set = new HashSet<int>(group);
        int e = 0;
        foreach (var i in group) foreach (var nb in adj[i]) if (set.Contains(nb)) e++;
        return e / 2;
    }
}

/// <summary>コミュニティ要約器。名前と要約文を返す。</summary>
public interface ISummarizer
{
    (string Name, string Summary) Summarize(IReadOnlyList<string> memberNames, int internalEdges);
}

/// <summary>
/// オフライン用の機械要約。代表メンバ名から名前を作り、構成を列挙する。
/// LLM 接続時はこの実装を差し替える。
/// </summary>
public sealed class HeuristicSummarizer : ISummarizer
{
    public (string Name, string Summary) Summarize(IReadOnlyList<string> memberNames, int internalEdges)
    {
        // 最頻の語幹を名前候補にする（例: PlayerController/PlayerInput → "Player系"）
        var stem = CommonStem(memberNames);
        var head = memberNames.OrderBy(n => n, StringComparer.Ordinal).Take(3);
        var name = stem is not null ? $"{stem}系" : $"{string.Join("/", head.Take(2))} 周辺";
        var summary =
            $"{memberNames.Count}型のクラスタ（内部結合 {internalEdges} 本）。" +
            $"主要メンバ: {string.Join(", ", memberNames.OrderBy(n => n, StringComparer.Ordinal).Take(8))}" +
            (memberNames.Count > 8 ? " …" : "");
        return (name, summary);
    }

    /// <summary>メンバ名群の最長共通プレフィックス語（CamelCase先頭語）を推定する。</summary>
    private static string? CommonStem(IReadOnlyList<string> names)
    {
        if (names.Count < 2) return null;
        string Head(string s)
        {
            int i = 1;
            while (i < s.Length && !(char.IsUpper(s[i]) && i > 0)) i++;
            return s[..Math.Min(i, s.Length)];
        }
        var first = Head(names[0]);
        return names.All(n => n.StartsWith(first, StringComparison.Ordinal)) && first.Length >= 3 ? first : null;
    }
}
