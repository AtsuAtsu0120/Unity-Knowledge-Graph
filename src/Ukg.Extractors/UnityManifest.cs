using System.Text.Json;
using System.Text.Json.Serialization;
using Ukg.Core;

namespace Ukg.Extractors;

/// <summary>
/// Unity Editor エクスポータ(com.ukg.exporter)が出力する依存マニフェスト（ADR-008）。
/// AssetDatabase 由来の正確な依存と script guid→型を取り込み、正規表現フォールバックより優先する。
/// </summary>
public sealed class UnityManifest
{
    [JsonPropertyName("schema")] public int SchemaVersion { get; init; }
    [JsonPropertyName("unityVersion")] public string? UnityVersion { get; init; }
    [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; init; }
    [JsonPropertyName("assets")] public List<ManifestAsset> Assets { get; init; } = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static UnityManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<UnityManifest>(json, Options)
               ?? throw new InvalidDataException($"Unity manifest を読めない: {path}");
    }

    /// <summary>
    /// マニフェストから Asset ノード・DEPENDS_ON・SCRIPT_OF を構築する。
    /// SCRIPT_OF は scriptType（厳密FQN）が既知の型キーに一致する場合のみ、その型の実ラベルで張る。
    /// </summary>
    public AssetLayer ToAssetLayer(IReadOnlyDictionary<string, string> typeKeyToLabel)
    {
        var result = new ExtractionResult();
        var guidByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var guids = new HashSet<string>(Assets.Select(a => a.Guid));

        foreach (var a in Assets)
        {
            if (string.IsNullOrEmpty(a.Guid)) continue;
            result.AddNode(GraphNode.Create(Schema.Asset, a.Guid,
                (Schema.PropName, Path.GetFileName(a.Path)),
                (Schema.PropPath, a.Path),
                (Schema.PropGuid, a.Guid)));
            if (!string.IsNullOrEmpty(a.Path)) guidByPath[a.Path] = a.Guid;
        }

        int scriptOf = 0;
        foreach (var a in Assets)
        {
            if (string.IsNullOrEmpty(a.Guid)) continue;

            foreach (var dep in a.Dependencies)
            {
                if (dep == a.Guid || !guids.Contains(dep)) continue;
                result.AddEdge(new GraphEdge(Schema.DependsOn, Schema.Asset, a.Guid, Schema.Asset, dep));
            }

            if (!string.IsNullOrEmpty(a.ScriptType) && typeKeyToLabel.TryGetValue(a.ScriptType!, out var label))
            {
                // 実ラベル（Class/Interface/Struct/Enum）で張る。型ノードは MERGE で収束済み。
                result.AddEdge(new GraphEdge(Schema.ScriptOf, Schema.Asset, a.Guid, label, a.ScriptType!));
                scriptOf++;
            }
        }

        return new AssetLayer(result, guidByPath, scriptOf);
    }
}

public sealed class ManifestAsset
{
    [JsonPropertyName("guid")] public string Guid { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("scriptType")] public string? ScriptType { get; init; }
    [JsonPropertyName("dependencies")] public string[] Dependencies { get; init; } = Array.Empty<string>();
}

/// <summary>アセット層の構築結果（ノード/エッジ ＋ SCRIPT_OF 本数）。</summary>
public sealed record AssetLayer(ExtractionResult Result, IReadOnlyDictionary<string, string> GuidByPath, int ScriptOfEdges);
