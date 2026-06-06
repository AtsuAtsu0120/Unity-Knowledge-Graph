using Ukg.Core;
using Ukg.Extractors;
using Xunit;

namespace Ukg.Eval;

/// <summary>埋め込み・コミュニティ・Cypher・索引状態のユニットテスト（DB不要）。</summary>
public sealed class UnitTests
{
    private static IndexState State(params (string Path, string Hash)[] files) =>
        new() { Files = files.ToDictionary(f => f.Path, f => f.Hash) };

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
