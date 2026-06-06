# ukg ロードマップ

最前線（GraphRAG / Graphiti / SCIP・Glean・CodeQL / LightRAG・HippoRAG）との差分評価を
実装計画に落とし込んだもの。**思想（静的バックボーン＋LLM意味層、Skill配信）は維持し、
欠けている「ベクトル検索・意味解決・ライフサイクル・階層化・時間性」を段階的に埋める**。

## 指針

- 各フェーズは**単独でリリース可能な価値**を持つ（縦切り）。
- 後段の判断を測れるよう、**評価ハーネス(P0)を最初に立てる**。
- `Ukg.Core` のスキーマ/リポジトリ層を壊さず拡張する（CLI・Skill後方互換）。
- 前提: 小規模開発・固定締切なし。各フェーズ末で再優先付け。

## 進捗（2026-06-06 時点）

全フェーズ実装済み。`dotnet test` で抽出ゴールデン＋FalkorDB統合の23テストが緑。
判断の経緯は `docs/DECISIONS.md`（ADR-001〜007）。

| Phase | 状態 | 実装の要点 |
|---|---|---|
| P0 | ✅ 完了 | ノード差分削除＋semantic保護(stale)、`query`読取専用化、スカラーparam化、xUnit評価23件 |
| P1 | ✅ 完了 | `IEmbedder`/`HashingEmbedder`、FalkorDBベクトル索引、`ukg search`、埋め込みキャッシュ |
| P2 | ✅ 完了 | Roslyn `Compilation`/`SemanticModel`、`Method`＋`CALLS`、`USES_COMPONENT`、impact再設計 |
| P3 | ✅ 完了 | `Concept`生成(`concept add`)、LPAコミュニティ検出、機械要約、`PART_OF`束ね |
| P4 | ✅ 完了 | `createdAt`/`validFrom`/`invalidAt`/`commitSha`、`--supersede`論理無効化、confidence降順 |
| P5 | 🟡 部分 | 並列パース＋増分埋め込み＋`contentHash`保存（フルなファイル単位差分更新は次段, ADR-007） |
| ライブ更新 | ✅ 完了 | 索引状態＋増分no-op / stale伝播(needsReview) / `status`鮮度ガード / `reflect`リフレクション / `watch`・hooks・CI（ADR-009） |

## 全体像

| Phase | テーマ | 解消する弱点 | 価値 | 依存 |
|---|---|---|---|---|
| P0 | 正確性ハードニング＋評価土台 | ノード残留 / 書込みquery / インライン化 / 計測不在 | 信頼性 | なし |
| P1 | ベクトル意味検索 | 完全一致のみ（最大欠陥） | ★最大 | P0 |
| P2 | 意味解決（Roslyn昇格＋Unity結合） | 構文止まり / impact過小評価 | ★大 | P0 |
| P3 | Concept層＋コミュニティ＋階層要約 | Concept死蔵 / フラットグラフ | 大 | P1,P2 |
| P4 | 時間性・来歴・confidence活用 | 非temporal / confidence死蔵 | 中 | P0 |
| P5 | 増分インデックス＋スケール | 毎回フル再構築 / 単スレッド | 中 | P2 |

---

## P0 — 正確性ハードニング ＋ 評価土台

> 後続フェーズを「測れる」状態にし、現状のバグを潰す。低工数で信頼を底上げ。

### スコープ
- **ノードのライフサイクル管理**: `ApplyStaticLayer` で消えた static ノードを削除／リネーム追従。
  孤児になった semantic エッジを検出（再接続 or 隔離）。
  - 実装案: 抽出結果のキー集合と既存 static ノードを差分し、`source=static` かつ未出現のノードを
    `DETACH DELETE`。semantic エッジを持つノードは消さず `stale=true` を付け、`sem` 系で警告。
  - 対象: `GraphRepository.cs:36-82`
- **`query` を読み取り専用に**: `Raw` を `ReadOnlyQuery` 経由へ。書込みは別コマンド/明示フラグに分離。
  - 対象: `GraphRepository.cs:143`, `GraphClient.cs:38-41`, `Program.cs:91-97`
- **パラメータ化クエリ**: `GRAPH.QUERY g "CYPHER k=$v ..."` のパラメータ機構へ移行し、
  巨大インライン `UNWIND` を解消。エスケープ依存（ユニコード/ヌル）を排除。
  - 対象: `Cypher.cs`, `GraphRepository.cs:48-79`, `GraphClient.cs`
- **評価ハーネス v0**: `fixtures/SampleUnityProject` を正解付きにし、
  「期待ノード/エッジ集合」「impact期待値」「検索期待ヒット」をスナップショット比較する E2E テスト。
  - 成果物: `tests/Ukg.Eval/`（xUnit）、`ukg eval` サブコマンド（任意）

### 完了条件
- クラス削除/リネーム後の再インデックスで旧ノードが残らない（テスト）。
- `query` で書込みが拒否される。
- インライン文字列クエリがコアの書込み経路から消える。
- `dotnet test` でグラフ構築のゴールデンテストが通る。

### 工数感: S–M

---

## P1 — ベクトル意味検索（最優先・最大価値）

> 「完全一致のみ」を脱却。FalkorDB ネイティブのベクトルインデックスを使い、
> 「ダメージ処理を担うクラスは？」を引けるようにする。

### スコープ
- **埋め込み付与**: 各ノードに `embedding` を持たせる。埋め込みテキストは
  `name + namespace + docコメント + シグネチャ概要 + path`。
  - 抽出時に XML docコメント／先頭サマリを収集（`CSharpExtractor` 拡張）。
  - 埋め込み生成は差し替え可能な `IEmbedder`（OpenAI/ローカル/Claude経由）。決定的キャッシュ必須
    （内容ハッシュ→ベクトルをローカルキャッシュし、再インデックスで無駄な再計算を避ける）。
- **ベクトルインデックス**: FalkorDB の `db.idx.vector.createNodeIndex` を `EnsureIndexes` に追加。
- **検索CLI**: `ukg search "<自然文>" [--label Class] [--k 10]` を新設。
  ベクトル近傍 → スコア付きで返す。`find`（完全一致）は温存。
- **ハイブリッド検索**: 近傍ヒットを起点に 1–2 hop のグラフ拡張を付けて返す
  （LightRAG/HippoRAG 的に「意味で当てて構造で広げる」）。

### 完了条件
- 名前を知らずに概念語でクラス/概念に到達できる（評価ハーネスに意味検索ケース追加）。
- 再インデックスで未変更ノードの埋め込みが再計算されない（キャッシュヒット率を計測）。

### 依存: P0（評価土台・スキーマ拡張の安全性）
### 工数感: M

---

## P2 — 意味解決：Roslyn 昇格 ＋ Unity 結合

> 構文名照合（`CSharpExtractor.cs:11-12,144-151`）を捨て、`Compilation`/`SemanticModel` による
> 正確なシンボル解決へ。`impact` の信頼性を成立させる。

### スコープ
- **Compilation 化**: `Assembly-CSharp` 相当の `Compilation` を組み、`SemanticModel` で型解決。
  同名衝突・部分クラス・ジェネリック・overload を正しく解く。
- **呼び出しグラフ**: `CALLS`（メソッド→メソッド）エッジを追加。メソッドノード `Method` を導入し、
  `REFERENCES` をフィールド/プロパティ型以外（引数・戻り値・ローカル・属性・ジェネリック制約）へ拡張。
- **Unity 結合の捕捉**（特化の本丸）:
  - `GetComponent<T>` / `AddComponent<T>` / `RequireComponent` → `USES_COMPONENT`
  - `[SerializeField]` 参照、UnityEvent、`SendMessage`、`Resources.Load`/Addressables キー
  - ScriptableObject 参照
- **impact の再設計**: 呼び出しグラフ＋Unity結合を含め、エッジ種別ごとに重み付け。
  固定 `*1..4`（`GraphRepository.cs:111-115`）をコストモデル付き探索へ。

### 完了条件
- メソッドシグネチャ変更が `impact` に反映される（評価ケース）。
- `GetComponent<T>` 等のUnity結合がエッジとして現れる。
- 同名型の誤接続が解消（衝突フィクスチャで検証）。

### 依存: P0
### 工数感: L（最重量。段階導入可: まず Compilation 化 → 呼び出しグラフ → Unity結合）

---

## P3 — Concept 層 ＋ コミュニティ検出 ＋ 階層要約

> 死蔵中の `Concept` ラベルを生かし、フラットな数千ノードを航行可能にする（GraphRAG の核）。

### スコープ
- **Concept ノード生成**: `sem add` を「両端 MATCH 必須」から拡張し、
  `ukg concept add <name> --summary ...` で概念ノードを新規作成可能に
  （`GraphRepository.cs:118-133` の MATCH 制約を緩和）。
- **コミュニティ検出**: Leiden/Louvain でモジュール分割（FalkorDB のアルゴリズム or 外部計算）。
  クラスタごとに `Concept`（=コミュニティ）ノードを自動生成し `PART_OF` で束ねる。
- **階層要約**: 各コミュニティを LLM で要約（責務・主要型・外部依存）。要約は埋め込み(P1)し、
  グローバルな問い（「このプロジェクトの戦闘系の構成は？」）に答えられるようにする。
- **マルチ解像度検索**: 検索結果を型レベル／コミュニティレベルで切替提示。

### 完了条件
- `Concept` ノードが生成・検索される（死蔵解消）。
- コミュニティ要約経由でグローバルな問いに回答できる（評価ケース）。

### 依存: P1（要約の埋め込み）, P2（正確なグラフがクラスタリング前提）
### 工数感: M–L

---

## P4 — 時間性・来歴 ＋ confidence 活用

> Graphiti 的なバイテンポラル化。書いて満足だった `confidence` を検索に効かせる。

### スコープ
- **来歴メタ**: semantic エッジ／Concept に `createdAt` `commitSha` `episode`（根拠の出所）を付与。
- **バイテンポラル**: `validFrom` / `invalidAt` を持たせ、矛盾する新事実で旧事実を無効化（論理削除）。
  履歴を辿れるようにする。
- **confidence をランクへ**: `search` / `neighbors` / impact 提示で `confidence` をスコア・閾値フィルタに使用
  （`GraphRepository.cs` の semantic 取得経路）。
- **Skill 更新**: タイムスタンプ・無効化のルールを `SKILL.md` に追記。

### 完了条件
- 矛盾する semantic エッジ追加で旧エッジが無効化され、履歴が残る。
- 低 confidence エッジが既定で検索ノイズから外れる。

### 依存: P0
### 工数感: M

---

## P5 — 増分インデックス ＋ スケール

> 毎回フル再構築（`Program.cs:41-89`, `CSharpExtractor.cs:31-49`）を脱却。

### スコープ
- **差分インデックス**: ファイル内容ハッシュ／mtime で変更ファイルのみ再抽出・再埋め込み。
- **並列走査**: ファイル列挙・パースを並列化。
- **ファイル監視**（任意）: `ukg watch` で保存時に増分更新。
- **大規模検証**: 数千〜万ファイル規模のフィクスチャでインデックス時間・クエリ遅延を計測。

### 完了条件
- 1ファイル変更時のインデックスが全体再構築より桁で速い。
- 大規模フィクスチャでクエリが実用遅延に収まる（評価ハーネスに性能ケース）。

### 依存: P2（増分の単位が正確なシンボル解決前提）
### 工数感: M

---

## シーケンス根拠

1. **P0 を最初に** — 計測できないと P1+ の良し悪しが判断できない。バグ（ノード残留・書込みquery）は
   早く潰すほど後の負債が減る。
2. **P1 を P2 より先** — ベクトル検索は単独で体験を激変させ、Roslyn昇格(P2)より低工数で価値が出る。
3. **P3 は P1・P2 の上に乗る** — 正確なグラフ(P2)と埋め込み(P1)が無いとコミュニティ/要約が機能しない。
4. **P4・P5 は土台が固まってから** — 時間性とスケールは差別化だが、検索・解決の品質が先。

## 当面の着手候補（次の一手）

- P0 の「ノードのライフサイクル管理」＋ゴールデンテスト（信頼の底上げ・低リスク）。
- 並行して P1 のベクトル検索 PoC（`IEmbedder` 抽象＋FalkorDBベクトルインデックス＋`ukg search`）。
