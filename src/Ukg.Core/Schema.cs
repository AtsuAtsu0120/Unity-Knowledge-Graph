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
    public const string Method = "Method";     // P2: メソッド単位ノード
    public const string Meta = "UkgMeta";      // 索引状態などの内部メタ（グラフに1つ）

    // 静的エッジ（source=static）
    public const string Inherits = "INHERITS";        // Class -> Class
    public const string Implements = "IMPLEMENTS";     // Class -> Interface
    public const string References = "REFERENCES";     // Type -> Type（フィールド/プロパティ/引数/戻り値）
    public const string InNamespace = "IN_NAMESPACE";  // Type -> Namespace
    public const string DependsOn = "DEPENDS_ON";      // Asset -> Asset
    public const string ScriptOf = "SCRIPT_OF";        // Asset(.cs) -> Class
    public const string DeclaredIn = "DECLARED_IN";    // P2: Method -> 宣言型
    public const string Calls = "CALLS";               // P2: Method -> Method
    public const string UsesComponent = "USES_COMPONENT"; // P2: Type -> Type（GetComponent<T>等のUnity結合）

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

    // P1: 埋め込み・要約
    public const string PropEmbedding = "embedding";   // vecf32 ベクトル
    public const string PropEmbedText = "embedText";   // 埋め込み元テキスト
    public const string PropEmbedHash = "embedHash";   // 埋め込み元テキストの内容ハッシュ（再計算回避）
    public const string PropDoc = "doc";               // XMLドキュメント/サマリ
    public const string PropSignature = "signature";   // 型/メソッドのシグネチャ概要
    public const string PropSummary = "summary";       // Concept/コミュニティ要約

    // P2: 増分・解析メタ
    public const string PropContentHash = "contentHash"; // ファイル内容ハッシュ（増分判定）

    // P3: コミュニティ
    public const string PropCommunity = "community";   // コミュニティID
    public const string PropMembers = "members";       // Concept(コミュニティ)の構成要素数

    // P0: ライフサイクル
    public const string PropStale = "stale";           // 抽出から消えたが semantic 接続で温存

    // P4: 時間性・来歴
    public const string PropCreatedAt = "createdAt";   // 作成時刻(ISO8601)
    public const string PropValidFrom = "validFrom";   // 有効開始
    public const string PropInvalidAt = "invalidAt";   // 論理無効化時刻（存在＝無効）
    public const string PropCommitSha = "commitSha";   // 根拠コミット

    // ライブ更新（ADR-009）
    public const string PropState = "state";           // 索引状態(JSON, UkgMeta)
    public const string PropNeedsReview = "needsReview"; // 構造変更で要再確認の意味エッジ
    public const string PropReviewReason = "reviewReason";
    public const string PropConfirmedAt = "confirmedAt"; // 再確認した時刻

    // source 値
    public const string SourceStatic = "static";
    public const string SourceSemantic = "semantic";

    /// <summary>意味エッジとして許可されるリレーションタイプ。</summary>
    public static readonly string[] SemanticRelations =
    {
        RelatesTo, PartOf, ResponsibleFor, CollaboratesWith
    };

    /// <summary>影響分析でたどる静的依存エッジ（逆向きにたどると上流＝影響範囲）。</summary>
    public static readonly string[] ImpactRelations =
    {
        References, DependsOn, ScriptOf, Inherits, Implements, Calls, UsesComponent
    };

    /// <summary>埋め込み索引を張る（＝意味検索の対象になる）ラベル。</summary>
    public static readonly string[] EmbeddableLabels =
    {
        Class, Interface, Struct, Enum, Method, Concept
    };

    public static bool IsSemanticRelation(string rel) =>
        Array.Exists(SemanticRelations, r => r.Equals(rel, StringComparison.OrdinalIgnoreCase));
}
