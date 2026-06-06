using Ukg.Core;
using Ukg.Extractors;
using Xunit;

namespace Ukg.Eval;

/// <summary>
/// Unity マニフェスト取り込み（ADR-008）の決定的テスト（DB不要）。
/// AssetDatabase 真値の DEPENDS_ON と、scriptType による厳密な SCRIPT_OF を固定する。
/// </summary>
public sealed class ManifestTests
{
    private static UnityManifest SampleManifest()
    {
        var path = Path.Combine(Fixture.RepoRoot(), "fixtures", "unity-manifest.sample.json");
        return UnityManifest.Load(path);
    }

    private static IReadOnlyDictionary<string, string> RealTypeLabels()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in Fixture.ExtractCs().Classes) dict[c.Key] = c.Label;
        return dict;
    }

    [Fact]
    public void Load_ParsesAssetsAndDependencies()
    {
        var m = SampleManifest();
        Assert.Equal(1, m.SchemaVersion);
        Assert.Equal(6, m.Assets.Count);
        var prefab = m.Assets.First(a => a.Path.EndsWith("Player.prefab"));
        Assert.Contains("33333333333333333333333333333333", prefab.Dependencies);
    }

    [Fact]
    public void AssetLayer_DependsOn_FromUnityTruth()
    {
        var layer = SampleManifest().ToAssetLayer(RealTypeLabels());
        // Player.prefab(5555) -> PlayerController.cs(3333)
        Assert.Contains(layer.Result.Edges, e =>
            e.Type == Schema.DependsOn &&
            e.FromKey == "55555555555555555555555555555555" &&
            e.ToKey == "33333333333333333333333333333333");
    }

    [Fact]
    public void AssetLayer_ScriptOf_UsesExactType_AndRealLabel()
    {
        var layer = SampleManifest().ToAssetLayer(RealTypeLabels());

        // クラスは Class ラベルで
        Assert.Contains(layer.Result.Edges, e =>
            e.Type == Schema.ScriptOf && e.ToLabel == Schema.Class && e.ToKey == "Game.PlayerController");

        // インターフェースは Interface ラベルで張られる（ラベル取り違えの回帰防止）
        Assert.Contains(layer.Result.Edges, e =>
            e.Type == Schema.ScriptOf && e.ToLabel == Schema.Interface && e.ToKey == "Game.IDamageable");

        // スクリプトでない prefab には SCRIPT_OF を張らない
        Assert.DoesNotContain(layer.Result.Edges, e =>
            e.Type == Schema.ScriptOf && e.FromKey == "55555555555555555555555555555555");
    }

    [Fact]
    public void AssetLayer_DropsDependencyToUnknownGuid()
    {
        var layer = SampleManifest().ToAssetLayer(RealTypeLabels());
        // マニフェストに無い guid への依存は張られない（外部除外）
        Assert.DoesNotContain(layer.Result.Edges, e =>
            e.Type == Schema.DependsOn && e.ToKey == "deadbeefdeadbeefdeadbeefdeadbeef");
    }
}
