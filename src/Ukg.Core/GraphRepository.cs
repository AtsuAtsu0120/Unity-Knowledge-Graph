namespace Ukg.Core;

/// <summary>
/// グラフへの高レベル操作。静的レイヤーの冪等な再構築（ライフサイクル管理付き）、
/// 意味検索、意味エッジの時間性つき操作、コミュニティ層を提供する。
/// 意味エッジ（source=semantic）は再インデックスで保持される。
/// </summary>
public sealed class GraphRepository
{
    private readonly GraphClient _client;

    public GraphRepository(GraphClient client) => _client = client;

    private static readonly string[] IndexedLabels =
    {
        Schema.Class, Schema.Interface, Schema.Struct, Schema.Enum,
        Schema.Namespace, Schema.Asset, Schema.Concept, Schema.Method
    };

    /// <summary>語彙あいまい検索（Candidates）の対象ラベル。</summary>
    private static readonly string[] CandidateLabels =
    {
        Schema.Class, Schema.Interface, Schema.Struct, Schema.Enum,
        Schema.Namespace, Schema.Asset, Schema.Concept, Schema.Method
    };

    /// <summary>各ラベルの key 索引と、埋め込みラベルのベクトル索引を作成する（既存なら無視）。</summary>
    public void EnsureIndexes(int embeddingDim)
    {
        foreach (var label in IndexedLabels)
            TryQuery($"CREATE INDEX FOR (n:{Cypher.Ident(label)}) ON (n.{Schema.PropKey})");

        foreach (var label in Schema.EmbeddableLabels)
            TryQuery($"CREATE VECTOR INDEX FOR (n:{Cypher.Ident(label)}) ON (n.{Schema.PropEmbedding}) " +
                     $"OPTIONS {{dimension: {embeddingDim}, similarityFunction: 'cosine'}}");
    }

    private void TryQuery(string cypher)
    {
        try { _client.Query(cypher); } catch { /* 既存などは無視 */ }
    }

    // ---- P0: 静的レイヤー再構築（ライフサイクル管理付き）----

    /// <summary>
    /// 抽出結果で静的レイヤーを再構築する。
    /// 1) 静的エッジ削除 → 2) ノード upsert → 3) 静的エッジ再生成 → 4) 消えたノードの掃除。
    /// source=semantic のエッジには触れない。semantic 接続のある孤児ノードは stale 化して温存（ADR-003）。
    /// </summary>
    public StaticLayerStats ApplyStaticLayer(ExtractionResult result, int embeddingDim)
    {
        EnsureIndexes(embeddingDim);

        // 1) 既存の静的エッジを削除（意味エッジは残す）
        _client.Query($"MATCH ()-[r]->() WHERE r.{Schema.PropSource} = '{Schema.SourceStatic}' DELETE r");

        // 2) ノードを upsert（ラベル単位の UNWIND バッチ・パラメータ化）
        int nodeCount = 0;
        foreach (var group in result.Nodes.GroupBy(n => n.Label))
        {
            Cypher.Ident(group.Key);
            foreach (var batch in Chunk(group, 500))
            {
                var rows = batch.Select(n => (object?)ToMap(WithSource(n.Props, Schema.SourceStatic)));
                _client.Query(
                    $"UNWIND {Cypher.List(rows)} AS row " +
                    $"MERGE (n:{group.Key} {{{Schema.PropKey}: row.{Schema.PropKey}}}) " +
                    $"SET n += row SET n.{Schema.PropStale} = null");
                nodeCount += batch.Count;
            }
        }

        // 3) 静的エッジを再生成（type×fromLabel×toLabel 単位の UNWIND バッチ）
        int edgeCount = 0;
        foreach (var group in result.Edges.GroupBy(e => (e.Type, e.FromLabel, e.ToLabel)))
        {
            Cypher.Ident(group.Key.Type); Cypher.Ident(group.Key.FromLabel); Cypher.Ident(group.Key.ToLabel);
            foreach (var batch in Chunk(group, 500))
            {
                var rows = batch.Select(e =>
                {
                    var props = ToMap(WithSource(e.Props ?? new Dictionary<string, object?>(), Schema.SourceStatic));
                    return (object?)new Dictionary<string, object?> { ["f"] = e.FromKey, ["t"] = e.ToKey, ["p"] = props };
                });
                _client.Query(
                    $"UNWIND {Cypher.List(rows)} AS row " +
                    $"MATCH (a:{group.Key.FromLabel} {{{Schema.PropKey}: row.f}}) " +
                    $"MATCH (b:{group.Key.ToLabel} {{{Schema.PropKey}: row.t}}) " +
                    $"MERGE (a)-[r:{group.Key.Type}]->(b) SET r += row.p");
                edgeCount += batch.Count;
            }
        }

        // 4) ライフサイクル: 今回の抽出に現れなかった静的ノードを掃除する
        int removed = 0, staled = 0;
        var keysByLabel = result.Nodes.GroupBy(n => n.Label)
            .ToDictionary(g => g.Key, g => g.Select(n => (object?)n.Key).ToList());
        foreach (var label in IndexedLabels)
        {
            if (label == Schema.Concept) continue; // Concept は semantic 資産なので掃除対象外
            var keys = keysByLabel.TryGetValue(label, out var k) ? k : new List<object?>();
            var keyList = Cypher.List(keys);

            // 4a) 消えたが semantic 接続あり → stale 化して温存
            var s = _client.Query(
                $"MATCH (n:{label}) WHERE n.{Schema.PropSource} = '{Schema.SourceStatic}' " +
                $"AND NOT n.{Schema.PropKey} IN {keyList} AND (n)-[{{{Schema.PropSource}: '{Schema.SourceSemantic}'}}]-() " +
                $"SET n.{Schema.PropStale} = true RETURN count(n) AS c");
            staled += (int)FirstLong(s);

            // 4b) 消えて semantic 接続なし → 物理削除
            var d = _client.Query(
                $"MATCH (n:{label}) WHERE n.{Schema.PropSource} = '{Schema.SourceStatic}' " +
                $"AND NOT n.{Schema.PropKey} IN {keyList} AND NOT (n)-[{{{Schema.PropSource}: '{Schema.SourceSemantic}'}}]-() " +
                $"WITH n LIMIT 100000 DETACH DELETE n");
            removed += DeletedNodes(d);
        }

        return new StaticLayerStats(nodeCount, edgeCount, removed, staled);
    }

    // ---- P1: 埋め込み（意味検索）----

    /// <summary>
    /// 埋め込み対象ノードのうち embedText が変化したものだけを再埋め込みする（内容ハッシュ差分）。
    /// 返り値は (embedded, skipped)。
    /// </summary>
    public EmbeddingStats ApplyEmbeddings(IEmbedder embedder)
    {
        int embedded = 0, skipped = 0;
        foreach (var label in Schema.EmbeddableLabels)
        {
            var res = _client.ReadOnlyQuery(
                $"MATCH (n:{label}) WHERE n.{Schema.PropEmbedText} IS NOT NULL " +
                $"RETURN n.{Schema.PropKey} AS key, n.{Schema.PropEmbedText} AS text, n.{Schema.PropEmbedHash} AS hash");

            var pending = new List<(string Key, string Text, string Hash)>();
            foreach (var row in res.Rows)
            {
                var key = row[0]?.ToString();
                var text = row[1]?.ToString();
                if (key is null || text is null) continue;
                var hash = Hashing.Sha1(text);
                if (string.Equals(row[2]?.ToString(), hash, StringComparison.Ordinal)) { skipped++; continue; }
                pending.Add((key, text, hash));
            }

            foreach (var batch in Chunk(pending, 200))
            {
                var vecs = embedder.EmbedBatch(batch.Select(p => p.Text).ToList());
                var rows = batch.Select((p, i) => (object?)new Dictionary<string, object?>
                {
                    ["k"] = p.Key,
                    ["e"] = new Vec(vecs[i]),
                    ["h"] = p.Hash,
                });
                _client.Query(
                    $"UNWIND {Cypher.List(rows)} AS row MATCH (n:{label} {{{Schema.PropKey}: row.k}}) " +
                    $"SET n.{Schema.PropEmbedding} = row.e, n.{Schema.PropEmbedHash} = row.h");
                embedded += batch.Count;
            }
        }
        return new EmbeddingStats(embedded, skipped);
    }

    /// <summary>全ノードの埋め込みと埋め込みハッシュを除去する（埋め込み器切替時, ADR-010）。</summary>
    public void ResetEmbeddings() =>
        _client.Query(
            $"MATCH (n) WHERE n.{Schema.PropEmbedding} IS NOT NULL OR n.{Schema.PropEmbedHash} IS NOT NULL " +
            $"REMOVE n.{Schema.PropEmbedding}, n.{Schema.PropEmbedHash}");

    /// <summary>ベクトル索引を破棄する（次元変更に伴う作り直しの前段, ADR-010）。</summary>
    public void DropVectorIndexes()
    {
        foreach (var label in Schema.EmbeddableLabels)
            TryQuery($"DROP VECTOR INDEX FOR (n:{Cypher.Ident(label)}) ON (n.{Schema.PropEmbedding})");
    }

    /// <summary>意味検索。各埋め込みラベルでベクトル近傍を取り、全体で上位 k を返す。</summary>
    public QueryResult Search(IEmbedder embedder, string queryText, int k, string? labelFilter)
    {
        var qvec = Cypher.Vecf32(embedder.Embed(queryText));
        var labels = labelFilter is not null
            ? new[] { labelFilter }.Where(l => Schema.EmbeddableLabels.Contains(l, StringComparer.OrdinalIgnoreCase)).ToArray()
            : Schema.EmbeddableLabels;

        var hits = new List<object?[]>();
        foreach (var label in labels)
        {
            QueryResult res;
            try
            {
                res = _client.ReadOnlyQuery(
                    $"CALL db.idx.vector.queryNodes('{Cypher.Ident(label)}', '{Schema.PropEmbedding}', {k}, {qvec}) " +
                    $"YIELD node, score " +
                    $"WHERE coalesce(node.{Schema.PropStale}, false) = false " +
                    $"RETURN '{label}' AS label, node.{Schema.PropName} AS name, node.{Schema.PropKey} AS key, " +
                    $"node.{Schema.PropPath} AS path, score AS distance");
            }
            catch { continue; } // 索引未作成ラベルはスキップ
            hits.AddRange(res.Rows);
        }
        // distance 昇順で全体上位 k
        var ordered = hits
            .OrderBy(r => r.Length > 4 && r[4] is double d ? d : double.MaxValue)
            .Take(k).ToList();
        return new QueryResult(new[] { "label", "name", "key", "path", "distance" }, ordered, Array.Empty<string>());
    }

    /// <summary>
    /// 語彙/構造のあいまい検索（grep の代替・トークン不要）。クエリをトークン分割し、
    /// <c>name/key/doc/summary/signature</c> をグラフ側で部分一致スコアリングする。ファイルは一切読まない。
    /// 返り値 columns: label, name, key, path, score（score 降順, 0..3 程度）。
    /// </summary>
    public QueryResult Candidates(string query, int k, string? labelFilter)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var tokens = Tokenize(query ?? "");
        if (tokens.Count == 0 && q.Length > 0) tokens.Add(q);
        if (tokens.Count == 0)
            return new QueryResult(new[] { "label", "name", "key", "path", "score" }, new List<object?[]>(), Array.Empty<string>());

        var labels = labelFilter is not null
            ? CandidateLabels.Where(l => l.Equals(labelFilter, StringComparison.OrdinalIgnoreCase)).ToArray()
            : CandidateLabels;
        if (labels.Length == 0) labels = CandidateLabels;

        var p = new Dictionary<string, object?>
        {
            ["q"] = q,
            ["toks"] = tokens.Select(t => (object?)t).ToList(),
            ["labels"] = labels.Select(l => (object?)l).ToList(),
            ["k"] = k,
        };

        // score = トークン一致率 + 名前の完全一致/部分一致/前方一致ボーナス
        return _client.ReadOnlyQuery(
            $"MATCH (n) WHERE any(l IN labels(n) WHERE l IN $labels) " +
            $"AND coalesce(n.{Schema.PropStale}, false) = false " +
            $"WITH n, toLower(coalesce(n.{Schema.PropName}, '')) AS lname, " +
            $"toLower(coalesce(n.{Schema.PropName}, '') + ' ' + coalesce(n.{Schema.PropKey}, '') + ' ' + " +
            $"coalesce(n.{Schema.PropDoc}, '') + ' ' + coalesce(n.{Schema.PropSummary}, '') + ' ' + " +
            $"coalesce(n.{Schema.PropSignature}, '')) AS hay " +
            $"WITH n, lname, reduce(s = 0, t IN $toks | s + CASE WHEN hay CONTAINS t THEN 1 ELSE 0 END) AS hits " +
            $"WHERE hits > 0 OR lname CONTAINS $q " +
            $"WITH n, lname, " +
            $"toFloat(hits) / size($toks) " +
            $"+ CASE WHEN lname = $q THEN 1.0 ELSE 0.0 END " +
            $"+ CASE WHEN lname CONTAINS $q THEN 0.5 ELSE 0.0 END " +
            $"+ CASE WHEN lname STARTS WITH $q THEN 0.25 ELSE 0.0 END AS score " +
            $"RETURN labels(n)[0] AS label, n.{Schema.PropName} AS name, n.{Schema.PropKey} AS key, " +
            $"n.{Schema.PropPath} AS path, score " +
            $"ORDER BY score DESC LIMIT $k", p);
    }

    /// <summary>クエリ文字列をトークン分割する（区切り文字＋camelCase 境界, 2文字以上, 小文字化, 重複排除）。</summary>
    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        void Flush() { if (cur.Length > 0) { tokens.Add(cur.ToString().ToLowerInvariant()); cur.Clear(); } }
        char prev = '\0';
        foreach (var ch in s)
        {
            if (!char.IsLetterOrDigit(ch)) { Flush(); prev = ch; continue; }
            if (cur.Length > 0 && char.IsUpper(ch) && char.IsLower(prev)) Flush(); // camelCase 境界
            cur.Append(ch);
            prev = ch;
        }
        Flush();
        return tokens.Where(t => t.Length >= 2).Distinct(StringComparer.Ordinal).ToList();
    }

    // ---- 定型クエリ ----

    public QueryResult FindByName(string nameOrKey) =>
        _client.ReadOnlyQuery(
            $"MATCH (n) WHERE n.{Schema.PropName} = $q OR n.{Schema.PropKey} = $q " +
            $"RETURN labels(n)[0] AS label, n.{Schema.PropName} AS name, n.{Schema.PropKey} AS key, " +
            $"n.{Schema.PropPath} AS path, n.{Schema.PropSource} AS source, " +
            $"coalesce(n.{Schema.PropStale}, false) AS stale LIMIT 100",
            new Dictionary<string, object?> { ["q"] = nameOrKey });

    public QueryResult Neighbors(string nameOrKey) =>
        _client.ReadOnlyQuery(
            $"MATCH (n) WHERE n.{Schema.PropName} = $q OR n.{Schema.PropKey} = $q " +
            $"MATCH (n)-[r]-(m) " +
            $"RETURN type(r) AS rel, " +
            $"CASE WHEN startNode(r) = n THEN 'out' ELSE 'in' END AS dir, " +
            $"labels(m)[0] AS label, m.{Schema.PropName} AS name, m.{Schema.PropKey} AS key, " +
            $"r.{Schema.PropSource} AS source, r.{Schema.PropConfidence} AS confidence " +
            $"ORDER BY source, rel LIMIT 200",
            new Dictionary<string, object?> { ["q"] = nameOrKey });

    public QueryResult Deps(string assetPathOrGuid) =>
        _client.ReadOnlyQuery(
            $"MATCH (a:{Schema.Asset}) WHERE a.{Schema.PropPath} = $q " +
            $"OR a.{Schema.PropGuid} = $q OR a.{Schema.PropName} = $q " +
            $"MATCH (a)-[r:{Schema.DependsOn}|{Schema.ScriptOf}]->(m) " +
            $"RETURN type(r) AS rel, labels(m)[0] AS label, m.{Schema.PropName} AS name, " +
            $"m.{Schema.PropKey} AS key, m.{Schema.PropPath} AS path LIMIT 200",
            new Dictionary<string, object?> { ["q"] = assetPathOrGuid });

    /// <summary>このノードに（推移的に）依存している上流ノード＝変更時の影響範囲。</summary>
    public QueryResult Impact(string nameOrKey, int depth)
    {
        var rels = string.Join("|", Schema.ImpactRelations);
        depth = Math.Clamp(depth, 1, 8);
        return _client.ReadOnlyQuery(
            $"MATCH (n) WHERE n.{Schema.PropName} = $q OR n.{Schema.PropKey} = $q " +
            $"MATCH (m)-[:{rels}*1..{depth}]->(n) WHERE m <> n " +
            $"RETURN DISTINCT labels(m)[0] AS label, m.{Schema.PropName} AS name, m.{Schema.PropKey} AS key, " +
            $"coalesce(m.{Schema.PropStale}, false) AS stale LIMIT 300",
            new Dictionary<string, object?> { ["q"] = nameOrKey });
    }

    // ---- 構造的根拠（curation 誤りの予防, ADR-014）----

    /// <summary>
    /// 2ノードが静的エッジ（ImpactRelations, 名前空間は除外）で <paramref name="maxHops"/> ホップ以内に
    /// 繋がっているか。最短ホップ数を返す（繋がっていなければ null）。意味エッジの構造的裏付け判定に使う。
    /// </summary>
    public int? StructuralBasis(string from, string to, int maxHops)
    {
        var rels = string.Join("|", Schema.ImpactRelations);
        maxHops = Math.Clamp(maxHops, 1, 6);
        var r = _client.ReadOnlyQuery(
            $"MATCH (a) WHERE a.{Schema.PropName} = $from OR a.{Schema.PropKey} = $from WITH a LIMIT 1 " +
            $"MATCH (b) WHERE b.{Schema.PropName} = $to OR b.{Schema.PropKey} = $to WITH a, b LIMIT 1 " +
            $"OPTIONAL MATCH p = (a)-[:{rels}*1..{maxHops}]-(b) " +
            $"RETURN min(length(p)) AS hops",
            new Dictionary<string, object?> { ["from"] = from, ["to"] = to });
        if (r.Rows.Count == 0) return null;
        return r.Rows[0][0] switch { long h => (int)h, int n => n, _ => null };
    }

    /// <summary>名前/キーが Concept ノードに解決されるか（構造的根拠の対象外＝概念は静的エッジを持たない）。</summary>
    private bool ResolvesToConcept(string nameOrKey)
    {
        var r = _client.ReadOnlyQuery(
            $"MATCH (n) WHERE n.{Schema.PropName} = $q OR n.{Schema.PropKey} = $q RETURN labels(n)[0] AS l LIMIT 1",
            new Dictionary<string, object?> { ["q"] = nameOrKey });
        return r.Rows.Count > 0 && r.Rows[0][0]?.ToString() == Schema.Concept;
    }

    // ---- P3/P4: 意味エッジ・Concept ----

    /// <summary>
    /// 意味エッジを追加する（source=semantic, 時間性つき）。
    /// <paramref name="requireBasis"/>=true の時、両端が型ノードなら構造的根拠（<paramref name="basisHops"/>
    /// ホップ以内の静的接続）を要求し、無ければ拒否する（誤った sem add の予防, ADR-014）。
    /// 概念ノードが絡む辺は静的エッジを持たないため根拠チェックの対象外。
    /// </summary>
    public QueryResult AddSemanticEdge(
        string from, string to, string rel, double? confidence, string? why, string author,
        string nowIso, string? commitSha, bool supersede,
        bool requireBasis = false, int basisHops = 3)
    {
        if (!Schema.IsSemanticRelation(rel))
            throw new ArgumentException(
                $"'{rel}' は意味エッジではありません。許可: {string.Join(", ", Schema.SemanticRelations)}");
        rel = rel.ToUpperInvariant();
        Cypher.Ident(rel);

        if (requireBasis && !ResolvesToConcept(from) && !ResolvesToConcept(to)
            && StructuralBasis(from, to, basisHops) is null)
            throw new ArgumentException(
                $"構造的根拠なし: '{from}' と '{to}' は静的エッジで {basisHops} ホップ以内に繋がっていません。" +
                $"誤った意味エッジの可能性が高いため拒否しました（--basis-hops で緩和可）。");

        var p = new Dictionary<string, object?> { ["from"] = from, ["to"] = to, ["now"] = nowIso };

        // supersede: 同じ (from,to) 間の有効な意味エッジを論理無効化してから張る
        if (supersede)
            _client.Query(
                $"MATCH (a)-[r]->(b) WHERE (a.{Schema.PropName} = $from OR a.{Schema.PropKey} = $from) " +
                $"AND (b.{Schema.PropName} = $to OR b.{Schema.PropKey} = $to) " +
                $"AND r.{Schema.PropSource} = '{Schema.SourceSemantic}' AND r.{Schema.PropInvalidAt} IS NULL " +
                $"SET r.{Schema.PropInvalidAt} = $now", p);

        var props = new Dictionary<string, object?>
        {
            [Schema.PropSource] = Schema.SourceSemantic,
            [Schema.PropAuthor] = author,
            [Schema.PropCreatedAt] = nowIso,
            [Schema.PropValidFrom] = nowIso,
            [Schema.PropInvalidAt] = null,
        };
        if (confidence is not null) props[Schema.PropConfidence] = confidence;
        if (!string.IsNullOrEmpty(why)) props[Schema.PropRationale] = why;
        if (!string.IsNullOrEmpty(commitSha)) props[Schema.PropCommitSha] = commitSha;

        return _client.Query(
            $"MATCH (a) WHERE a.{Schema.PropName} = $from OR a.{Schema.PropKey} = $from WITH a LIMIT 1 " +
            $"MATCH (b) WHERE b.{Schema.PropName} = $to OR b.{Schema.PropKey} = $to WITH a, b LIMIT 1 " +
            $"MERGE (a)-[r:{rel}]->(b) SET r += {Cypher.Map(ToMap(props))} " +
            $"RETURN a.{Schema.PropName} AS from, type(r) AS rel, b.{Schema.PropName} AS to", p);
    }

    /// <summary>意味エッジを一覧する（既定は有効なもののみ）。</summary>
    public QueryResult ListSemanticEdges(bool includeInvalid = false)
    {
        var filter = includeInvalid ? "" : $"AND r.{Schema.PropInvalidAt} IS NULL ";
        return _client.ReadOnlyQuery(
            $"MATCH (a)-[r]->(b) WHERE r.{Schema.PropSource} = '{Schema.SourceSemantic}' {filter}" +
            $"RETURN type(r) AS rel, a.{Schema.PropName} AS from, b.{Schema.PropName} AS to, " +
            $"r.{Schema.PropConfidence} AS confidence, r.{Schema.PropRationale} AS rationale, " +
            $"r.{Schema.PropAuthor} AS author, r.{Schema.PropCreatedAt} AS createdAt, " +
            $"r.{Schema.PropInvalidAt} AS invalidAt " +
            $"ORDER BY confidence DESC");
    }

    /// <summary>Concept ノードを新規作成/更新する（source=semantic）。LLM が抽象概念を立てるための入口。</summary>
    public QueryResult AddConcept(string name, string? summary, string author, string nowIso)
    {
        var props = new Dictionary<string, object?>
        {
            [Schema.PropName] = name,
            [Schema.PropSource] = Schema.SourceSemantic,
            [Schema.PropAuthor] = author,
            [Schema.PropCreatedAt] = nowIso,
        };
        if (!string.IsNullOrEmpty(summary))
        {
            props[Schema.PropSummary] = summary;
            props[Schema.PropEmbedText] = $"{name}. {summary}";
        }
        else props[Schema.PropEmbedText] = name;

        return _client.Query(
            $"MERGE (c:{Schema.Concept} {{{Schema.PropKey}: $key}}) SET c += {Cypher.Map(ToMap(props))} " +
            $"RETURN c.{Schema.PropName} AS name, c.{Schema.PropKey} AS key",
            new Dictionary<string, object?> { ["key"] = "concept:" + name });
    }

    // ---- ライブ更新（ADR-009）: 索引状態・stale伝播・レビュー・リフレクション ----

    /// <summary>索引状態(JSON)を UkgMeta ノードに保存する。</summary>
    public void SaveIndexState(string stateJson, string nowIso) =>
        _client.Query(
            $"MERGE (m:{Schema.Meta} {{{Schema.PropKey}: 'index'}}) " +
            $"SET m.{Schema.PropState} = $state, m.indexedAt = $now",
            new Dictionary<string, object?> { ["state"] = stateJson, ["now"] = nowIso });

    /// <summary>保存済み索引状態(JSON)を取り出す（無ければ null）。</summary>
    public string? LoadIndexState()
    {
        var r = _client.ReadOnlyQuery(
            $"MATCH (m:{Schema.Meta} {{{Schema.PropKey}: 'index'}}) RETURN m.{Schema.PropState} AS s");
        return r.Rows.Count > 0 ? r.Rows[0][0]?.ToString() : null;
    }

    /// <summary>変更された型キーに係る LLM 由来の意味エッジを needsReview 化する（stale伝播）。</summary>
    public int FlagSemanticReview(IReadOnlyCollection<string> changedKeys, string reason)
    {
        if (changedKeys.Count == 0) return 0;
        var keys = Cypher.List(changedKeys.Select(k => (object?)k));
        var r = _client.Query(
            $"UNWIND {keys} AS k MATCH (n {{{Schema.PropKey}: k}})-[r {{{Schema.PropSource}: '{Schema.SourceSemantic}'}}]-(m) " +
            $"WHERE coalesce(r.{Schema.PropAuthor}, '') <> 'community-detection' AND r.{Schema.PropInvalidAt} IS NULL " +
            $"SET r.{Schema.PropNeedsReview} = true, r.{Schema.PropReviewReason} = $reason " +
            $"RETURN count(r) AS c",
            new Dictionary<string, object?> { ["reason"] = reason });
        return (int)FirstLong(r);
    }

    /// <summary>要再確認の意味エッジ（needsReview もしくは端点が stale）を返す。</summary>
    public QueryResult ReviewQueue() =>
        _client.ReadOnlyQuery(
            $"MATCH (a)-[r]->(b) WHERE r.{Schema.PropSource} = '{Schema.SourceSemantic}' AND r.{Schema.PropInvalidAt} IS NULL " +
            $"AND (coalesce(r.{Schema.PropNeedsReview}, false) = true OR coalesce(a.{Schema.PropStale}, false) = true " +
            $"OR coalesce(b.{Schema.PropStale}, false) = true) " +
            $"RETURN type(r) AS rel, a.{Schema.PropName} AS from, b.{Schema.PropName} AS to, " +
            $"r.{Schema.PropConfidence} AS confidence, r.{Schema.PropRationale} AS rationale, " +
            $"coalesce(r.{Schema.PropReviewReason}, 'endpoint stale') AS reason");

    /// <summary>意味エッジの再確認フラグを解除する（needsReview を消し confirmedAt を打つ）。</summary>
    public QueryResult ConfirmSemanticEdge(string from, string to, string rel, string nowIso)
    {
        rel = rel.ToUpperInvariant();
        Cypher.Ident(rel);
        return _client.Query(
            $"MATCH (a)-[r:{rel}]->(b) WHERE (a.{Schema.PropName} = $from OR a.{Schema.PropKey} = $from) " +
            $"AND (b.{Schema.PropName} = $to OR b.{Schema.PropKey} = $to) AND r.{Schema.PropSource} = '{Schema.SourceSemantic}' " +
            $"SET r.{Schema.PropNeedsReview} = null, r.{Schema.PropReviewReason} = null, r.{Schema.PropConfirmedAt} = $now " +
            $"RETURN a.{Schema.PropName} AS from, type(r) AS rel, b.{Schema.PropName} AS to",
            new Dictionary<string, object?> { ["from"] = from, ["to"] = to, ["now"] = nowIso });
    }

    /// <summary>低 confidence の意味エッジ（再確認候補）。</summary>
    public QueryResult LowConfidenceEdges(double threshold) =>
        _client.ReadOnlyQuery(
            $"MATCH (a)-[r]->(b) WHERE r.{Schema.PropSource} = '{Schema.SourceSemantic}' AND r.{Schema.PropInvalidAt} IS NULL " +
            $"AND coalesce(r.{Schema.PropAuthor}, '') <> 'community-detection' AND r.{Schema.PropConfidence} < {threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"RETURN type(r) AS rel, a.{Schema.PropName} AS from, b.{Schema.PropName} AS to, r.{Schema.PropConfidence} AS confidence " +
            $"ORDER BY confidence ASC LIMIT 100");

    /// <summary>stale（消えたが温存された）ノード一覧。</summary>
    public QueryResult StaleNodes() =>
        _client.ReadOnlyQuery(
            $"MATCH (n) WHERE coalesce(n.{Schema.PropStale}, false) = true " +
            $"RETURN labels(n)[0] AS label, n.{Schema.PropName} AS name, n.{Schema.PropKey} AS key LIMIT 100");

    /// <summary>名前が重複している Concept（統合候補）。</summary>
    public QueryResult DuplicateConcepts() =>
        _client.ReadOnlyQuery(
            $"MATCH (c:{Schema.Concept}) WITH toLower(c.{Schema.PropName}) AS n, collect(c.{Schema.PropKey}) AS keys, count(*) AS cnt " +
            $"WHERE cnt > 1 RETURN n AS name, cnt AS count, keys");

    /// <summary>自動生成のままで未整理のコミュニティ Concept（人手で命名/集約する候補）。</summary>
    public QueryResult UncuratedCommunities() =>
        _client.ReadOnlyQuery(
            $"MATCH (c:{Schema.Concept}) WHERE c.{Schema.PropKey} STARTS WITH 'community:' " +
            $"RETURN c.{Schema.PropName} AS name, c.{Schema.PropMembers} AS members, c.{Schema.PropSummary} AS summary " +
            $"ORDER BY members DESC LIMIT 50");

    // ---- 増築（curation の成長）: クエリミス・ログ ＋ カバレッジギャップ ----

    /// <summary>
    /// candidates が none/low だったクエリを記録する（需要シグナル）。同一クエリは count を加算。
    /// 「実際に聞かれたのに答えられなかった問い」＝意味づけを増やすべき場所の地図になる。
    /// </summary>
    public void LogMiss(string query, string confidence, string nowIso)
    {
        var key = (query ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) return;
        _client.Query(
            $"MERGE (q:{Schema.QueryLog} {{{Schema.PropKey}: $key}}) " +
            $"SET q.{Schema.PropQuery} = $query, " +
            $"q.{Schema.PropCount} = coalesce(q.{Schema.PropCount}, 0) + 1, " +
            $"q.{Schema.PropConfidence} = $conf, " +
            $"q.{Schema.PropLastSeen} = $now, " +
            $"q.{Schema.PropCreatedAt} = coalesce(q.{Schema.PropCreatedAt}, $now)",
            new Dictionary<string, object?> { ["key"] = key, ["query"] = query, ["conf"] = confidence, ["now"] = nowIso });
    }

    /// <summary>答えられなかったクエリを頻度順に返す（増築の優先度＝需要）。</summary>
    public QueryResult MissedQueries(int limit) =>
        _client.ReadOnlyQuery(
            $"MATCH (q:{Schema.QueryLog}) " +
            $"RETURN q.{Schema.PropQuery} AS query, q.{Schema.PropCount} AS count, " +
            $"q.{Schema.PropConfidence} AS lastConfidence, q.{Schema.PropLastSeen} AS lastSeen " +
            $"ORDER BY count DESC LIMIT {Math.Clamp(limit, 1, 200)}");

    /// <summary>
    /// 中心的（構造的に高次数）なのに意味エッジが1本も無い「型」＝「重要だが薄い」増築候補。
    /// curation の単位は型/サブシステムなのでメソッドは除外。<paramref name="excludeKeySubstr"/> で
    /// vendored ライブラリ（例: "SQLite"）を key 部分一致で除外できる。
    /// </summary>
    public QueryResult UncoveredHubs(int limit, string? excludeKeySubstr = null)
    {
        var labels = string.Join(" OR ", new[] { Schema.Class, Schema.Interface, Schema.Struct, Schema.Enum }
            .Select(l => $"labels(n)[0] = '{l}'"));
        var exclude = string.IsNullOrEmpty(excludeKeySubstr)
            ? ""
            : $"AND NOT n.{Schema.PropKey} CONTAINS $ex ";
        // 「意味づけ済み」はLLM/人が張った意味エッジで判定する（community-detection の自動エッジは除外）。
        return _client.ReadOnlyQuery(
            $"MATCH (n) WHERE ({labels}) AND coalesce(n.{Schema.PropStale}, false) = false {exclude}" +
            $"MATCH (n)-[r {{{Schema.PropSource}: '{Schema.SourceStatic}'}}]-() " +
            $"WITH n, count(r) AS deg " +
            $"OPTIONAL MATCH (n)-[rs {{{Schema.PropSource}: '{Schema.SourceSemantic}'}}]-() " +
            $"WHERE coalesce(rs.{Schema.PropAuthor}, '') <> 'community-detection' " +
            $"WITH n, deg, count(rs) AS sem WHERE sem = 0 " +
            $"RETURN labels(n)[0] AS label, n.{Schema.PropName} AS name, n.{Schema.PropKey} AS key, deg " +
            $"ORDER BY deg DESC LIMIT {Math.Clamp(limit, 1, 200)}",
            excludeKeySubstr is null ? null : new Dictionary<string, object?> { ["ex"] = excludeKeySubstr });
    }

    /// <summary>生 Cypher を読み取り専用で実行する（書き込みは拒否される, P0）。</summary>
    public QueryResult Raw(string cypher) => _client.ReadOnlyQuery(cypher);

    /// <summary>生 Cypher を書き込み込みで実行する（明示要求時のみ）。</summary>
    public QueryResult RawWrite(string cypher) => _client.Query(cypher);

    // ---- P3: コミュニティ ----

    /// <summary>構造グラフの隣接を取り出す（型ノードと構造エッジ）。コミュニティ検出の入力。</summary>
    public (List<(string Key, string Label, string Name)> Nodes, List<(string A, string B)> Edges) StructuralGraph()
    {
        var typeLabels = new[] { Schema.Class, Schema.Interface, Schema.Struct, Schema.Enum };
        var labelPred = string.Join(" OR ", typeLabels.Select(l => $"labels(n)[0] = '{l}'"));

        var nres = _client.ReadOnlyQuery(
            $"MATCH (n) WHERE ({labelPred}) AND coalesce(n.{Schema.PropStale}, false) = false " +
            $"RETURN n.{Schema.PropKey} AS key, labels(n)[0] AS label, n.{Schema.PropName} AS name");
        var nodes = nres.Rows
            .Select(r => (r[0]?.ToString() ?? "", r[1]?.ToString() ?? "", r[2]?.ToString() ?? ""))
            .Where(t => t.Item1.Length > 0).ToList();

        var rels = string.Join("|", new[] { Schema.References, Schema.Inherits, Schema.Implements, Schema.Calls, Schema.UsesComponent });
        var eres = _client.ReadOnlyQuery(
            $"MATCH (a)-[r:{rels}]->(b) WHERE a.{Schema.PropKey} IS NOT NULL AND b.{Schema.PropKey} IS NOT NULL " +
            $"RETURN a.{Schema.PropKey} AS a, b.{Schema.PropKey} AS b");
        var edges = eres.Rows
            .Select(r => (r[0]?.ToString() ?? "", r[1]?.ToString() ?? ""))
            .Where(t => t.Item1.Length > 0 && t.Item2.Length > 0).ToList();

        return (nodes, edges);
    }

    /// <summary>コミュニティ(Concept)ノードを作り、構成メンバを PART_OF で束ねる。既存の自動生成分は作り直す。</summary>
    public int WriteCommunities(IReadOnlyList<CommunityResult> communities, string nowIso)
    {
        // 自動生成コミュニティ（key プレフィックス community:）と PART_OF を一旦掃除
        _client.Query(
            $"MATCH (c:{Schema.Concept}) WHERE c.{Schema.PropKey} STARTS WITH 'community:' DETACH DELETE c");

        int written = 0;
        foreach (var c in communities)
        {
            var key = "community:" + c.Id;
            var props = new Dictionary<string, object?>
            {
                [Schema.PropName] = c.Name,
                [Schema.PropSource] = Schema.SourceSemantic,
                [Schema.PropSummary] = c.Summary,
                [Schema.PropEmbedText] = $"{c.Name}. {c.Summary}",
                [Schema.PropCommunity] = c.Id,
                [Schema.PropMembers] = c.MemberKeys.Count,
                [Schema.PropCreatedAt] = nowIso,
                [Schema.PropAuthor] = "community-detection",
            };
            _client.Query(
                $"MERGE (c:{Schema.Concept} {{{Schema.PropKey}: $key}}) SET c += {Cypher.Map(ToMap(props))}",
                new Dictionary<string, object?> { ["key"] = key });

            var members = Cypher.List(c.MemberKeys.Select(m => (object?)m));
            _client.Query(
                $"MATCH (c:{Schema.Concept} {{{Schema.PropKey}: $key}}) " +
                $"UNWIND {members} AS mk MATCH (n {{{Schema.PropKey}: mk}}) " +
                $"MERGE (n)-[r:{Schema.PartOf}]->(c) " +
                $"SET r.{Schema.PropSource} = '{Schema.SourceSemantic}', r.{Schema.PropAuthor} = 'community-detection'",
                new Dictionary<string, object?> { ["key"] = key });
            written++;
        }
        return written;
    }

    // ---- helpers ----

    private static IReadOnlyDictionary<string, object?> WithSource(IReadOnlyDictionary<string, object?> props, string source)
    {
        var dict = new Dictionary<string, object?>(props) { [Schema.PropSource] = source };
        return dict;
    }

    /// <summary>null 値を除いたマップ（MERGE で誤って null セットしないため）。</summary>
    private static Dictionary<string, object?> ToMap(IReadOnlyDictionary<string, object?> props)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (k, v) in props) if (v is not null) dict[k] = v;
        return dict;
    }

    private static long FirstLong(QueryResult r) =>
        r.Rows.Count > 0 && r.Rows[0].Length > 0 && r.Rows[0][0] is long l ? l : 0;

    private static int DeletedNodes(QueryResult r) =>
        r.Statistics.Select(s => s).FirstOrDefault(s => s.Contains("Nodes deleted")) is { } stat
        && int.TryParse(new string(stat.Where(char.IsDigit).ToArray()), out var n) ? n : 0;

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
public sealed record StaticLayerStats(int Nodes, int Edges, int Removed, int Staled);

/// <summary>埋め込み適用の件数。</summary>
public sealed record EmbeddingStats(int Embedded, int Skipped);

/// <summary>コミュニティ検出の結果（1コミュニティ）。</summary>
public sealed record CommunityResult(string Id, string Name, string Summary, IReadOnlyList<string> MemberKeys);
