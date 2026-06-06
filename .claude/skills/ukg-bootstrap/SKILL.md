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

- サブシステム／責務ごとに概念ノードを立てる（要約は意味検索の対象になる）：
  ```bash
  ukg concept add --name "Combat" --summary "ダメージ計算・被弾・体力管理を担う戦闘サブシステム"
  ```
- 概念と実コード（クラス／メソッド）を意味エッジで結ぶ。許可リレーション＝
  `RESPONSIBLE_FOR` / `COLLABORATES_WITH` / `PART_OF` / `RELATES_TO`：
  ```bash
  ukg sem add --from "Combat" --to "PlayerController" --rel RESPONSIBLE_FOR \
              --why "プレイヤーの攻撃・被弾処理を担う" --confidence 0.8
  ukg sem add --from "PlayerController" --to "Inventory" --rel COLLABORATES_WITH \
              --why "アイテム使用でインベントリを参照" --confidence 0.7
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
