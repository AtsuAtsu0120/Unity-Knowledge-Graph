using System.Text;

namespace Ukg.Core;

/// <summary>
/// グラフへの高レベル操作。静的レイヤーの冪等な再構築と、CLI 用の定型クエリ・
/// 意味エッジ操作を提供する。意味エッジ（source=semantic）は再インデックスで保持される。
/// </summary>
public sealed class GraphRepository
{
    private readonly GraphClient _client;

    public GraphRepository(GraphClient client) => _client = client;

    private static readonly string[] IndexedLabels =
    {
        Schema.Class, Schema.Interface, Schema.Struct, Schema.Enum,
        Schema.Namespace, Schema.Asset, Schema.Concept
    };

    /// <summary>各ラベルの key に索引を作成する（既存なら無視）。</summary>
    public void EnsureIndexes()
    {
        foreach (var label in IndexedLabels)
        {
            try { _client.Query($"CREATE INDEX FOR (n:{label}) ON (n.{Schema.PropKey})"); }
            catch { /* 既に存在する場合は無視 */ }
        }
    }

    /// <summary>
    /// 抽出結果で静的レイヤーを再構築する。
    /// 1) source=static のエッジを削除 → 2) ノードを upsert（MERGE）→ 3) 静的エッジを再生成。
    /// source=semantic のエッジには一切触れない。
    /// </summary>
    public StaticLayerStats ApplyStaticLayer(ExtractionResult result)
    {
        EnsureIndexes();

        // 1) 既存の静的エッジを削除（意味エッジは残す）
        _client.Query($"MATCH ()-[r]->() WHERE r.{Schema.PropSource} = '{Schema.SourceStatic}' DELETE r");

        // 2) ノードを upsert（ラベル単位の UNWIND バッチ）
        int nodeCount = 0;
        foreach (var group in result.Nodes.GroupBy(n => n.Label))
        {
            foreach (var batch in Chunk(group, 500))
            {
                var rows = batch.Select(n => Cypher.Map(WithSource(n.Props, Schema.SourceStatic)));
                var cypher =
                    $"UNWIND [{string.Join(", ", rows)}] AS row " +
                    $"MERGE (n:{group.Key} {{{Schema.PropKey}: row.{Schema.PropKey}}}) " +
                    $"SET n += row";
                _client.Query(cypher);
                nodeCount += batch.Count;
            }
        }

        // 3) 静的エッジを再生成（type×fromLabel×toLabel 単位の UNWIND バッチ）
        int edgeCount = 0;
        foreach (var group in result.Edges.GroupBy(e => (e.Type, e.FromLabel, e.ToLabel)))
        {
            foreach (var batch in Chunk(group, 500))
            {
                var rows = batch.Select(e =>
                {
                    var props = WithSource(e.Props ?? new Dictionary<string, object?>(), Schema.SourceStatic);
                    return $"{{f: {Cypher.Str(e.FromKey)}, t: {Cypher.Str(e.ToKey)}, p: {Cypher.Map(props)}}}";
                });
                var cypher =
                    $"UNWIND [{string.Join(", ", rows)}] AS row " +
                    $"MATCH (a:{group.Key.FromLabel} {{{Schema.PropKey}: row.f}}) " +
                    $"MATCH (b:{group.Key.ToLabel} {{{Schema.PropKey}: row.t}}) " +
                    $"MERGE (a)-[r:{group.Key.Type}]->(b) " +
                    $"SET r += row.p";
                _client.Query(cypher);
                edgeCount += batch.Count;
            }
        }

        return new StaticLayerStats(nodeCount, edgeCount);
    }

    /// <summary>名前またはキーでノードを検索する。</summary>
    public QueryResult FindByName(string nameOrKey) =>
        _client.ReadOnlyQuery(
            $"MATCH (n) WHERE n.{Schema.PropName} = {Cypher.Str(nameOrKey)} OR n.{Schema.PropKey} = {Cypher.Str(nameOrKey)} " +
            $"RETURN labels(n)[0] AS label, n.{Schema.PropName} AS name, n.{Schema.PropKey} AS key, " +
            $"n.{Schema.PropPath} AS path, n.{Schema.PropSource} AS source LIMIT 100");

    /// <summary>ノードの隣接エッジ（出入り両方向）を方向情報付きで返す。</summary>
    public QueryResult Neighbors(string nameOrKey) =>
        _client.ReadOnlyQuery(
            $"MATCH (n) WHERE n.{Schema.PropName} = {Cypher.Str(nameOrKey)} OR n.{Schema.PropKey} = {Cypher.Str(nameOrKey)} " +
            $"MATCH (n)-[r]-(m) " +
            $"RETURN type(r) AS rel, " +
            $"CASE WHEN startNode(r) = n THEN 'out' ELSE 'in' END AS dir, " +
            $"labels(m)[0] AS label, m.{Schema.PropName} AS name, m.{Schema.PropKey} AS key, " +
            $"r.{Schema.PropSource} AS source LIMIT 200");

    /// <summary>Asset の依存（DEPENDS_ON / SCRIPT_OF）を返す。</summary>
    public QueryResult Deps(string assetPathOrGuid) =>
        _client.ReadOnlyQuery(
            $"MATCH (a:{Schema.Asset}) WHERE a.{Schema.PropPath} = {Cypher.Str(assetPathOrGuid)} " +
            $"OR a.{Schema.PropGuid} = {Cypher.Str(assetPathOrGuid)} OR a.{Schema.PropName} = {Cypher.Str(assetPathOrGuid)} " +
            $"MATCH (a)-[r:{Schema.DependsOn}|{Schema.ScriptOf}]->(m) " +
            $"RETURN type(r) AS rel, labels(m)[0] AS label, m.{Schema.PropName} AS name, " +
            $"m.{Schema.PropKey} AS key, m.{Schema.PropPath} AS path LIMIT 200");

    /// <summary>このノードに（推移的に）依存している上流ノードを返す＝変更時の影響範囲。</summary>
    public QueryResult Impact(string nameOrKey) =>
        _client.ReadOnlyQuery(
            $"MATCH (n) WHERE n.{Schema.PropName} = {Cypher.Str(nameOrKey)} OR n.{Schema.PropKey} = {Cypher.Str(nameOrKey)} " +
            $"MATCH (m)-[:{Schema.References}|{Schema.DependsOn}|{Schema.ScriptOf}|{Schema.Inherits}|{Schema.Implements}*1..4]->(n) " +
            $"RETURN DISTINCT labels(m)[0] AS label, m.{Schema.PropName} AS name, m.{Schema.PropKey} AS key LIMIT 200");

    /// <summary>意味エッジを追加する（source=semantic）。</summary>
    public QueryResult AddSemanticEdge(string from, string to, string rel, double? confidence, string? why, string author)
    {
        if (!Schema.IsSemanticRelation(rel))
            throw new ArgumentException(
                $"'{rel}' は意味エッジではありません。許可: {string.Join(", ", Schema.SemanticRelations)}");

        var props = new Dictionary<string, object?> { [Schema.PropSource] = Schema.SourceSemantic, [Schema.PropAuthor] = author };
        if (confidence is not null) props[Schema.PropConfidence] = confidence;
        if (!string.IsNullOrEmpty(why)) props[Schema.PropRationale] = why;

        return _client.Query(
            $"MATCH (a) WHERE a.{Schema.PropName} = {Cypher.Str(from)} OR a.{Schema.PropKey} = {Cypher.Str(from)} WITH a LIMIT 1 " +
            $"MATCH (b) WHERE b.{Schema.PropName} = {Cypher.Str(to)} OR b.{Schema.PropKey} = {Cypher.Str(to)} WITH a, b LIMIT 1 " +
            $"MERGE (a)-[r:{rel.ToUpperInvariant()}]->(b) SET r += {Cypher.Map(props)} " +
            $"RETURN a.{Schema.PropName} AS from, type(r) AS rel, b.{Schema.PropName} AS to");
    }

    /// <summary>意味エッジを一覧する。</summary>
    public QueryResult ListSemanticEdges() =>
        _client.ReadOnlyQuery(
            $"MATCH (a)-[r]->(b) WHERE r.{Schema.PropSource} = '{Schema.SourceSemantic}' " +
            $"RETURN type(r) AS rel, a.{Schema.PropName} AS from, b.{Schema.PropName} AS to, " +
            $"r.{Schema.PropConfidence} AS confidence, r.{Schema.PropRationale} AS rationale, r.{Schema.PropAuthor} AS author");

    /// <summary>生 Cypher を実行する（読み取り専用ではない）。</summary>
    public QueryResult Raw(string cypher) => _client.Query(cypher);

    private static IReadOnlyDictionary<string, object?> WithSource(IReadOnlyDictionary<string, object?> props, string source)
    {
        var dict = new Dictionary<string, object?>(props) { [Schema.PropSource] = source };
        return dict;
    }

    private static IEnumerable<List<T>> Chunk<T>(IEnumerable<T> items, int size)
    {
        var batch = new List<T>(size);
        foreach (var item in items)
        {
            batch.Add(item);
            if (batch.Count == size) { yield return batch; batch = new List<T>(size); }
        }
        if (batch.Count > 0) yield return batch;
    }
}

/// <summary>静的レイヤー再構築の件数。</summary>
public sealed record StaticLayerStats(int Nodes, int Edges);
