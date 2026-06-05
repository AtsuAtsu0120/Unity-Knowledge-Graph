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
                "find" => Lookup(args, (repo, x) => repo.FindByName(x)),
                "neighbors" => Lookup(args, (repo, x) => repo.Neighbors(x)),
                "deps" => Lookup(args, (repo, x) => repo.Deps(x)),
                "impact" => Lookup(args, (repo, x) => repo.Impact(x)),
                "sem" => Sem(args),
                "-h" or "--help" or "help" => Usage(),
                _ => Fail($"unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static int Index(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg index <unityProjectPath>");
        var projectPath = args[1];
        if (!Directory.Exists(projectPath)) return Fail($"path not found: {projectPath}");

        // 1) 静的抽出（Asset + C#）
        var assetGraph = new AssetExtractor().Extract(projectPath);
        var csGraph = new CSharpExtractor().Extract(projectPath);

        // 2) マージ ＋ SCRIPT_OF 橋渡し（.cs Asset の guid → その型）
        var merged = new ExtractionResult();
        merged.Nodes.AddRange(assetGraph.Result.Nodes);
        merged.Nodes.AddRange(csGraph.Result.Nodes);
        merged.Edges.AddRange(assetGraph.Result.Edges);
        merged.Edges.AddRange(csGraph.Result.Edges);

        int scriptOf = 0;
        foreach (var group in csGraph.Classes.GroupBy(c => c.File))
        {
            if (!assetGraph.GuidByPath.TryGetValue(group.Key, out var guid)) continue;
            var basename = Path.GetFileNameWithoutExtension(group.Key);
            var matches = group.Where(c => c.Name == basename).ToList();
            var targets = matches.Count > 0 ? matches : group.ToList(); // 同名クラスが無ければ全て繋ぐ
            foreach (var t in targets)
            {
                merged.AddEdge(new GraphEdge(Schema.ScriptOf, Schema.Asset, guid, t.Label, t.Key));
                scriptOf++;
            }
        }

        // 3) 静的レイヤーを再構築（意味エッジは保持）
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        var stats = repo.ApplyStaticLayer(merged);
        var semantic = repo.ListSemanticEdges();

        Write(new
        {
            ok = true,
            project = Path.GetFullPath(projectPath),
            graph = client.GraphName,
            nodes = stats.Nodes,
            edges = stats.Edges,
            scriptOfEdges = scriptOf,
            semanticEdgesPreserved = semantic.Rows.Count
        });
        return 0;
    }

    private static int Query(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg query \"<cypher>\"");
        using var client = GraphClient.Connect();
        WriteResult(new GraphRepository(client).Raw(args[1]));
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
                WriteResult(repo.ListSemanticEdges());
                return 0;
            case "add":
                var opt = ParseFlags(args, 2);
                var from = Require(opt, "from");
                var to = Require(opt, "to");
                var rel = Require(opt, "rel");
                double? confidence = opt.TryGetValue("confidence", out var c) && double.TryParse(c, out var cd) ? cd : null;
                var why = opt.GetValueOrDefault("why");
                var author = opt.GetValueOrDefault("author", "llm");
                WriteResult(repo.AddSemanticEdge(from, to, rel, confidence, why, author));
                return 0;
            default:
                return Fail($"unknown sem subcommand: {args[1]}");
        }
    }

    // ---- helpers ----

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
          index <unityProjectPath>      Unityプロジェクトを解析し静的レイヤーを再構築
          query "<cypher>"              生Cypherを実行
          find <name>                   名前/キーでノード検索
          neighbors <name>              隣接エッジ（出入り両方向）
          deps <assetPathOrGuid>        Assetの依存（DEPENDS_ON / SCRIPT_OF）
          impact <name>                 変更時の影響範囲（推移的な上流依存）
          sem add --from <a> --to <b> --rel <REL> [--confidence 0.8] [--why "..."] [--author llm]
                                        意味エッジを追加（source=semantic）
          sem ls                        意味エッジ一覧

        Env: UKG_REDIS (default localhost:6379), UKG_GRAPH (default unity)
        """);
}
