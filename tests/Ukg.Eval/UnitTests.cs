using Ukg.Core;
using Xunit;

namespace Ukg.Eval;

/// <summary>埋め込み・コミュニティ・Cypher のユニットテスト（DB不要）。</summary>
public sealed class UnitTests
{
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
