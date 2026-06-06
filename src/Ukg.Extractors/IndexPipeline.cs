using Ukg.Core;

namespace Ukg.Extractors;

/// <summary>
/// インデックス手続き一式（抽出 → SCRIPT_OF 橋渡し → 静的レイヤー再構築 → コミュニティ → 埋め込み）。
/// CLI とテストで共有する再利用可能なオーケストレーション。
/// </summary>
public static class IndexPipeline
{
    public static IndexSummary Run(
        string projectPath, GraphRepository repo, IEmbedder embedder, string nowIso, bool communities,
        string? unityManifestPath = null, bool force = false)
    {
        // 鮮度判定: 変更が無ければ抽出もDB書き込みもせず高速スキップ（ADR-009）
        var currentState = IndexState.Compute(projectPath, nowIso);
        currentState.EmbedderId = embedder.Id;
        if (!string.IsNullOrEmpty(unityManifestPath) && File.Exists(unityManifestPath))
            currentState.Files["::unity-manifest"] = Hashing.Sha1(File.ReadAllText(unityManifestPath));
        var previousState = IndexState.FromJson(repo.LoadIndexState());
        var diff = currentState.DiffFrom(previousState);
        // 埋め込み器が変わったら全再埋め込み（次元・ベクトル非互換, ADR-010）
        bool embedderChanged = previousState is not null && previousState.EmbedderId != embedder.Id;
        if (!force && previousState is not null && !diff.HasChanges && !embedderChanged)
            return IndexSummary.Skipped();

        // 埋め込み器切替: 旧ベクトルと索引を破棄し新次元で作り直す
        if (embedderChanged)
        {
            repo.ResetEmbeddings();
            repo.DropVectorIndexes();
        }

        var csGraph = new CSharpExtractor().Extract(projectPath);

        var merged = new ExtractionResult();
        merged.Nodes.AddRange(csGraph.Result.Nodes);
        merged.Edges.AddRange(csGraph.Result.Edges);

        // アセット層: マニフェスト(Unity真値)があれば優先、無ければ正規表現フォールバック（ADR-008）
        int scriptOf;
        bool usedManifest = false;
        if (!string.IsNullOrEmpty(unityManifestPath) && File.Exists(unityManifestPath))
        {
            var typeKeyToLabel = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in csGraph.Classes) typeKeyToLabel[c.Key] = c.Label; // FQN→実ラベル
            var layer = UnityManifest.Load(unityManifestPath).ToAssetLayer(typeKeyToLabel);
            merged.Nodes.AddRange(layer.Result.Nodes);
            merged.Edges.AddRange(layer.Result.Edges);
            scriptOf = layer.ScriptOfEdges;
            usedManifest = true;
        }
        else
        {
            var assetGraph = new AssetExtractor().Extract(projectPath);
            merged.Nodes.AddRange(assetGraph.Result.Nodes);
            merged.Edges.AddRange(assetGraph.Result.Edges);
            scriptOf = BridgeScriptsByFilename(csGraph, assetGraph, merged);
        }

        var stats = repo.ApplyStaticLayer(merged, embedder.Dimension);

        int communityCount = 0;
        if (communities)
        {
            var (nodes, edges) = repo.StructuralGraph();
            var detected = CommunityDetector.Detect(nodes, edges, new HeuristicSummarizer());
            communityCount = repo.WriteCommunities(detected, nowIso);
        }

        var emb = repo.ApplyEmbeddings(embedder);

        // stale伝播: 変更/追加された .cs に属する型の意味エッジを needsReview 化（ADR-009）
        int flagged = 0;
        if (previousState is not null)
        {
            var fileToKeys = csGraph.Classes.GroupBy(c => c.File)
                .ToDictionary(g => g.Key, g => g.Select(c => c.Key).ToList());
            var changedKeys = diff.AddedAndChanged
                .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => fileToKeys.TryGetValue(p, out var ks) ? ks : Enumerable.Empty<string>())
                .Distinct().ToList();
            flagged = repo.FlagSemanticReview(changedKeys, "source changed");
        }

        repo.SaveIndexState(currentState.ToJson(), nowIso);

        return new IndexSummary(stats.Nodes, stats.Edges, stats.Removed, stats.Staled, scriptOf,
            communityCount, emb.Embedded, emb.Skipped, usedManifest,
            UpToDate: false, ChangedFiles: diff.Added.Count + diff.Changed.Count + diff.Removed.Count, FlaggedForReview: flagged);
    }

    /// <summary>マニフェスト不在時の SCRIPT_OF 橋渡し（ファイル名一致のフォールバック）。</summary>
    private static int BridgeScriptsByFilename(CSharpGraph csGraph, AssetGraph assetGraph, ExtractionResult merged)
    {
        int scriptOf = 0;
        foreach (var group in csGraph.Classes.GroupBy(c => c.File))
        {
            if (!assetGraph.GuidByPath.TryGetValue(group.Key, out var guid)) continue;
            var basename = Path.GetFileNameWithoutExtension(group.Key);
            var matches = group.Where(c => c.Name == basename).ToList();
            var targets = matches.Count > 0 ? matches : group.ToList();
            foreach (var t in targets)
            {
                merged.AddEdge(new GraphEdge(Schema.ScriptOf, Schema.Asset, guid, t.Label, t.Key));
                scriptOf++;
            }
        }
        return scriptOf;
    }
}

public sealed record IndexSummary(
    int Nodes, int Edges, int Removed, int Staled, int ScriptOfEdges, int Communities, int Embedded, int EmbeddingsSkipped,
    bool UsedManifest, bool UpToDate = false, int ChangedFiles = 0, int FlaggedForReview = 0)
{
    /// <summary>変更なしで処理をスキップしたときのサマリ。</summary>
    public static IndexSummary Skipped() =>
        new(0, 0, 0, 0, 0, 0, 0, 0, false, UpToDate: true, ChangedFiles: 0, FlaggedForReview: 0);
}
