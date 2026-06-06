using Ukg.Core;
using Ukg.Extractors;
using Xunit;

namespace Ukg.Eval;

/// <summary>
/// FalkorDB を使う統合テスト。専用グラフ(ukg_eval)で完結し、未起動時はスキップ扱い（早期return）。
/// P0(ライフサイクル) / P1(検索) / P3(コミュニティ) / P4(意味エッジ保持・supersede) を通しで固定する。
/// </summary>
public sealed class IntegrationTests
{
    private const string Now = "2026-01-01T00:00:00.0000000Z";

    private static GraphClient? Connect()
    {
        try
        {
            var c = GraphClient.Connect(graph: "ukg_eval");
            c.DeleteGraph();                       // グラフ全体を破棄（索引も作り直す）
            c.Query("RETURN 1");                   // 接続確認
            return c;
        }
        catch { return null; }
    }

    private static IndexSummary Index(GraphRepository repo, IEmbedder e, bool communities = true, bool force = false) =>
        IndexPipeline.Run(Fixture.SampleProject(), repo, e, Now, communities, unityManifestPath: null, force: force);

    [Fact]
    public void FullPipeline_PopulatesGraph()
    {
        using var client = Connect();
        if (client is null) return; // DB未起動: スキップ
        var repo = new GraphRepository(client);
        var s = Index(repo, new HashingEmbedder());

        Assert.True(s.Nodes >= 11);
        Assert.True(s.Embedded > 0);
        Assert.True(s.Communities >= 1);

        // 主要エッジが存在する
        var inh = repo.Raw("MATCH (:Class {name:'PlayerController'})-[:INHERITS]->(:Class {name:'Character'}) RETURN count(*) AS c");
        Assert.Equal(1L, inh.Rows[0][0]);
        var calls = repo.Raw("MATCH (:Method {name:'Attack'})-[:CALLS]->(:Method) RETURN count(*) AS c");
        Assert.Equal(2L, calls.Rows[0][0]);
        var uc = repo.Raw("MATCH (:Class {name:'PlayerController'})-[:USES_COMPONENT]->(:Class {name:'Weapon'}) RETURN count(*) AS c");
        Assert.Equal(1L, uc.Rows[0][0]);
    }

    [Fact]
    public void Search_FindsByMeaning_NotExactName()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        Index(repo, e);

        // "damage" は型名に無いが IDamageable/TakeDamage に当たるべき
        var res = repo.Search(e, "take damage health", 5, null);
        Assert.NotEmpty(res.Rows);
        var names = res.Rows.Select(r => r[1]?.ToString()).ToList();
        Assert.Contains(names, n => n == "TakeDamage" || n == "IDamageable");
    }

    [Fact]
    public void Impact_IncludesCallAndComponentDependents()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        Index(repo, new HashingEmbedder());

        var res = repo.Impact("Character", 4);
        var keys = res.Rows.Select(r => r[2]?.ToString()).ToList();
        Assert.Contains("Game.PlayerController", keys); // INHERITS 経由の上流
    }

    [Fact]
    public void SemanticEdge_SurvivesReindex()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        Index(repo, e);

        repo.AddSemanticEdge("PlayerController", "Inventory", "COLLABORATES_WITH",
            0.8, "uses items", "test", Now, null, supersede: false);

        // 再インデックス（強制フル再構築）でも意味エッジは残る
        Index(repo, e, force: true);
        var ls = repo.ListSemanticEdges();
        Assert.Contains(ls.Rows, r =>
            r[0]?.ToString() == "COLLABORATES_WITH" &&
            r[1]?.ToString() == "PlayerController" && r[2]?.ToString() == "Inventory");
    }

    [Fact]
    public void Supersede_InvalidatesPriorEdge()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        Index(repo, e);

        repo.AddSemanticEdge("PlayerController", "Inventory", "COLLABORATES_WITH", 0.8, "v1", "test", Now, null, false);
        repo.AddSemanticEdge("PlayerController", "Inventory", "RESPONSIBLE_FOR", 0.9, "v2", "test", Now, null, supersede: true);

        var active = repo.ListSemanticEdges(includeInvalid: false).Rows
            .Where(r => r[1]?.ToString() == "PlayerController" && r[2]?.ToString() == "Inventory").ToList();
        Assert.DoesNotContain(active, r => r[0]?.ToString() == "COLLABORATES_WITH");
        Assert.Contains(active, r => r[0]?.ToString() == "RESPONSIBLE_FOR");

        var all = repo.ListSemanticEdges(includeInvalid: true).Rows
            .Where(r => r[1]?.ToString() == "PlayerController" && r[2]?.ToString() == "Inventory").ToList();
        Assert.Contains(all, r => r[0]?.ToString() == "COLLABORATES_WITH");
    }

    [Fact]
    public void Lifecycle_RemovesVanishedNode_ButProtectsSemanticAttached()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        Index(repo, e);

        // 抽出に存在しない static ノードを直接作る（消えたノードを模擬）
        repo.RawWrite("MERGE (n:Class {key:'Game.Ghost'}) SET n.source='static', n.name='Ghost'");
        repo.RawWrite("MERGE (n:Class {key:'Game.Protected'}) SET n.source='static', n.name='Protected'");
        // Protected には意味エッジを付ける（保護対象）
        repo.RawWrite("MATCH (a:Class {key:'Game.Protected'}), (b:Class {name:'Inventory'}) " +
                      "MERGE (a)-[r:RELATES_TO]->(b) SET r.source='semantic'");

        Index(repo, e, force: true); // 手動追加ノードはファイル差分に出ないので force で再構築

        var ghost = repo.Raw("MATCH (n:Class {key:'Game.Ghost'}) RETURN count(n) AS c");
        Assert.Equal(0L, ghost.Rows[0][0]); // semantic 接続なし → 削除

        var prot = repo.Raw("MATCH (n:Class {key:'Game.Protected'}) RETURN coalesce(n.stale,false) AS s");
        Assert.True(prot.Rows.Count == 1 && (bool)prot.Rows[0][0]!); // semantic 接続あり → stale で温存
    }

    [Fact]
    public void Embeddings_AreIncremental_OnReindex()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        Index(repo, e, communities: false);
        // 変更なしの再indexは丸ごとスキップされるため、埋め込みキャッシュ自体を見るには --force
        var second = Index(repo, e, communities: false, force: true);
        // 2回目は型ノードの埋め込みを再計算しない（内容ハッシュ一致）
        Assert.True(second.EmbeddingsSkipped > 0);
        Assert.Equal(0, second.Embedded);
    }
}
