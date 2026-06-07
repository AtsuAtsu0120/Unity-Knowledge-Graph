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
                "status" => Status(args),
                "watch" => Watch(args),
                "query" => Query(args),
                "search" => Search(args),
                "candidates" => Candidates(args),
                "find" => Lookup(args, (repo, x) => repo.FindByName(x)),
                "neighbors" => Lookup(args, (repo, x) => repo.Neighbors(x)),
                "deps" => Lookup(args, (repo, x) => repo.Deps(x)),
                "impact" => Impact(args),
                "sem" => Sem(args),
                "concept" => Concept(args),
                "community" => Community(args),
                "reflect" => Reflect(args),
                "gaps" => Gaps(args),
                "basis" => Basis(args),
                "-h" or "--help" or "help" => Usage(),
                _ => Fail($"unknown command: {args[0]}")
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    private static IEmbedder MakeEmbedder() => Embedders.FromEnvironment();

    private static int Index(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg index <unityProjectPath> [--no-communities] [--force] [--unity-manifest <path>]");
        var projectPath = args[1];
        if (!Directory.Exists(projectPath)) return Fail($"path not found: {projectPath}");
        bool communities = !args.Contains("--no-communities");
        bool force = args.Contains("--force");
        var opt = ParseFlags(args, 2);
        var manifest = opt.GetValueOrDefault("unity-manifest");
        if (manifest is not null && !File.Exists(manifest)) return Fail($"unity manifest not found: {manifest}");

        using var client = GraphClient.Connect();
        Write(DoIndex(client, projectPath, manifest, communities, force));
        return 0;
    }

    /// <summary>index 本体（watch からも使う）。サマリ用の匿名オブジェクトを返す。</summary>
    private static object DoIndex(GraphClient client, string projectPath, string? manifest, bool communities, bool force)
    {
        var repo = new GraphRepository(client);
        var embedder = MakeEmbedder();
        var s = IndexPipeline.Run(projectPath, repo, embedder, NowIso(), communities, manifest, force);
        if (s.UpToDate)
            return new { ok = true, project = Path.GetFullPath(projectPath), graph = client.GraphName, upToDate = true };

        var semantic = repo.ListSemanticEdges();
        return new
        {
            ok = true,
            project = Path.GetFullPath(projectPath),
            graph = client.GraphName,
            embedder = embedder.Id,
            assetSource = s.UsedManifest ? "unity-manifest" : "regex-fallback",
            changedFiles = s.ChangedFiles,
            nodes = s.Nodes,
            edges = s.Edges,
            removed = s.Removed,
            staled = s.Staled,
            scriptOfEdges = s.ScriptOfEdges,
            communities = s.Communities,
            embedded = s.Embedded,
            embeddingsSkipped = s.EmbeddingsSkipped,
            flaggedForReview = s.FlaggedForReview,
            semanticEdgesPreserved = semantic.Rows.Count
        };
    }

    private static int Status(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg status <unityProjectPath> [--unity-manifest <path>]");
        var projectPath = args[1];
        if (!Directory.Exists(projectPath)) return Fail($"path not found: {projectPath}");
        var opt = ParseFlags(args, 2);
        var manifest = opt.GetValueOrDefault("unity-manifest");

        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);

        var embedder = MakeEmbedder();
        var current = IndexState.Compute(projectPath, NowIso());
        if (manifest is not null && File.Exists(manifest))
            current.Files["::unity-manifest"] = Hashing.Sha1(File.ReadAllText(manifest));
        var stored = IndexState.FromJson(repo.LoadIndexState());
        var diff = current.DiffFrom(stored);
        bool indexed = stored is not null;
        bool embedderChanged = indexed && stored!.EmbedderId != embedder.Id;
        bool fresh = indexed && !diff.HasChanges && !embedderChanged;
        int reviewPending = repo.ReviewQueue().Rows.Count;

        Write(new
        {
            ok = true,
            project = Path.GetFullPath(projectPath),
            graph = client.GraphName,
            indexed,
            fresh,
            indexedAt = stored?.IndexedAt,
            embedder = embedder.Id,
            embedderChanged,
            added = diff.Added,
            changed = diff.Changed,
            removed = diff.Removed,
            reviewPending,
            hint = !indexed ? "ukg index <proj> を実行してください"
                 : embedderChanged ? "埋め込み器が変わりました。ukg index <proj> で全再埋め込みされます"
                 : !fresh ? "古くなっています。ukg index <proj> で更新してください"
                 : reviewPending > 0 ? "鮮度OK。ただし ukg reflect で要再確認の意味エッジがあります"
                 : "最新です"
        });
        return 0;
    }

    private static int Watch(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg watch <unityProjectPath> [--unity-manifest <path>] [--debounce 800]");
        var projectPath = args[1];
        if (!Directory.Exists(projectPath)) return Fail($"path not found: {projectPath}");
        bool communities = !args.Contains("--no-communities");
        var opt = ParseFlags(args, 2);
        var manifest = opt.GetValueOrDefault("unity-manifest");
        int debounce = opt.TryGetValue("debounce", out var dv) && int.TryParse(dv, out var d) ? d : 800;

        using var client = GraphClient.Connect();
        // 起動時に一度同期
        Write(DoIndex(client, projectPath, manifest, communities, false));

        var gate = new object();
        var pending = false;
        System.Threading.Timer? timer = null;
        void Schedule()
        {
            lock (gate)
            {
                pending = true;
                timer ??= new System.Threading.Timer(_ =>
                {
                    bool run; lock (gate) { run = pending; pending = false; }
                    if (!run) return;
                    try { Write(DoIndex(client, projectPath, manifest, communities, false)); }
                    catch (Exception ex) { Fail(ex.Message); }
                }, null, debounce, System.Threading.Timeout.Infinite);
                timer.Change(debounce, System.Threading.Timeout.Infinite);
            }
        }

        var watchers = new List<FileSystemWatcher>();
        foreach (var sub in new[] { "Assets", "Packages" })
        {
            var dir = Path.Combine(projectPath, sub);
            if (!Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            w.Filter = "*.*";
            w.Changed += (_, _) => Schedule(); w.Created += (_, _) => Schedule();
            w.Deleted += (_, _) => Schedule(); w.Renamed += (_, _) => Schedule();
            watchers.Add(w);
        }
        if (watchers.Count == 0) return Fail("watch対象(Assets/Packages)が見つからない");

        Console.Error.WriteLine($"[ukg] watching {projectPath} … Ctrl-C で終了");
        var quit = new System.Threading.ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };
        quit.Wait();
        foreach (var w in watchers) w.Dispose();
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

    private static int Candidates(string[] args)
    {
        if (args.Length < 2) return Fail("usage: ukg candidates \"<text>\" [--k 10] [--label Class]");
        var opt = ParseFlags(args, 2);
        int k = opt.TryGetValue("k", out var ks) && int.TryParse(ks, out var kv) ? kv : 10;
        var label = opt.GetValueOrDefault("label");
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        var r = repo.Candidates(args[1], k, label);

        // miss シグナル: トップスコアで信頼度を3段階判定し、grep フォールバック方針を明示する（ADR-011）。
        double? top = TopScore(r);
        string confidence = top is null ? "none" : top >= 1.0 ? "high" : "low";
        bool hit = confidence == "high";
        // グラフは英語語彙で構築される。日本語等の非ASCIIクエリが弱い/ヒット無しの時は、
        // grep に落ちる前に「英語のコード用語で引き直す」よう促す（ADR-011 #言語ブリッジ）。
        bool nonAscii = args[1].Any(c => c > 0x7F);
        bool retryEnglish = !hit && nonAscii;
        // 増築の需要シグナル: 答えられなかった(none/low)クエリを記録する（ADR-013）。
        if (!hit) repo.LogMiss(args[1], confidence, NowIso());
        string recommendation = confidence switch
        {
            "high" => "グラフに十分な候補があります。grep は不要です。",
            "low" => retryEnglish
                ? "弱い候補です。グラフは英語語彙で構築されています。まず英語のコード用語で引き直してください（例: candidates \"option button\"）。それでも的外れなら grep。"
                : "弱い候補が見つかりました。まず上位候補を確認し、的外れなら grep にフォールバックしてください。",
            _ => retryEnglish
                ? "ヒットなし。グラフは英語語彙で構築されています。grep の前に、まず英語のコード用語で引き直してください（例: candidates \"option button\"）。それでも none なら grep。"
                : "グラフにヒットがありません。grep にフォールバックし、理解したら ukg sem/concept add で書き戻してください。",
        };
        Write(new
        {
            ok = true,
            hit,
            confidence,
            topScore = top,
            retryEnglish,
            recommendation,
            columns = r.Columns,
            rows = r.Rows
        });
        return 0;
    }

    /// <summary>QueryResult の score 列の先頭（最大）値を返す。行が無ければ null。</summary>
    private static double? TopScore(QueryResult r)
    {
        if (r.Rows.Count == 0) return null;
        int idx = -1;
        for (int i = 0; i < r.Columns.Count; i++)
            if (r.Columns[i] == "score") { idx = i; break; }
        if (idx < 0) return null;
        return r.Rows[0][idx] switch
        {
            double d => d,
            long l => l,
            int n => n,
            _ => null
        };
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
                bool requireBasis = opt.ContainsKey("require-basis");
                int basisHops = opt.TryGetValue("basis-hops", out var bh) && int.TryParse(bh, out var bhv) ? bhv : 3;
                WriteResult(repo.AddSemanticEdge(from, to, rel, confidence, why, author, NowIso(), GitHead(), supersede, requireBasis, basisHops));
                return 0;
            case "review":
                WriteResult(repo.ReviewQueue());
                return 0;
            case "confirm":
                var co = ParseFlags(args, 2);
                WriteResult(repo.ConfirmSemanticEdge(Require(co, "from"), Require(co, "to"), Require(co, "rel"), NowIso()));
                return 0;
            default:
                return Fail($"unknown sem subcommand: {args[1]}");
        }
    }

    private static int Reflect(string[] args)
    {
        var opt = ParseFlags(args, 1);
        double threshold = opt.TryGetValue("min-confidence", out var t) && double.TryParse(t, out var tv) ? tv : 0.5;
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);

        object Rows(QueryResult r) => new { columns = r.Columns, rows = r.Rows };
        var review = repo.ReviewQueue();
        var lowConf = repo.LowConfidenceEdges(threshold);
        var stale = repo.StaleNodes();
        var dups = repo.DuplicateConcepts();
        var communities = repo.UncuratedCommunities();

        Write(new
        {
            ok = true,
            graph = client.GraphName,
            summary = new
            {
                needsReview = review.Rows.Count,
                lowConfidence = lowConf.Rows.Count,
                staleNodes = stale.Rows.Count,
                duplicateConcepts = dups.Rows.Count,
                uncuratedCommunities = communities.Rows.Count
            },
            needsReview = Rows(review),
            lowConfidence = Rows(lowConf),
            staleNodes = Rows(stale),
            duplicateConcepts = Rows(dups),
            uncuratedCommunities = Rows(communities),
            guidance = "needsReview/staleは sem confirm か sem add --supersede で更新。lowConfidenceは再検証。" +
                       "duplicateConceptsは統合。uncuratedCommunitiesは concept add で意味のある概念に整理。"
        });
        return 0;
    }

    private static int Basis(string[] args)
    {
        if (args.Length < 3) return Fail("usage: ukg basis <from> <to> [--hops 3]");
        var opt = ParseFlags(args, 3);
        int hops = opt.TryGetValue("hops", out var hs) && int.TryParse(hs, out var hv) ? hv : 3;
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);
        var d = repo.StructuralBasis(args[1], args[2], hops);
        Write(new
        {
            ok = true,
            from = args[1],
            to = args[2],
            maxHops = hops,
            hops = d,
            hasBasis = d is not null,
            note = d is not null
                ? $"{d} ホップで構造的に接続（意味エッジの根拠あり）"
                : $"{hops} ホップ以内に静的接続なし（意味エッジの構造的根拠が弱い）"
        });
        return 0;
    }

    private static int Gaps(string[] args)
    {
        var opt = ParseFlags(args, 1);
        int limit = opt.TryGetValue("limit", out var ls) && int.TryParse(ls, out var lv) ? lv : 15;
        // vendored/生成コード除外（カンマ区切りで複数可, 例: --exclude SQLite,Generated）
        var excludes = (opt.GetValueOrDefault("exclude") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        using var client = GraphClient.Connect();
        var repo = new GraphRepository(client);

        object Rows(QueryResult r) => new { columns = r.Columns, rows = r.Rows };
        var missed = repo.MissedQueries(limit);
        var hubs = repo.UncoveredHubs(limit, excludes);
        var comms = repo.UncuratedCommunities(excludes);

        Write(new
        {
            ok = true,
            graph = client.GraphName,
            summary = new
            {
                missedQueries = missed.Rows.Count,
                uncoveredHubs = hubs.Rows.Count,
                uncuratedCommunities = comms.Rows.Count
            },
            missedQueries = Rows(missed),
            uncoveredHubs = Rows(hubs),
            uncuratedCommunities = Rows(comms),
            guidance = "意味層を増築すべき場所。missedQueries=実需で答えられなかった問い→該当エリアを concept/sem add。" +
                       "uncoveredHubs=中心的だが意味づけ0の型→責務を sem add。uncuratedCommunities=自動クラスタ→concept add。" +
                       "すべて英語・非自明優先。reflect(保守)と対で運用する。"
        });
        return 0;
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
          index <unityProjectPath> [--no-communities] [--force] [--unity-manifest <path>]
                                        解析し静的レイヤー再構築＋コミュニティ＋埋め込み
                                        （変更が無ければスキップ。--forceで強制。--unity-manifestでUnity真値）
          status <unityProjectPath>     鮮度判定（古い/要レビューを返す。クエリ前のガード用）
          watch <unityProjectPath>      ファイル監視して変更時に自動再インデックス
          query "<cypher>" [--write]    Cypher実行（既定は読み取り専用）
          search "<text>" [--k 10] [--label Class]
                                        意味検索（ベクトル近傍）
          candidates "<text>" [--k 10] [--label Class]
                                        語彙あいまい検索（grep代替/トークン不要）。
                                        hit/confidence/grepフォールバック推奨を返す
          find <name>                   名前/キーでノード検索（完全一致）
          neighbors <name>              隣接エッジ（出入り両方向）
          deps <assetPathOrGuid>        Assetの依存（DEPENDS_ON / SCRIPT_OF）
          impact <name> [--depth 4]     変更時の影響範囲（推移的な上流依存）
          sem add --from <a> --to <b> --rel <REL> [--confidence 0.8] [--why "..."] [--supersede]
                  [--require-basis] [--basis-hops 3]  構造的根拠が無い辺を拒否（curation誤り予防）
          basis <from> <to> [--hops 3]  2ノードの構造的接続（意味エッジの根拠）を確認
          sem ls [--all]                意味エッジ一覧（--allで無効化済みも）
          sem review                    構造変更で要再確認になった意味エッジ
          sem confirm --from <a> --to <b> --rel <REL>   再確認フラグを解除
          concept add --name <name> [--summary "..."]   概念ノードを追加
          community                     コミュニティを再計算
          reflect [--min-confidence 0.5]  要レビュー/低confidence/孤児/重複概念/未整理を集約（保守）
          gaps [--limit 15] [--exclude SQLite,Generated]
                                        意味層の増築候補（答えられなかったクエリ/意味づけ0の中心型/未整理クラスタ）
                                        --excludeでvendored/生成を除外（カンマ区切り複数可, hubs/communities両方に適用）

        Env:
          UKG_REDIS (default localhost:6379), UKG_GRAPH (default unity)
          UKG_EMBEDDER  hashing(既定/オフライン) | http(=openai, OpenAI互換API)
          http時: UKG_EMBED_URL (既定 https://api.openai.com/v1/embeddings)
                  UKG_EMBED_MODEL (既定 text-embedding-3-small)
                  UKG_EMBED_API_KEY, UKG_EMBED_DIM(必須=モデルの出力次元)
        """);
}
