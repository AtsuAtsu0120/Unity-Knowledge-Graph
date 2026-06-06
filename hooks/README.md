# git hooks（自動再インデックス）

コミット/プル後に `ukg index` を自動で叩き、ナレッジグラフを最新に保つ（ADR-009）。
変更が無ければ `index` 側で即スキップされるので軽い。

## 導入

```bash
# このリポジトリの hooks/ を有効化
git config core.hooksPath hooks
chmod +x hooks/post-merge hooks/post-commit

# 対象Unityプロジェクトを指定（既定は fixtures/SampleUnityProject）
git config --local --add ukg.project /path/to/UnityProject   # 参考用メモ
export UKG_PROJECT=/path/to/UnityProject                      # 実際の指定はこちら
# Unity真値マニフェストを使うなら
export UKG_MANIFEST=/path/to/ukg-unity-manifest.json
```

## 注意

- FalkorDB が起動していない環境（CI のチェックアウト等）では index は失敗するが、
  hook はコミット/マージを止めない（エラーは警告に握りつぶす）。
- 常時同期したい場合は hook の代わりに `ukg watch <proj>` を別プロセスで回す手もある。
