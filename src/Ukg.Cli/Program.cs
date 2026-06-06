using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ukg.Core;
using Ukg.Extractors;

return UkgCli.Run(args);

internal static class UkgCli
{
    // 非ASCII（日本語の rationale やエラーメッセージ等）をそのまま出力し、LLM/人間が読みやすくする。
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }
        try
        {
            return args[0] switch
            {
                "index" => Index(args),
                "query" => Query(args),
                "search" => Search(args),
                "find" => Lookup(args, (repo, x) => repo.FindByName(x)),
                "neighbors" => Lookup(args, (repo, x) => repo.Neighbors(x)),
                "deps" => Lookup(args, (repo, x) => repo.Deps(x)),
                "impact" => Impact(args),
                "sem" => Sem(args),
                "concept" => Concept(args),
                "community" => Community(args),
                "-h" or "--help" or "help" => Usage(),
                _ => Fail($"unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static IEmbedder MakeEmbedder() => new HashingEmbedder(256);

    private static int Index(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg index <unityProjectPath> [--no-communities]");
        var projectPath = args[1];
        if (!Directory.Exists(projectPath)) return Fail($"path not found: {projectPath}");
        bool communities = !args.Contains("--no-communities");
        var opt = ParseFlags(args, 2);
        var manifest = opt.GetValueOrDefault("unity-manifest");
        if (manifest is not null && !File.Exists(manifest)) return Fail($"unity manifest not found: {manifest}");

        var embedder = MakeEmbedder();
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        var s = IndexPipeline.Run(projectPath, repo, embedder, NowIso(), communities, manifest);
        var semantic = repo.ListSemanticEdges();

        Write(new
        {
            ok = true,
            project = Path.GetFullPath(projectPath),
            graph = client.GraphName,
            assetSource = s.UsedManifest ? "unity-manifest" : "regex-fallback",
            nodes = s.Nodes,
            edges = s.Edges,
            removed = s.Removed,
            staled = s.Staled,
            scriptOfEdges = s.ScriptOfEdges,
            communities = s.Communities,
            embedded = s.Embedded,
            embeddingsSkipped = s.EmbeddingsSkipped,
            semanticEdgesPreserved = semantic.Rows.Count
        });
        return 0;
    }

    private static int Query(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg query \"<cypher>\" [--write]");
        bool write = args.Contains("--write");
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        WriteResult(write ? repo.RawWrite(args[1]) : repo.Raw(args[1]));
        return 0;
    }

    private static int Search(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg search \"<text>\" [--k 10] [--label Class]");
        var opt = ParseFlags(args, 2);
        int k = opt.TryGetValue("k", out var ks) && int.TryParse(ks, out var kv) ? kv : 10;
        var label = opt.GetValueOrDefault("label");
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        WriteResult(repo.Search(MakeEmbedder(), args[1], k, label));
        return 0;
    }

    private static int Impact(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg impact <name> [--depth 4]");
        var opt = ParseFlags(args, 2);
        int depth = opt.TryGetValue("depth", out var ds) && int.TryParse(ds, out var dv) ? dv : 4;
        using var client = GraphClient.Connect();
        WriteResult(new GraphRepository(client).Impact(args[1], depth));
        return 0;
    }

    private static int Lookup(string[] args, Func<GraphRepository, string, QueryResult> op)
    {
        if (args.Length < 2) return Fail($"usage: ukg {args[0]} <name>");
        using var client = GraphClient.Connect();
        WriteResult(op(new GraphRepository(client), args[1]));
        return 0;
    }

    private static int Sem(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg sem <add|ls>");
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);

        switch (args[1])
        {
            case "ls":
                bool all = args.Contains("--all");
                WriteResult(repo.ListSemanticEdges(all));
                return 0;
            case "add":
                var opt = ParseFlags(args, 2);
                var from = Require(opt, "from");
                var to = Require(opt, "to");
                var rel = Require(opt, "rel");
                double? confidence = opt.TryGetValue("confidence", out var c) && double.TryParse(c, out var cd) ? cd : null;
                var why = opt.GetValueOrDefault("why");
                var author = opt.GetValueOrDefault("author", "llm");
                bool supersede = opt.ContainsKey("supersede");
                WriteResult(repo.AddSemanticEdge(from, to, rel, confidence, why, author, NowIso(), GitHead(), supersede));
                return 0;
            default:
                return Fail($"unknown sem subcommand: {args[1]}");
        }
    }

    private static int Concept(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg concept add --name <name> [--summary ...]");
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        if (args[1] != "add") return Fail($"unknown concept subcommand: {args[1]}");
        var opt = ParseFlags(args, 2);
        var name = Require(opt, "name");
        var summary = opt.GetValueOrDefault("summary");
        var author = opt.GetValueOrDefault("author", "llm");
        var res = repo.AddConcept(name, summary, author, NowIso());
        // 直ちに埋め込みを反映（意味検索の対象にする）
        repo.ApplyEmbeddings(MakeEmbedder());
        WriteResult(res);
        return 0;
    }

    private static int Community(string[] args)
    {
        // コミュニティを再計算する（index でも自動実行されるが単体でも回せる）
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        var (nodes, edges) = repo.StructuralGraph();
        var detected = CommunityDetector.Detect(nodes, edges, new HeuristicSummarizer());
        var n = repo.WriteCommunities(detected, NowIso());
        repo.ApplyEmbeddings(MakeEmbedder());
        Write(new { ok = true, communities = n, detail = detected.Select(d => new { d.Id, d.Name, members = d.MemberKeys.Count, d.Summary }) });
        return 0;
    }

    // ---- helpers ----

    private static string NowIso() => DateTime.UtcNow.ToString("o");

    /// <summary>現在のコミットSHA（取得不能なら null）。来歴記録用。</summary>
    private static string? GitHead()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(outp) ? null : outp;
        }
        catch { return null; }
    }

    private static Dictionary<string, string> ParseFlags(string[] args, int start)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = start; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            var val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
            dict[key] = val;
        }
        return dict;
    }

    private static string Require(Dictionary<string, string> opt, string key) =>
        opt.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required");

    private static void WriteResult(QueryResult r) =>
        Write(new { columns = r.Columns, rows = r.Rows, statistics = r.Statistics });

    private static void Write(object obj) =>
        Console.WriteLine(JsonSerializer.Serialize(obj, Json));

    private static int Fail(string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message }, Json));
        return 1;
    }

    private static int Usage() { PrintUsage(); return 0; }

    private static void PrintUsage() => Console.WriteLine(
        """
        ukg — Unity Knowledge Graph CLI

        Commands:
          index <unityProjectPath> [--no-communities] [--unity-manifest <path>]
                                        解析し静的レイヤー再構築＋コミュニティ＋埋め込み
                                        （--unity-manifestでUnity真値の依存を優先取り込み）
          query "<cypher>" [--write]    Cypher実行（既定は読み取り専用）
          search "<text>" [--k 10] [--label Class]
                                        意味検索（ベクトル近傍）
          find <name>                   名前/キーでノード検索
          neighbors <name>              隣接エッジ（出入り両方向）
          deps <assetPathOrGuid>        Assetの依存（DEPENDS_ON / SCRIPT_OF）
          impact <name> [--depth 4]     変更時の影響範囲（推移的な上流依存）
          sem add --from <a> --to <b> --rel <REL> [--confidence 0.8] [--why "..."] [--supersede]
          sem ls [--all]                意味エッジ一覧（--allで無効化済みも）
          concept add --name <name> [--summary "..."]   概念ノードを追加
          community                     コミュニティを再計算

        Env: UKG_REDIS (default localhost:6379), UKG_GRAPH (default unity)
        """);
}
