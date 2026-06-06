# Unity Knowledge Graph (ukg)

UnityプロジェクトのC#型・メソッド・呼び出し・Asset依存といった**静的構造**と、LLMが付与する
**意味的な繋がり**を、ひとつのナレッジグラフ（FalkorDB）に統合し、Claude等のLLMから
効率よく参照できるようにするツール。

- **静的・決定的な情報** → C#（Roslyn `Compilation`/`SemanticModel` + GUID/YAMLパーサ）が
  厳密解決で抽出（`source=static`）。継承/実装/参照に加え、**呼び出しグラフ(`CALLS`)** と
  **Unity結合(`USES_COMPONENT`: GetComponent/RequireComponent)** も張る。
- **意味検索** → 型/メソッド/概念に埋め込みを付け、FalkorDBのベクトルインデックスで
  自然文から引ける（`ukg search`）。
- **意味的な繋がり** → LLMがエッジ/概念として追記（`source=semantic`、時間性つき）。
- **概念層** → コミュニティ検出で凝集クラスタを `Concept` ノードに束ね、大規模でも航行可能に。
- 静的/意味は `source` で分離。再インデックスでLLM由来のエッジは保持され、消えた型は自動掃除
  （意味エッジ付きは `stale` 化して温存）。

インターフェースは **Skill + CLI**。常時利用するツールでMCPの常駐コンテキスト消費を避け、
Skillの漸進的開示で軽量に保つ。コアは `Ukg.Core` に分離してあり、将来MCPサーバーを
薄く被せることも可能。

設計の経緯と判断は `docs/ROADMAP.md` / `docs/DECISIONS.md` を参照。

## 構成

```
src/
  Ukg.Core/         グラフモデル・FalkorDBクライアント・リポジトリ・埋め込み・コミュニティ
  Ukg.Extractors/   CSharpExtractor(Roslyn) / AssetExtractor(.meta,YAML) / UnityManifest / IndexPipeline
  Ukg.Cli/          ukg コマンド（index/status/watch/search/candidates/query/find/neighbors/deps/impact/sem/concept/community/reflect）
unity/
  com.ukg.exporter/ Unity Editor 用エクスポータ(UPM)。AssetDatabase真値の依存をJSON出力（任意）
skills/
  unity-knowledge-graph/SKILL.md   Claude向け手続き知識
tests/
  Ukg.Eval/         抽出ゴールデン＋FalkorDB統合テスト＋検索品質eval(EvalHarness, golden/)
                    評価方法は docs/EVAL.md（内在/外在・3レーン・lift）を参照
fixtures/
  SampleUnityProject/  E2E検証用の小さなサンプル
docs/
  ROADMAP.md DECISIONS.md          実装計画と設計判断(ADR)
```

## 運用（ハイブリッド構成 / ADR-008）

エンジンは Unity 非依存のヘッドレス CLI。Claude からいつでも軽量に叩けるのが基本形。
**アセット依存の精度が要るとき**だけ、Unity Editor 側の UPM エクスポータに真値を出させて取り込む。

```
[Unity Editor] com.ukg.exporter ──→ ukg-unity-manifest.json
                                          │ (任意・あれば優先)
[headless]  ukg index <proj> [--unity-manifest …] ──→ FalkorDB ──→ Claude(Skill+CLI)
```

- マニフェスト無し: `.meta`/YAML を正規表現で解析（近似・Unity不要）。
- マニフェスト有り: `AssetDatabase.GetDependencies` の正確な依存＋`MonoScript`の厳密な script→型。
- UPM 導入と batch 実行は `unity/com.ukg.exporter/README.md` 参照。

### ライブ更新（鮮度の自動管理 / ADR-009）

**構造はイベント駆動で自動・決定的に、意味はLLMが手入れ**という役割分担で鮮度を保つ。

- `ukg index` は変更が無ければ即スキップ（内容ハッシュ差分）。コード変更時だけ実コストが出る。
- `ukg status <proj>` で鮮度判定（`fresh`/`reviewPending`）。Claude は構造の質問前にこれで確認し、
  古ければ自分で `index` してから答える（読み取り時の自己修復）。
- コード変更で古くなった**意味エッジは自動で `needsReview` 化**（stale伝播）。
  `ukg reflect` / `ukg sem review` で surface し、`sem confirm` / `sem add --supersede` で手入れ。
- 自動トリガ: `ukg watch <proj>`（ファイル監視）、git hooks（`hooks/`）、CI（`.github/workflows/ukg.yml`）。

```bash
ukg status /path/to/UnityProject     # 古い？要レビュー？
ukg watch  /path/to/UnityProject     # 変更を監視して自動再インデックス
ukg reflect                          # 意味層のメンテ候補をまとめて取得
```

## 使い方

```bash
# 1. FalkorDB 起動
docker compose up -d

# 2. ビルド & テスト
dotnet build
dotnet test                       # 抽出ゴールデン + 統合(FalkorDB)テスト

# 3. Unityプロジェクトをインデックス（静的+コミュニティ+埋め込み）
dotnet run --project src/Ukg.Cli -- index /path/to/UnityProject

# 4. クエリ
dotnet run --project src/Ukg.Cli -- search "ダメージを処理するクラス"   # 意味検索（ベクトル）
dotnet run --project src/Ukg.Cli -- candidates "player ctrl"          # 語彙あいまい検索（grep代替/トークン0, miss時はgrep誘導）
dotnet run --project src/Ukg.Cli -- find PlayerController              # 完全一致
dotnet run --project src/Ukg.Cli -- neighbors PlayerController
dotnet run --project src/Ukg.Cli -- deps Assets/Prefabs/Player.prefab
dotnet run --project src/Ukg.Cli -- impact Character --depth 4
dotnet run --project src/Ukg.Cli -- query "MATCH (n) RETURN labels(n), count(n)"

# 5. LLMが意味的エッジ・概念を追記
dotnet run --project src/Ukg.Cli -- sem add --from Player --to InventorySystem \
  --rel COLLABORATES_WITH --confidence 0.8 --why "Player picks up items into inventory"
dotnet run --project src/Ukg.Cli -- concept add --name "戦闘システム" --summary "..."
dotnet run --project src/Ukg.Cli -- sem ls
```

## グラフスキーマ

| 種別 | ラベル / タイプ |
|---|---|
| ノード | `Class` `Interface` `Struct` `Enum` `Method` `Namespace` `Asset` `Concept` |
| 静的エッジ (`source=static`) | `INHERITS` `IMPLEMENTS` `REFERENCES` `DECLARED_IN` `CALLS` `USES_COMPONENT` `IN_NAMESPACE` `DEPENDS_ON` `SCRIPT_OF` |
| 意味エッジ (`source=semantic`) | `RELATES_TO` `PART_OF` `RESPONSIBLE_FOR` `COLLABORATES_WITH` |

設定は環境変数で上書き可能:

- `UKG_REDIS` — FalkorDB接続文字列（既定 `localhost:6379`）
- `UKG_GRAPH` — グラフ名（既定 `unity`）

## 埋め込み（意味検索）の差し替え

`ukg search` の意味検索は埋め込み器に依存する。既定は外部依存なしの `HashingEmbedder`（語彙ハッシュ・
オフライン）で、**ビルド/CI/お試しはこのまま動く**が、語彙の重なりに頼るため意味検索の品質は限定的。
実プロジェクトで本物の意味検索を使うときは `UKG_EMBEDDER=http` でOpenAI互換APIに差し替える（ADR-010）。
**埋め込み器を変えると次回 `index` で全ノードを自動で再埋め込み**し、ベクトル索引も新次元で作り直す。

| 変数 | 既定 | 説明 |
|---|---|---|
| `UKG_EMBEDDER` | `hashing` | `hashing`（オフライン） / `http`（OpenAI互換API） |
| `UKG_EMBED_URL` | OpenAI | `http`時のエンドポイント |
| `UKG_EMBED_MODEL` | `text-embedding-3-small` | モデル名 |
| `UKG_EMBED_API_KEY` | — | APIキー（要れば） |
| `UKG_EMBED_DIM` | — | `http`時必須＝モデルの出力次元（例 1536） |

```bash
# OpenAI
export UKG_EMBEDDER=http UKG_EMBED_API_KEY=sk-... UKG_EMBED_DIM=1536
ukg index /path/to/UnityProject       # 全ノードを本物の埋め込みで再構築

# ローカル（Ollama 等の OpenAI 互換サーバ。APIキー不要）
export UKG_EMBEDDER=http UKG_EMBED_DIM=768 \
  UKG_EMBED_URL=http://localhost:11434/v1/embeddings UKG_EMBED_MODEL=nomic-embed-text
ukg index /path/to/UnityProject
```
