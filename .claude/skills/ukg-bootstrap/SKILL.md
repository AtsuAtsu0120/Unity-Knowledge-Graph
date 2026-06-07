---
name: ukg-bootstrap
description: Unity Knowledge Graph (ukg) を対象 Unity プロジェクトに対して初期化（ブートストラップ）するスキル。構造グラフを自動生成したうえで、Claude がコード全体を大まかに把握して意味層（concept / 意味エッジ）を種付けし、「意味0スタート」を回避する。ユーザーが「ukg 初期化」「KG 立ち上げ」「ナレッジグラフをブートストラップ」「グラフに意味を入れて」「ukg bootstrap」などと言ったときに使う。新しい Unity プロジェクトを ukg に取り込む意図が読み取れたら積極的に使うこと。
---

# ukg ブートストラップ

対象 Unity プロジェクトの Knowledge Graph を、**エージェントが grep せずに問い合わせられる状態**まで一気に立ち上げる。

設計思想（ADR-009 / ADR-011）：
- **構造グラフ（クラス・依存・呼び出し・guid）は `ukg index` が Roslyn で自動生成する。トークンは1滴も使わない。**
- **Claude が使うトークンは「意味層の種付け」に集中投下する** — コードを読んだだけでは出てこない「責務」「協調関係」を `concept add` / `sem add` で書き戻す。
- これにより次のエージェント（Claude Code でも Codex でも）は `candidates` / `search` でグラフに問うだけで答えが返り、grep に戻らなくて済む。

**重要な原則：意味の種付けは「大まか」でよい。全ファイルを読むな。** 主要サブシステムの責務と関係を押さえることが目的。網羅は後続の運用（使いながら `sem add`）が育てる。

**言語・量の方針（トークン節約）：**
- **意味層（concept summary / sem --why）は英語で書く。日本語併記は任意。** 消費者は主にエージェントで英語クエリで引くため、英語のみで機械の検索経路は完全に成立する。人間向けの日本語説明はコードの `///` doc が担う。
- **docs（`///` コメント）とコードは触らない。** 識別子は元から英語で検索に効くので、doc の英語化・併記は不要（コスパが悪い）。
- **名前から自明なものに concept を作らない。** 例: `SQLiteConnection` に「DB接続」概念は不要。意味層は「**名前から分からない非自明な知識**」だけを少数精鋭で。種付け1回のトークンは全セッションで償却される。

---

## 前提チェック

1. FalkorDB が起動しているか（既定 `localhost:6379`）。未起動なら起動を促す。
2. CLI をビルド：
   ```bash
   dotnet build
   ```
   以降 `ukg` は `dotnet src/Ukg.Cli/bin/Debug/net9.0/Ukg.Cli.dll <args>` で実行する（エイリアスがあればそれで可）。
3. 環境変数（任意）：`UKG_GRAPH`（既定 `unity`）、`UKG_REDIS`。複数プロジェクトを扱うなら `UKG_GRAPH` をプロジェクトごとに分ける。

---

## 手順

### Step 1 — 構造グラフを自動生成（トークン0）

```bash
ukg index <unityProjectPath>
```

- Unity の真の依存を使いたい場合は Editor エクスポータの出力を `--unity-manifest <path>` で渡す（無ければ guid 正規表現フォールバック）。
- 出力 JSON の `nodes` / `edges` / `communities` を確認。`communities >= 1` を確認できれば骨格はできている。

```bash
ukg status <unityProjectPath>   # fresh=true を確認
```

### Step 2 — 全体像を「大まかに」把握（ここで初めて読む）

grep で漁らず、**グラフに骨格を出させてから**ピンポイントで読む：

1. コミュニティ（自動クラスタ）を骨格として読む：
   ```bash
   ukg community
   ```
   各コミュニティのメンバーが「サブシステムの当たり」になる。
2. 中心ノード（次数が高い＝重要）を出す：
   ```bash
   ukg query "MATCH (n)-[r]-() WHERE n.source='static' RETURN labels(n)[0] AS label, n.name AS name, count(r) AS deg ORDER BY deg DESC LIMIT 20"
   ```
3. Unity のエントリポイント（MonoBehaviour 継承など）を出す：
   ```bash
   ukg query "MATCH (c:Class)-[:INHERITS|IMPLEMENTS]->(b) WHERE b.name CONTAINS 'MonoBehaviour' OR b.name CONTAINS 'ScriptableObject' RETURN c.name, b.name LIMIT 30"
   ```
4. **上位コミュニティ／中心ノードに対応するファイルだけ**を数本読み、各サブシステムの責務を1〜2行で言語化する。**全ファイルは読まない。**

### Step 3 — 意味層を種付け（トークンを使う本番）

把握した内容を、コードに固定されない「意味」としてグラフに書き戻す。

- サブシステム／責務ごとに概念ノードを立てる（要約は意味検索の対象になる。**summary は英語で**）：
  ```bash
  ukg concept add --name "Combat" --summary "Combat subsystem: damage calculation, taking hits, health management"
  ```
- 概念と実コード（クラス／メソッド）を意味エッジで結ぶ。許可リレーション＝
  `RESPONSIBLE_FOR` / `COLLABORATES_WITH` / `PART_OF` / `RELATES_TO`（**--why も英語で**）：
  ```bash
  ukg sem add --from "Combat" --to "PlayerController" --rel RESPONSIBLE_FOR \
              --why "handles player attack and damage-taking" --confidence 0.8
  ukg sem add --from "PlayerController" --to "Inventory" --rel COLLABORATES_WITH \
              --why "reads inventory when using items" --confidence 0.7
  ```
- `--confidence` は確信度（自分で読んで確かめた=0.8前後、推測混じり=0.5前後）。`--author` は既定 `llm`。
- **目安：主要サブシステム 3〜8 個、それぞれに 2〜5 本の意味エッジ。** 多すぎる低確信エッジより、確かな少数を入れる。

### Step 4 — 反映と検証

1. `concept add` は自動で埋め込みを反映する。手動でまとめて反映したい場合：
   ```bash
   ukg community            # 必要なら再クラスタ＋再埋め込み
   ```
2. 種付けの健全性を確認：
   ```bash
   ukg reflect              # 要レビュー/低confidence/重複概念/未整理コミュニティを集約
   ```
   `duplicateConcepts` があれば統合、`lowConfidence` は再検証。
3. **grep不要が成立したかを実際に確かめる**（このスキルのゴール）：
   ```bash
   ukg candidates "<うろ覚えの語彙>"   # confidence=high で当たるか
   ukg search "<概念的な問い>"          # 意味検索で責務に当たるか
   ```
   `candidates` が `confidence: none` を返す主要語彙があれば、Step 3 の種付けが不足しているサイン。補う。

---

## 完了の目安

- `ukg status` が `fresh=true`。
- 主要サブシステムが `concept` として存在し、実コードへ意味エッジで接続されている。
- 代表的な「うろ覚えの語彙」「概念的な問い」が `candidates` / `search` で当たる（grep に落ちない）。
- `ukg reflect` に未処理の重複・低確信が大量に残っていない。

以降は運用フェーズ：エージェントは `candidates`（語彙・トークン0）→ ヒット無し時のみ grep → 理解したら `sem add` で書き戻す、のループでグラフを育てる。

## 意味層を育てる2系統（保守 ＋ 増築）

意味層の更新は「保守（既存を直す）」と「増築（厚みを増やす）」の2系統。両方を定期的に回す。

- **保守 = `ukg reflect`**: needsReview / 低confidence / stale / 重複概念 を検知。
  `sem confirm`・`sem add --supersede`・概念統合で解消する。**厚みは増えない**（品質維持）。
- **増築 = `ukg gaps`**: 意味層を厚くすべき場所を出す。
  - `missedQueries`: `candidates` が答えられなかった（none/low）クエリを頻度順＝**実需の地図**。
    該当エリアを読んで `concept/sem add` する。需要に沿って育つ。
  - `uncoveredHubs`: 構造的に中心的なのに意味づけ0の**型**。責務を `sem add` する。
    `--exclude` で vendored/生成を除外（カンマ区切り複数可・hubs/communities 両方に適用。例: `ukg gaps --exclude SQLite,Generated,Formatters`）。
  - `uncuratedCommunities`: 自動クラスタで未概念化のもの → `concept add`。
- **定期キュレーション**: スケジュール（`/schedule` 等）で「`reflect` 解消 ＋ `gaps` 埋め戻し」を回すと、
  意味層が「放っておいても保守＋増築される」状態に近づく。増築の効果は eval の lift で確認できる。
- いずれも **英語・非自明優先**（名前で自明な vendored 型には concept を作らない）。

## 運用ルール（エージェント向け・重要）

グラフは**英語語彙**で構築される（識別子＝英語、意味層も英語）。だから:

- **ukg を引くときは英語のコード用語で引く。** 日本語の質問は**英語キーワードに直してから** `candidates`/`search` する。
  - 例: ユーザーの問い「選択肢ボタンの入力は？」→ `ukg candidates "option button input"`（×「選択肢ボタン入力」）。
  - 日本語のまま引くと `confidence:none` になりやすく、grep に落ちて誤着地する（実測で確認済み）。
- `candidates` の応答に `retryEnglish:true` が出たら、それは「日本語で引いて空振りした」サイン。**grep に落ちる前に英語で引き直す**。
- サブシステム（複数クラスにまたがる責務）を問われたら `ukg neighbors <Concept名>` で一括取得する。各クラスを個別に grep し直さない。
- `confidence:high` は信頼してよい。`none`/`low` の時だけ grep にフォールバックし、理解したら `sem`/`concept add` で書き戻す。

対象リポジトリの `CLAUDE.md` に次の1行を入れると徹底できる:
> コード探索は ukg を**英語のコード用語**で引く（candidates→none/low の時のみ grep）。日本語の質問は英語キーワードに直してから引く。理解した構造は `ukg sem/concept add`（英語）で書き戻す。
