# ukg 評価ガイド

ukg の価値は「**エージェントが grep せず即答を得て、トークンが減り、使うほど育つ**」こと。
評価もそこに紐づける。「テストが通る」≠「ツールとして良い」。

## 大原則：内在評価と外在評価を分ける

| | 内在評価 (intrinsic) | 外在評価 (extrinsic) |
|---|---|---|
| 問い | グラフは正しい答えを返すか | 実タスクでトークン/成功率が改善するか |
| 性質 | 安い・再現可能・**CIで毎回** | 高い・非決定的・**定期的に** |
| 役割 | リグレッション検知 | 価値の最終証明 |

## 交絡に注意：エンジン改善 と グラフ成長 を混同しない

`Recall = f(エンジン, コーパス, 種付け, embedder)`。素の Recall が上がっても、
**ukg が良くなったのか／グラフが育っただけ**か区別できない。
変数を固定して1つだけ動かす（対照実験）。そのために**種付けはコード化**する
（`tests/Ukg.Eval/golden/curation.json` ＝ 再現可能な定数）。

## 3レーン

評価対象は3つ。それぞれ別レーンで測る（`tests/Ukg.Eval/EvalHarness.cs`）。

### レーンA — コールドスタート（純エンジン）
- 条件: `index` のみ。構造＋オフライン埋め込み、**種付け0**。
- 測る: Recall@k / MRR / miss較正。
- 意味: 種付けが一切混ざらないので、**スコアの変化＝100%エンジン由来**。「ukg の地力」。

### レーンB — 種付け後（エンジン＋固定curation）
- 条件: コーパス・エンジン固定のまま `curation.json` を適用。
- 測る: 種付け後 Recall（総合点）。
- **lift = B.Recall − A.Recall** ＝ コーパス・エンジン固定下での**意味づけの貢献**。

### レーンC — 意味づけ方法・bootstrapスキルの評価（半自動）
スキル（コードベース → 意味層を生成するエージェント）の良し悪しを測る。
- 固定: コーパス(commit pin)・エンジン・embedder。
- 動かす: **スキル/方針**（例: 英語のみ vs 日英併記、概念5個 vs 15個、プロンプト差分）。
- 手順:
  1. スキルを走らせて意味層を生成（＝ `curation.json` 相当を自動生成）。
  2. その状態でゴールデン集を採点 → レーンB と同じ要領で lift を算出。
  3. スキルは**非決定的**なので **N回回して lift の平均と分散**を見る。
- スキル固有の指標:
  - **lift / token**（スキルが使ったトークンあたりの Recall 押し上げ）＝効率。方針比較の主指標。
  - 種付けエッジの precision（concept→class が実際正しいか／独立判定）。
  - 非自明サブシステムのカバレッジ（事前作成の「拾うべきリスト」の充足率）。
  - confidence 較正（付けた 0.8 が実際 8割正しいか）。
  - `reflect` 健全性（重複・低確信・孤児の残量）。

## curation 非依存でエンジンだけを測れる指標（成長と完全に切り離せる）

ゴールデン集を待たずとも、以下は構造とエンジンだけの関数：
- **抽出 precision/recall**: INHERITS/CALLS/DEPENDS_ON/USES_COMPONENT/SCRIPT_OF を Roslyn/Unity 真値と照合（`IntegrationTests` 拡張）。`regex-fallback vs unity-manifest` の差で exporter 価値も測る。
- **コールドスタート Recall@k**（レーンA）。
- **miss シグナル混同行列**（下記）。
- **性能**: index 時間・クエリ遅延・DBサイズの対プロジェクト規模スケール。

## メトリクス定義

- **Recall@k**: 正例のうち、上位 k に期待ノードが1つでも入った割合。
- **MRR**: 最初の期待ノードの順位の逆数の平均。
- **miss シグナル（candidates のみ）**: `confidence` を high/low/none の3階層で出し、
  「答えられるか」の二値分類器として混同行列で評価。**誤りは非対称**：
  - **false-high（high と言ったが誤り）= 最悪**。エージェントが信じ grep せず誤答を掴む。**ゲートで常に 0 を要求**。
  - false-none（none だが答えはあった）= 軽い。無駄に grep するだけ。
  - 閾値（現 `1.0`）は点でなく**スイープして precision/recall 曲線**で較正する。

## 外在評価（本命KPI）

同じ実タスク群を2条件で走らせ比較：
- A群: ukg ツールあり（candidates/search/neighbors/impact）
- B群: grep/read のみ
- 測る: **消費トークン / grep・read 回数 / 所要時間 / タスク成功率**。
- 成長: セッションを重ねて Recall の時系列と**償却曲線**（種付けトークン累計 vs 削減累計の交差点）。

## 落とし穴

1. **循環論法**: 種付けした本人が、それ用のクエリで採点しない。評価クエリは種付けと独立に作る（理想は実タスク由来）。
2. **curation 交絡**: 必ずコールドスタートと種付け後を別々に出す。
3. **embedder は変数**: `hashing` vs `http(OpenAI)` で評価マトリクスを分ける（特に日英横断）。
4. **無音の打ち切りを報告**: top-N 制限・サンプリング等で評価範囲を絞ったらログに残す。

## 走らせ方

```bash
# レーンA/B ＋ lift ＋ 回帰ゲート（要 FalkorDB。未起動ならスキップ）
dotnet test --filter "FullyQualifiedName~EvalHarness" --logger "console;verbosity=detailed"
```

- ゴールデン集: `tests/Ukg.Eval/golden/queries.json`（正例/負例/curation依存を含む）
- 固定 curation: `tests/Ukg.Eval/golden/curation.json`
- 回帰ゲート: `EvalHarness.cs` の `ColdStartRecallFloor` / `CuratedRecallFloor` と `false-high == 0`。
  改善したら floor を引き上げてラチェットにする。

## 育て方

- 最初は小さく（SampleUnityProject で 30〜50問）。
- **CLI のクエリログ**（どんな問いが来て none/grepに落ちたか）を収集し、実分布をゴールデン集に還元する。
- 実 Unity プロジェクト（arcade 等）でコーパス多様性を足す。種付け済みグラフでは循環に注意した設計にする。
