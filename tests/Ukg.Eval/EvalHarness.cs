using System.Text.Json;
using System.Text.Json.Serialization;
using Ukg.Core;
using Ukg.Extractors;
using Xunit;
using Xunit.Abstractions;

namespace Ukg.Eval;

/// <summary>
/// ukg の検索品質を「内訳が取れる」形で評価するハーネス（docs/EVAL.md の3レーン設計）。
///
/// - レーンA コールドスタート: index のみ（構造＋オフライン埋め込み, 種付け0）。
///   → スコアの変化＝純粋にエンジン由来。「ukg 自体の地力」を測る。
/// - レーンB 種付け後: 固定 curation スクリプト(golden/curation.json)を適用。
///   → エンジン＋curation 合算の総合点。
/// - lift = (B の Recall − A の Recall)。コーパス・エンジンを固定しているので、
///   lift は「意味づけ（curation/スキル）の貢献」を表す（docs/EVAL.md レーンC の土台）。
///
/// FalkorDB 未起動時はスキップ（早期return）。専用グラフ ukg_eval_harness で完結。
/// </summary>
public sealed class EvalHarness
{
    private const string Now = "2026-01-01T00:00:00.0000000Z";
    private readonly ITestOutputHelper _out;
    private readonly List<string> _report = new();
    public EvalHarness(ITestOutputHelper o) => _out = o;

    /// <summary>テストログ(CI用)とレポートバッファ(ファイル出力用)の両方に書く。</summary>
    private void Log(string line) { _out.WriteLine(line); _report.Add(line); }

    // ---- 回帰ゲート（大きな劣化を検知する保守的な下限。改善したら引き上げる）----
    private const double ColdStartRecallFloor = 0.60; // 構造クエリは種付け無しでも引けるはず
    private const double CuratedRecallFloor = 0.85;   // 種付け後はほぼ全問取れるはず

    [Fact]
    public void Scorecard_AllLanes_AndRegressionGate()
    {
        var client = Connect();
        if (client is null) { _out.WriteLine("[skip] FalkorDB 未起動"); return; }
        using var _ = client;
        Log($"ukg 検索品質スコアカード  (corpus: SampleUnityProject)");

        var golden = LoadGolden();
        var curation = LoadCuration();
        var embedder = new HashingEmbedder();

        // --- レーンA: コールドスタート ---
        var repo = BuildGraph(client, embedder, curation: null);
        var cold = Score(repo, embedder, golden);
        Print("レーンA コールドスタート（種付け0・純エンジン）", cold);

        // --- レーンB: 種付け後 ---
        repo = BuildGraph(client, embedder, curation);
        var curated = Score(repo, embedder, golden);
        Print("レーンB 種付け後（エンジン＋固定curation）", curated);

        // --- lift（意味づけの貢献）---
        var lift = curated.RecallAtK - cold.RecallAtK;
        Log("");
        Log($"━━ 意味づけ（種付け）の効果 ━━");
        Log($"  正解率: {cold.RecallAtK:P0} → {curated.RecallAtK:P0}  （+{lift:P0} ポイント）");
        Log($"  ※ コードもエンジンも同じ条件なので、この上昇分はすべて「意味づけ」の貢献");

        // --- レポートをファイルにも書き出す（detailed ロガー無しでも見れるように）---
        var reportPath = Path.Combine(Fixture.RepoRoot(), "tests", "Ukg.Eval", "eval-report.txt");
        File.WriteAllText(reportPath, string.Join("\n", _report) + "\n");
        _out.WriteLine($"\n[レポート保存] {reportPath}");

        // ---- 回帰ゲート ----
        Assert.Equal(0, cold.FalseHigh);     // false-high（自信満々の誤答）は最悪。常に0であるべき
        Assert.Equal(0, curated.FalseHigh);
        Assert.True(cold.RecallAtK >= ColdStartRecallFloor,
            $"コールドスタート Recall@{golden.K}={cold.RecallAtK:F3} が下限 {ColdStartRecallFloor} を下回った（エンジン劣化の疑い）");
        Assert.True(curated.RecallAtK >= CuratedRecallFloor,
            $"種付け後 Recall@{golden.K}={curated.RecallAtK:F3} が下限 {CuratedRecallFloor} を下回った");
        Assert.True(curated.RecallAtK >= cold.RecallAtK,
            "curation が Recall を下げている（意味づけが逆効果）");
    }

    // ---- グラフ構築 ----

    /// <summary>フィクスチャを index し、curation があれば適用する。curation=null はコールドスタート。</summary>
    private static GraphRepository BuildGraph(GraphClient client, IEmbedder embedder, Curation? curation)
    {
        client.DeleteGraph();
        var repo = new GraphRepository(client);
        IndexPipeline.Run(Fixture.SampleProject(), repo, embedder, Now,
            communities: true, unityManifestPath: null, force: true);

        if (curation is not null)
        {
            foreach (var c in curation.Concepts)
                repo.AddConcept(c.Name, c.Summary, "eval-curation", Now);
            foreach (var e in curation.Edges)
                repo.AddSemanticEdge(e.From, e.To, e.Rel, e.Confidence, e.Why, "eval-curation", Now, null, supersede: false);
            repo.ApplyEmbeddings(embedder); // concept 要約を意味検索の対象にする
        }
        return repo;
    }

    // ---- 採点 ----

    private static Scorecard Score(GraphRepository repo, IEmbedder embedder, Golden golden)
    {
        var card = new Scorecard { K = golden.K };
        foreach (var q in golden.Queries)
        {
            var r = q.Type == "semantic"
                ? repo.Search(embedder, q.Query, golden.K, null)
                : repo.Candidates(q.Query, golden.K, null);

            int keyCol = IndexOf(r.Columns, "key");
            var keys = r.Rows.Select(row => keyCol >= 0 ? row[keyCol]?.ToString() : null)
                             .Where(s => s is not null).Cast<string>().Take(golden.K).ToList();

            // miss シグナル階層（candidates のみ。semantic は距離のみで階層なし）
            string tier = q.Type == "semantic" ? "n/a" : Tier(r);

            bool hit = q.Expected.Any(keys.Contains);
            int rank = 1; bool ranked = false;
            foreach (var k in keys) { if (q.Expected.Contains(k)) { ranked = true; break; } rank++; }

            if (q.ShouldHit)
            {
                card.PosTotal++;
                if (hit) { card.PosHit++; card.MrrSum += ranked ? 1.0 / rank : 0; }
                else card.FailedPositives.Add($"{q.id} \"{q.Query}\" (tier={tier})");
            }
            else
            {
                card.NegTotal++;
                if (tier == "high" || (q.Type != "semantic" && hit)) { card.FalseHigh++; card.FalseHighList.Add($"{q.id} \"{q.Query}\""); }
                else if (tier == "none" || q.Type == "semantic") card.CorrectReject++;
            }
        }
        return card;
    }

    /// <summary>candidates 結果の confidence 階層（CLI と同じ閾値: high>=1.0 / low>0 / none=0件）。</summary>
    private static string Tier(QueryResult r)
    {
        if (r.Rows.Count == 0) return "none";
        int sc = IndexOf(r.Columns, "score");
        if (sc < 0) return "low";
        double top = r.Rows[0][sc] switch { double d => d, long l => l, int n => n, _ => 0 };
        return top >= 1.0 ? "high" : "low";
    }

    private void Print(string lane, Scorecard c)
    {
        Log("");
        Log($"━━ {lane} ━━");
        Log($"  正解率　　　　　　 : {c.PosHit}/{c.PosTotal}問 ({c.RecallAtK:P0})   ← 上位{c.K}件に正解が入っていた割合");
        Log($"  正解の上位さ　　　 : {c.Mrr:F2} / 1.00          ← 1に近いほど正解が上の方に来る");
        Log($"  「該当なし」を当てた: {c.CorrectReject}/{c.NegTotal}問          ← 存在しない物を正しく門前払いできた数");
        Log($"  自信満々の誤答　　 : {c.FalseHigh}件 {(c.FalseHigh == 0 ? "✓(理想)" : "✗ 要修正")}        ← 最悪のミス。常に0であるべき");
        if (c.FailedPositives.Count > 0) Log("  取りこぼした問い　 : " + string.Join(" / ", c.FailedPositives));
        if (c.FalseHighList.Count > 0) Log("  ⚠ 自信満々で外した : " + string.Join(" / ", c.FalseHighList));
    }

    private static int IndexOf(IReadOnlyList<string> cols, string name)
    {
        for (int i = 0; i < cols.Count; i++) if (cols[i] == name) return i;
        return -1;
    }

    // ---- ロード ----

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static string GoldenDir => Path.Combine(Fixture.RepoRoot(), "tests", "Ukg.Eval", "golden");
    private static Golden LoadGolden() => JsonSerializer.Deserialize<Golden>(File.ReadAllText(Path.Combine(GoldenDir, "queries.json")), JsonOpts)!;
    private static Curation LoadCuration() => JsonSerializer.Deserialize<Curation>(File.ReadAllText(Path.Combine(GoldenDir, "curation.json")), JsonOpts)!;

    private static GraphClient? Connect()
    {
        // 専用グラフ（他テストクラスと並列実行されるので名前を分けて衝突回避）
        try { var c = GraphClient.Connect(graph: "ukg_eval_harness"); c.Query("RETURN 1"); return c; }
        catch { return null; }
    }

    // ---- DTO ----

    private sealed record Golden
    {
        public int K { get; init; } = 5;
        public List<GoldenQuery> Queries { get; init; } = new();
    }

    private sealed record GoldenQuery
    {
        public string id { get; init; } = "";
        public string Query { get; init; } = "";
        public string Type { get; init; } = "lexical";
        public string Lang { get; init; } = "en";
        [JsonPropertyName("should_hit")] public bool ShouldHit { get; init; }
        public string Targets { get; init; } = "";
        public List<string> Expected { get; init; } = new();
    }

    private sealed record Curation
    {
        public List<CurConcept> Concepts { get; init; } = new();
        public List<CurEdge> Edges { get; init; } = new();
    }
    private sealed record CurConcept { public string Name { get; init; } = ""; public string? Summary { get; init; } }
    private sealed record CurEdge
    {
        public string From { get; init; } = ""; public string To { get; init; } = ""; public string Rel { get; init; } = "";
        public string? Why { get; init; } public double? Confidence { get; init; }
    }

    private sealed class Scorecard
    {
        public int K = 5;
        public int PosTotal, PosHit, NegTotal, CorrectReject, FalseHigh;
        public double MrrSum;
        public List<string> FailedPositives = new();
        public List<string> FalseHighList = new();
        public double RecallAtK => PosTotal == 0 ? 0 : (double)PosHit / PosTotal;
        public double Mrr => PosTotal == 0 ? 0 : MrrSum / PosTotal;
    }
}
