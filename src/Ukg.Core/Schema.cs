namespace Ukg.Core;

/// <summary>
/// グラフのラベル・エッジタイプ・プロパティキー・source 値の定数定義。
/// 抽出器・リポジトリ・CLI すべてがここを参照し、文字列の食い違いを防ぐ。
/// </summary>
public static class Schema
{
    // ノードラベル
    public const string Class = "Class";
    public const string Interface = "Interface";
    public const string Struct = "Struct";
    public const string Enum = "Enum";
    public const string Namespace = "Namespace";
    public const string Asset = "Asset";
    public const string Concept = "Concept";

    // 静的エッジ（source=static）
    public const string Inherits = "INHERITS";        // Class -> Class
    public const string Implements = "IMPLEMENTS";     // Class -> Interface
    public const string References = "REFERENCES";     // Type -> Type（フィールド/プロパティ型）
    public const string InNamespace = "IN_NAMESPACE";  // Type -> Namespace
    public const string DependsOn = "DEPENDS_ON";      // Asset -> Asset
    public const string ScriptOf = "SCRIPT_OF";        // Asset(.cs) -> Class

    // 意味エッジ（source=semantic）
    public const string RelatesTo = "RELATES_TO";
    public const string PartOf = "PART_OF";
    public const string ResponsibleFor = "RESPONSIBLE_FOR";
    public const string CollaboratesWith = "COLLABORATES_WITH";

    // プロパティキー
    public const string PropKey = "key";          // 安定キー（クラス=完全修飾名 / Asset=GUID）
    public const string PropName = "name";         // 表示名
    public const string PropSource = "source";     // 'static' | 'semantic'
    public const string PropPath = "path";         // ファイルパス（Asset/型）
    public const string PropGuid = "guid";         // Unity GUID（Asset）
    public const string PropConfidence = "confidence";
    public const string PropRationale = "rationale";
    public const string PropAuthor = "author";

    // source 値
    public const string SourceStatic = "static";
    public const string SourceSemantic = "semantic";

    /// <summary>意味エッジとして許可されるリレーションタイプ。</summary>
    public static readonly string[] SemanticRelations =
    {
        RelatesTo, PartOf, ResponsibleFor, CollaboratesWith
    };

    public static bool IsSemanticRelation(string rel) =>
        Array.Exists(SemanticRelations, r => r.Equals(rel, StringComparison.OrdinalIgnoreCase));
}
