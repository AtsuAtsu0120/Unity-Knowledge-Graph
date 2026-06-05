namespace Ukg.Core;

/// <summary>
/// 抽出器が生成するノード。<see cref="Key"/> は安定キー（クラス=完全修飾名 / Asset=GUID）で、
/// 同一キーは MERGE により同一ノードへ収束する。
/// </summary>
public sealed record GraphNode(string Label, string Key, IReadOnlyDictionary<string, object?> Props)
{
    public static GraphNode Create(string label, string key, params (string Key, object? Value)[] props)
    {
        var dict = new Dictionary<string, object?> { [Schema.PropKey] = key };
        foreach (var (k, v) in props)
            if (v is not null) dict[k] = v;
        return new GraphNode(label, key, dict);
    }
}

/// <summary>
/// 抽出器が生成する静的エッジ。両端は (ラベル, キー) で識別する。
/// </summary>
public sealed record GraphEdge(
    string Type,
    string FromLabel, string FromKey,
    string ToLabel, string ToKey,
    IReadOnlyDictionary<string, object?>? Props = null);

/// <summary>
/// 抽出結果一式。CLI はこれを <see cref="GraphRepository"/> に渡して静的レイヤーを再構築する。
/// </summary>
public sealed class ExtractionResult
{
    public List<GraphNode> Nodes { get; } = new();
    public List<GraphEdge> Edges { get; } = new();

    public void AddNode(GraphNode node) => Nodes.Add(node);
    public void AddEdge(GraphEdge edge) => Edges.Add(edge);
}
