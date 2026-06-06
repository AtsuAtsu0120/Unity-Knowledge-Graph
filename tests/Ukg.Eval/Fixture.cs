using Ukg.Extractors;

namespace Ukg.Eval;

/// <summary>テスト用フィクスチャの場所解決と抽出のヘルパ。</summary>
internal static class Fixture
{
    /// <summary>リポジトリルート（UnityKnowledgeGraph.sln のあるディレクトリ）を上方向に探す。</summary>
    public static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "UnityKnowledgeGraph.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("UnityKnowledgeGraph.sln が見つからない");
    }

    public static string SampleProject() =>
        Path.Combine(RepoRoot(), "fixtures", "SampleUnityProject");

    public static CSharpGraph ExtractCs() => new CSharpExtractor().Extract(SampleProject());
    public static AssetGraph ExtractAssets() => new AssetExtractor().Extract(SampleProject());
}
