using Ukg.Core;
using Ukg.Extractors;
using Xunit;

namespace Ukg.Eval;

/// <summary>
/// ライブ更新（ADR-009）の統合テスト。専用グラフ(ukg_eval2)で完結し、未起動時はスキップ。
/// 増分no-op / 索引状態 / stale伝播 / レビュー / リフレクションを固定する。
/// </summary>
public sealed class LiveUpdateTests
{
    private const string Now = "2026-01-01T00:00:00.0000000Z";

    private static GraphClient? Connect()
    {
        try
        {
            var c = GraphClient.Connect(graph: "ukg_eval2");
            c.DeleteGraph();
            c.Query("RETURN 1");
            return c;
        }
        catch { return null; }
    }

    private static IndexSummary Index(GraphRepository repo, IEmbedder e, bool force = false) =>
        IndexPipeline.Run(Fixture.SampleProject(), repo, e, Now, communities: false, unityManifestPath: null, force: force);

    [Fact]
    public void Reindex_Unchanged_IsSkipped()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();

        var first = Index(repo, e);
        Assert.False(first.UpToDate);

        var second = Index(repo, e);
        Assert.True(second.UpToDate); // 変更なし → スキップ

        var forced = Index(repo, e, force: true);
        Assert.False(forced.UpToDate); // --force は走る
    }

    [Fact]
    public void IndexState_RoundTripsThroughGraph()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        Index(repo, new HashingEmbedder());

        var json = repo.LoadIndexState();
        Assert.NotNull(json);
        var state = IndexState.FromJson(json);
        Assert.NotNull(state);
        Assert.Contains(state!.Files.Keys, k => k.EndsWith("PlayerController.cs"));
    }

    [Fact]
    public void StaleReview_FlagQueueConfirm_Cycle()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        Index(repo, e);

        repo.AddSemanticEdge("PlayerController", "Inventory", "COLLABORATES_WITH", 0.8, "uses", "test", Now, null, false);

        // 構造変更を模擬: Game.Inventory を needsReview 化
        var flagged = repo.FlagSemanticReview(new[] { "Game.Inventory" }, "source changed");
        Assert.Equal(1, flagged);

        var queue = repo.ReviewQueue();
        Assert.Contains(queue.Rows, r => r[1]?.ToString() == "PlayerController" && r[2]?.ToString() == "Inventory");

        // 再確認でフラグ解除
        repo.ConfirmSemanticEdge("PlayerController", "Inventory", "COLLABORATES_WITH", Now);
        Assert.DoesNotContain(repo.ReviewQueue().Rows,
            r => r[1]?.ToString() == "PlayerController" && r[2]?.ToString() == "Inventory");
    }

    [Fact]
    public void FlagSemanticReview_IgnoresCommunityEdges()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        // コミュニティ(PART_OF, author=community-detection)を含めて構築
        IndexPipeline.Run(Fixture.SampleProject(), repo, e, Now, communities: true);

        // 全型キーを review 対象にしても、コミュニティ由来エッジは needsReview にならない
        var flagged = repo.FlagSemanticReview(new[] { "Game.PlayerController", "Game.Inventory", "Game.Character" }, "x");
        // LLM由来エッジは無い状態なので 0、かつ PART_OF は除外される
        Assert.Equal(0, flagged);
    }

    [Fact]
    public void Reflect_SurfacesLowConfidence()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        var e = new HashingEmbedder();
        Index(repo, e);

        repo.AddSemanticEdge("PlayerController", "Inventory", "RELATES_TO", 0.2, "weak", "test", Now, null, false);
        var low = repo.LowConfidenceEdges(0.5);
        Assert.Contains(low.Rows, r => r[0]?.ToString() == "RELATES_TO");
        // 閾値未満のみ
        Assert.All(low.Rows, r => Assert.True((double)r[3]! < 0.5));
    }

    [Fact]
    public void EmbedderSwitch_ReembedsAtNewDimension()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);

        IndexPipeline.Run(Fixture.SampleProject(), repo, new HashingEmbedder(256), Now, communities: false);

        // 別次元の埋め込み器へ切替（ファイル不変でも全再埋め込みされる）
        var e128 = new HashingEmbedder(128);
        var switched = IndexPipeline.Run(Fixture.SampleProject(), repo, e128, Now, communities: false);
        Assert.False(switched.UpToDate);
        Assert.True(switched.Embedded > 0);
        Assert.Equal(0, switched.EmbeddingsSkipped);

        // 新次元(128)で検索が機能する（索引が作り直されている）
        var res = repo.Search(e128, "take damage", 3, null);
        Assert.NotEmpty(res.Rows);

        // 切替後の状態は EmbedderId を保持
        var state = IndexState.FromJson(repo.LoadIndexState());
        Assert.Equal("hashing-128", state!.EmbedderId);
    }

    [Fact]
    public void Status_OnEmptyGraph_DoesNotThrow()
    {
        using var client = Connect();
        if (client is null) return;
        var repo = new GraphRepository(client);
        // 未indexのグラフでも読み取りは空として扱える（GraphClient寛容化）
        Assert.Null(repo.LoadIndexState());
        Assert.Empty(repo.ReviewQueue().Rows);
    }
}
