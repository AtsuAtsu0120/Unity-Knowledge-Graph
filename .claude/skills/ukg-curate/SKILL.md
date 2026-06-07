---
name: ukg-curate
description: ukg の意味層を継続的に「増築＋保守」する actuator スキル。ukg gaps / reflect を読み、誤った意味づけを多層ゲート（構造的根拠→敵対的検証→eval回帰→可逆な来歴）で防ぎながら、少数精鋭で concept / sem を自動追加・整える。ユーザーが「ukg を育てて」「意味層を増築」「curation を回して」「ukg curate」などと言ったとき、または定期キュレーションを実行するときに使う。bootstrap（初期化）と対：こちらは継続成長。
---

# ukg-curate（意味層の継続キュレーション）

`ukg gaps`（増築 TODO）と `ukg reflect`（保守 TODO）を入力に、意味層を自動で育てる。
**最重要原則：誤った意味づけを「人の目視」でなく「仕組み」で防ぐ。** だから提案は必ず多層ゲートを通す。
これにより無人スケジュール実行（`/schedule`）でも安全に回せる。

bootstrap=初期化（0→1）に対し、curate=継続増築/保守（1→n）。

## 前提
```bash
cd <projectDir> && source .ukg.env    # UKG_GRAPH を設定
```
`ukg` は `~/.local/bin/ukg`。意味層は**英語・非自明優先**（名前で自明な vendored 型に concept を作らない）。

## コスト方針（②だけが有料・リスク比例で絞る）
ゲートのうち **①構造的根拠・③eval・④来歴はトークンほぼ0**（Cypher / ローカルテスト / タグ）。
有料なのは **②敵対的検証（refuter の fan-out）だけ**。日々の検索は一切変わらない（curation は定期＝償却投資）。
②は次の方針で増やしすぎない：
- **①で殺せるものは②に回さない**：構造的根拠が強い（1ホップ＋明確な evidence）型エッジは②スキップ or N=1。
- **既定 N=1。N=3 にするのは「概念→型エッジ」「低confidence」「中心的で影響大」な提案のみ**（①が効かない所に集中）。
- **refuter は安いモデル（Haiku）**で回す（反証は焦点の狭い判定なので小型で十分）。
- **バッチ上限**（下記）で②の総コールを頭打ち。タダの③に systemic な保険を寄せ、②は最小限の上積みに。

## 境界（必ず守る・無人実行の安全弁）
- **1回あたり上限**: 増築は gaps 上位 **最大5件**、保守は reflect の needsReview 上位 **最大5件**。
- **トークン予算**を意識して打ち切る。残りは次回に回す（gaps/reflect は累積するので取りこぼしOK）。
- **すべての自動追加は `--author auto-curate`** でタグ付け（来歴・一括リバート用）。
- 不確実なものは**低 confidence（≤0.6）**で入れる → 次回 `reflect` が拾い直す（自己修正）。

## パイプライン（提案 → 4層ゲート → 反映）

### Step 0 — TODO を取得
```bash
ukg gaps --limit 8 --exclude SQLite --exclude Generated   # 増築候補
ukg reflect                                                # 保守候補
```
増築は `missedQueries`（実需）＞ `uncoveredHubs`（中心だが薄い）＞ `uncuratedCommunities` の順で優先。

### Step 1 — 提案（必ず根拠付き）
各候補について **実際にコードを読む**（該当クラス/エリアを Read）。読んだ上で：
- 概念か責務かを決める（例: uncoveredHub `SoundManager` → 「Audio」概念の責務）。
- **根拠を file:line で明示**してから提案する。コードを見ずに意味づけしない（幻覚防止）。

### Step 2 — ① 構造的根拠ゲート（決定的・無料）
型↔型の意味エッジは `--require-basis` 付きで追加する。構造的接続が無い辺は ukg が**自動拒否**する：
```bash
ukg sem add --from <TypeA> --to <TypeB> --rel COLLABORATES_WITH \
            --why "<evidence: file:line>" --confidence 0.7 \
            --author auto-curate --require-basis --basis-hops 3
```
- 概念→型の辺は静的エッジを持たないため根拠チェック対象外（②③で担保）。
- 事前確認したいときは `ukg basis <A> <B> --hops 3`。

### Step 3 — ② 敵対的検証（subtle な誤りを catch・主レバー）
構造的に近くても**責務の解釈が誤っている**ことがある。各提案を**別の独立サブエージェントに反証させる**：
- 検証エージェントに「主張＋根拠コード」を渡し、**デフォルト反証寄り**で「これは誤りか？」を判定させる。
- 重要な提案は **N=2〜3 体**で多数決。過半数が反証できなければ採用、できたら破棄。
- （Workflow が使えるなら、提案ごとに refuter を fan-out して並列検証すると速い。）

### Step 4 — 反映（staging）＋ ④ 来歴
検証を通った提案だけを反映：
- `concept add` / `sem add --author auto-curate`（②で確信が高ければ confidence 0.75、曖昧なら ≤0.6）。
- 反映した内容を**サマリ出力**（何を・なぜ・confidence・根拠）。監査できる形で残す。

### Step 5 — ③ eval 回帰ゲート（systemic）
バッチ反映後、検索品質が劣化していないか確認：
```bash
cd <ukgRepo> && dotnet test --filter EvalHarness   # false-high=0 / recall 非回帰
```
- **false-high が出る or recall が下がったらバッチをロールバック**（下記）。意味層版の CI。
- 対象プロジェクト固有の recall を測るには、そのプロジェクトの golden 集が要る（無ければ engine 回帰のみ確認）。

### Step 6 — ロールバック（④ 可逆性）
バッチが eval を通らない／後で誤りと分かったら、このrunの自動追加を論理無効化：
```bash
ukg query --write "MATCH ()-[r {author:'auto-curate'}]->() WHERE r.invalidAt IS NULL AND r.createdAt = '<thisRunIso>' SET r.invalidAt = '<nowIso>'"
```
バイテンポラルなので履歴は残しつつ無効化できる。

## 保守パス（reflect 由来）
- `needsReview`: 構造を確認 → 妥当なら `ukg sem confirm`、変わっていれば `ukg sem add --supersede`。
- `duplicateConcepts`: 統合（一方に寄せて他方を supersede/無効化）。
- `lowConfidence`/`stale`: 再検証して confidence 更新 or 無効化。

## 完了レポート（毎回出す）
- 増築: 追加した concept/sem（根拠・confidence）、①で拒否された件数、②で破棄された件数
- 保守: confirm/supersede/統合した件数
- ③ eval: 結果（pass/rollback）
- 次回への持ち越し（上限で処理しきれなかった TODO 件数）

## ⑤ 使用フィードバック（将来）
ある sem エッジ経由でエージェントが誤答した（E2E/実使用で検知）ら、そのエッジを降格/無効化する自己修正は次段。
現状は ①構造 → ②敵対検証 → ③eval → ④可逆 の4層で誤り混入を防ぐ。

## スケジュール化（A: 勝手に育つ）
品質を手動運用で確認できたら `/schedule` で定期実行（毎晩/週次）。
注意: グラフはローカル FalkorDB なので、cron も**同じマシンで**動かす必要がある。
