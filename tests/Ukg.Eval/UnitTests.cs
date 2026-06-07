using Ukg.Core;
using Ukg.Extractors;
using Xunit;

namespace Ukg.Eval;

/// <summary>埋め込み・コミュニティ・Cypher・索引状態のユニットテスト（DB不要）。</summary>
public sealed class UnitTests
{
    private static IndexState State(params (string Path, string Hash)[] files) =>
        new() { Files = files.ToDictionary(f => f.Path, f => f.Hash) };

    // ---- Addressables 抽出（ADR-015, DB不要）----

    [Fact]
    public void AddressableExtractor_ParsesGroupAndEntry()
    {
        var r = new AddressableExtractor().Extract(Fixture.SampleProject());

        // フィクスチャの SampleGroup / player-prefab エントリが取れる
        Assert.Contains(r.Nodes, n => n.Label == Schema.AddressableGroup && n.Key == "addrgroup:SampleGroup");
        Assert.Contains(r.Nodes, n => n.Label == Schema.AddressableEntry && n.Key == "addr:player-prefab");
        // アドレス → 実アセット(guid) の ADDRESSES 辺
        Assert.Contains(r.Edges, e => e.Type == Schema.Addresses
            && e.FromKey == "addr:player-prefab" && e.ToKey == "55555555555555555555555555555555");
        Assert.Contains(r.Edges, e => e.Type == Schema.HasEntry && e.FromKey == "addrgroup:SampleGroup");
    }

    [Fact]
    public void AddressableExtractor_EmptyWhenNoAddressables()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ukg_noaddr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tmp, "Assets"));
        try
        {
            // Addressables グループの無いプロジェクト → 空（壊れない）
            var r = new AddressableExtractor().Extract(tmp);
            Assert.Empty(r.Nodes);
            Assert.Empty(r.Edges);
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void CSharpExtractor_DetectsAddressablesLoadLiteral()
    {
        var cs = Fixture.ExtractCs();
        // AddressableLoader.Load() の Addressables.LoadAssetAsync<object>("player-prefab") → LOADS 辺
        Assert.Contains(cs.Result.Edges, e => e.Type == Schema.Loads
            && e.FromKey == "Game.AddressableLoader" && e.ToKey == "addr:player-prefab");
    }

    [Fact]
    public void IndexState_Diff_DetectsAddedChangedRemoved()
    {
        var prev = State(("a.cs", "h1"), ("b.cs", "h2"), ("c.cs", "h3"));
        var cur = State(("a.cs", "h1"), ("b.cs", "CHANGED"), ("d.cs", "h4"));
        var diff = cur.DiffFrom(prev);

        Assert.Equal(new[] { "d.cs" }, diff.Added);
        Assert.Equal(new[] { "b.cs" }, diff.Changed);
        Assert.Equal(new[] { "c.cs" }, diff.Removed);
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void IndexState_Diff_NoChange()
    {
        var s = State(("a.cs", "h1"), ("b.cs", "h2"));
        Assert.False(s.DiffFrom(State(("a.cs", "h1"), ("b.cs", "h2"))).HasChanges);
    }

    [Fact]
    public void IndexState_JsonRoundTrip()
    {
        var s = State(("a.cs", "h1"));
        var back = IndexState.FromJson(s.ToJson());
        Assert.NotNull(back);
        Assert.Equal("h1", back!.Files["a.cs"]);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0; for (int i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot; // 既に L2 正規化済み
    }

    [Fact]
    public void Embedder_HasStableId()
    {
        Assert.Equal("hashing-256", new HashingEmbedder(256).Id);
        Assert.Equal("hashing-128", new HashingEmbedder(128).Id);
    }

    [Fact]
    public void Embedder_DefaultEmbedBatch_MatchesEmbed()
    {
        IEmbedder e = new HashingEmbedder(64);
        var texts = new[] { "alpha beta", "gamma" };
        var batch = e.EmbedBatch(texts);
        Assert.Equal(2, batch.Count);
        Assert.Equal(e.Embed("alpha beta"), batch[0]);
        Assert.Equal(e.Embed("gamma"), batch[1]);
    }

    [Fact]
    public void EmbedderFactory_DefaultsToHashing_AndRespectsConfig()
    {
        var saved = (Environment.GetEnvironmentVariable("UKG_EMBEDDER"),
                     Environment.GetEnvironmentVariable("UKG_EMBED_DIM"));
        try
        {
            Environment.SetEnvironmentVariable("UKG_EMBEDDER", null);
            Environment.SetEnvironmentVariable("UKG_EMBED_DIM", null);
            Assert.Equal("hashing-256", Embedders.FromEnvironment().Id);

            Environment.SetEnvironmentVariable("UKG_EMBED_DIM", "512");
            Assert.Equal("hashing-512", Embedders.FromEnvironment().Id);

            // http はモデル次元(UKG_EMBED_DIM)必須
            Environment.SetEnvironmentVariable("UKG_EMBEDDER", "http");
            Environment.SetEnvironmentVariable("UKG_EMBED_DIM", null);
            Assert.Throws<InvalidOperationException>(() => Embedders.FromEnvironment());

            // http 選択時は HttpEmbedder（id は http:model:dim）
            Environment.SetEnvironmentVariable("UKG_EMBED_DIM", "1536");
            Assert.StartsWith("http:", Embedders.FromEnvironment().Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UKG_EMBEDDER", saved.Item1);
            Environment.SetEnvironmentVariable("UKG_EMBED_DIM", saved.Item2);
        }
    }

    [Fact]
    public void Embedder_IsDeterministic()
    {
        var e = new HashingEmbedder(256);
        Assert.Equal(e.Embed("PlayerController combat"), e.Embed("PlayerController combat"));
        Assert.Equal(256, e.Embed("x").Length);
    }

    [Fact]
    public void Embedder_SimilarTextRanksHigher()
    {
        var e = new HashingEmbedder(256);
        var q = e.Embed("inventory item slot storage");
        var near = e.Embed("Inventory manages item slots storage");
        var far = e.Embed("network socket tcp protocol handshake");
        Assert.True(Cosine(q, near) > Cosine(q, far));
    }

    [Fact]
    public void Community_DetectsConnectedCluster()
    {
        var nodes = new List<(string, string, string)>
        {
            ("A", "Class", "A"), ("B", "Class", "B"), ("C", "Class", "C"),
            ("X", "Class", "X"), ("Y", "Class", "Y"),
        };
        // 三角形 A-B-C と 辺 X-Y の2クラスタ
        var edges = new List<(string, string)>
        {
            ("A", "B"), ("B", "C"), ("C", "A"), ("X", "Y"),
        };
        var coms = CommunityDetector.Detect(nodes, edges, new HeuristicSummarizer(), minSize: 2);
        Assert.Equal(2, coms.Count);
        var abc = coms.First(c => c.MemberKeys.Contains("A"));
        Assert.Equal(new[] { "A", "B", "C" }, abc.MemberKeys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void Community_IsDeterministic()
    {
        var nodes = new List<(string, string, string)>
        { ("A", "Class", "A"), ("B", "Class", "B"), ("C", "Class", "C") };
        var edges = new List<(string, string)> { ("A", "B"), ("B", "C") };
        var a = CommunityDetector.Detect(nodes, edges, new HeuristicSummarizer());
        var b = CommunityDetector.Detect(nodes, edges, new HeuristicSummarizer());
        Assert.Equal(a.Select(c => string.Join(",", c.MemberKeys)), b.Select(c => string.Join(",", c.MemberKeys)));
    }

    [Fact]
    public void Cypher_EscapesInjection()
    {
        var malicious = "x') DELETE n //";
        var lit = Cypher.Str(malicious);
        Assert.StartsWith("'", lit);
        Assert.EndsWith("'", lit);
        Assert.Contains("\\'", lit); // 単一引用符はエスケープされる
    }

    [Fact]
    public void Cypher_RejectsBadIdentifier() =>
        Assert.Throws<ArgumentException>(() => Cypher.Ident("bad-name; DROP"));

    [Fact]
    public void Cypher_Vecf32Literal()
    {
        var lit = Cypher.Vecf32(new[] { 0.5f, -0.25f });
        Assert.StartsWith("vecf32([", lit);
        Assert.Contains("0.5", lit);
    }
}
