using Ukg.Core;
using Xunit;

namespace Ukg.Eval;

/// <summary>
/// C#/Asset 抽出のゴールデンテスト（DB不要・完全決定的）。P2 の解析精度を固定する。
/// </summary>
public sealed class ExtractorTests
{
    private static (List<GraphNode> Nodes, List<GraphEdge> Edges) Cs()
    {
        var g = Fixture.ExtractCs();
        return (g.Result.Nodes, g.Result.Edges);
    }

    private static bool HasEdge(IEnumerable<GraphEdge> edges, string type, string fromKey, string toKey) =>
        edges.Any(e => e.Type == type && e.FromKey == fromKey && e.ToKey == toKey);

    [Fact]
    public void Types_AreExtractedWithFqnKeys()
    {
        var (nodes, _) = Cs();
        Assert.Contains(nodes, n => n.Label == Schema.Class && n.Key == "Game.PlayerController");
        Assert.Contains(nodes, n => n.Label == Schema.Class && n.Key == "Game.Character");
        Assert.Contains(nodes, n => n.Label == Schema.Class && n.Key == "Game.Inventory");
        Assert.Contains(nodes, n => n.Label == Schema.Class && n.Key == "Game.Weapon");
        Assert.Contains(nodes, n => n.Label == Schema.Interface && n.Key == "Game.IDamageable");
    }

    [Fact]
    public void Inheritance_AndInterface_ResolvedBySymbol()
    {
        var (_, edges) = Cs();
        Assert.True(HasEdge(edges, Schema.Inherits, "Game.PlayerController", "Game.Character"));
        Assert.True(HasEdge(edges, Schema.Implements, "Game.PlayerController", "Game.IDamageable"));
    }

    [Fact]
    public void References_FieldType_Linked()
    {
        var (_, edges) = Cs();
        Assert.True(HasEdge(edges, Schema.References, "Game.PlayerController", "Game.Inventory"));
    }

    [Fact]
    public void Methods_AreNodes_WithDeclaredIn()
    {
        var (nodes, edges) = Cs();
        Assert.Contains(nodes, n => n.Label == Schema.Method && n.Key == "Game.PlayerController.Attack()");
        Assert.True(HasEdge(edges, Schema.DeclaredIn, "Game.PlayerController.Attack()", "Game.PlayerController"));
    }

    [Fact]
    public void CallGraph_ResolvesInProjectCalls()
    {
        var (_, edges) = Cs();
        Assert.True(HasEdge(edges, Schema.Calls, "Game.PlayerController.Attack()", "Game.Inventory.Add(Int32)"));
        Assert.True(HasEdge(edges, Schema.Calls, "Game.PlayerController.Attack()", "Game.PlayerController.TakeDamage(Int32)"));
    }

    [Fact]
    public void UnityCoupling_GetComponentAndRequireComponent()
    {
        var (_, edges) = Cs();
        Assert.True(HasEdge(edges, Schema.UsesComponent, "Game.PlayerController", "Game.Weapon"));
        // 重複は1本に畳まれる（GetComponent + RequireComponent の両方が同じ先）
        Assert.Single(edges.Where(e => e.Type == Schema.UsesComponent
            && e.FromKey == "Game.PlayerController" && e.ToKey == "Game.Weapon"));
    }

    [Fact]
    public void MonoBehaviourFlag_DetectedByBaseName_WithoutUnityAssembly()
    {
        var (nodes, _) = Cs();
        var pc = nodes.First(n => n.Key == "Game.PlayerController");
        var character = nodes.First(n => n.Key == "Game.Character");
        Assert.True(character.Props.ContainsKey("isMonoBehaviour"));
        // 派生先は基底名 MonoBehaviour を直接持たないのでフラグは付かない（直接継承のみ）
        Assert.False(pc.Props.ContainsKey("isMonoBehaviour"));
    }

    [Fact]
    public void EmbedText_IsPopulated_ForSemanticSearch()
    {
        var (nodes, _) = Cs();
        var inv = nodes.First(n => n.Key == "Game.Inventory");
        Assert.True(inv.Props.TryGetValue(Schema.PropEmbedText, out var t));
        Assert.Contains("所持品", t?.ToString());
    }

    [Fact]
    public void Assets_DependsOn_PrefabToScript()
    {
        var g = Fixture.ExtractAssets();
        // Player.prefab は Character スクリプトの guid を参照する
        Assert.Contains(g.Result.Edges, e => e.Type == Schema.DependsOn);
        Assert.Contains(g.Result.Nodes, n => n.Label == Schema.Asset && n.Props.TryGetValue(Schema.PropName, out var v)
            && v?.ToString() == "Player.prefab");
    }
}
