# E2E location タスク集（外在評価・①理解/特定）

「**grep だけの LLM vs ukg を持つ LLM**」を同一プロジェクト・同一タスクで A/B 比較するための、
**読み取り専用の特定タスク**集（docs/EVAL.md の外在評価レーン）。コードを書かせず質問に答えさせ、
回答（ファイルパス or シンボル名）を gold と照合する。

## なぜ読み取りタスクから始めるか
- コードを変えないのでプロジェクトが汚れず、同一条件で N 回回せる
- 採点が機械的（ファイル/シンボル一致）
- ukg の価値（探索）を実装力と切り離して測れる
（実装タスク=②は後段。git worktree で1回ずつ＋テスト採点が要る）

## A/B の鉄則
**同じタスク・同じプロジェクトで、エージェントが持つツールだけを変える。**
- B群（baseline）: grep / read のみ
- A群（treatment）: ukg（candidates/search/neighbors/impact）＋ grep フォールバック
プロジェクトを A/B で分けてはいけない（交絡する）。

## プライバシー境界（重要）
- **ハーネス・フォーマット・公開サンプルはこのリポジトリに public**。
- **private リポジトリ由来のタスク（ファイルパス・クラス名・正解）は `*.local.json` に置く＝gitignore 済み**。
  例: `arcade.local.json`（arcade-digloupe は private なのでコミットしない）。
- 公開・再現可能なベンチが欲しくなったら OSS Unity プロジェクトでコーパスを足す。

## ファイル形式

```jsonc
{
  "corpus": "~/Develop/arcade-digloupe",   // 対象 Unity プロジェクト
  "graph": "arcade",                        // UKG_GRAPH
  "k": 5,                                    // 上位何件で正解判定するか
  "tasks": [
    {
      "id": "loc-approot",
      "prompt": "...クラスはどれ？ファイルパスで答えて。",
      "lang": "ja",                          // ja | en
      "angle": "semantic",                   // structure（名前で引ける） | semantic（概念で引く）
      "difficulty": "easy",                  // easy | med | hard
      "exists": true,                        // false = 「存在しない＝not found が正解」（負例/フォールバック検証）
      "gold": {
        "files":   ["Assets/.../Foo.cs"],    // どれか1つ当たれば正解
        "symbols": ["Foo"]
      }
    }
  ]
}
```

## ランナー（次ステップ・未実装）
確率的なのでxUnitではなくエージェント駆動ハーネス（Workflow/script）で回す:
1. 各 task × {A=ukg, B=grep} × N回、同一 prompt でエージェント実行
2. トランスクリプトから抽出: 消費トークン / ツール呼び出し回数 / 最終回答
3. 採点: gold の files/symbols といずれか一致で○。`exists:false` は「not found」と言えたら○
4. 集計（docs/EVAL.md と同じ形）: 成功率・正解1件あたりトークン・grep回数・**A/Bのトークン差**
5. 台帳両側: ukg のツールスキーマ＋結果トークンも A 側コストに計上。種付け投資 vs 累計削減＝**償却曲線**
6. 退行も見る: false-high で grep に落ちず失敗したケース数

## 走らせ方（予定）
```bash
# 例（ランナー実装後）
ukg-eval-e2e tests/Ukg.Eval/golden/e2e/arcade.local.json --runs 5
```
