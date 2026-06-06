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
        string? unityManifestPath = null)
    {
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
        return new IndexSummary(stats.Nodes, stats.Edges, stats.Removed, stats.Staled, scriptOf, communityCount, emb.Embedded, emb.Skipped, usedManifest);
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
    bool UsedManifest);
