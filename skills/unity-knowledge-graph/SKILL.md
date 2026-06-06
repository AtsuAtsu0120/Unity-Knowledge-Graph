---
name: unity-knowledge-graph
description: >
  Query and enrich the Unity project's knowledge graph (FalkorDB) via the `ukg` CLI.
  Use when you need to understand C# class/method relationships (inheritance, interfaces,
  references, call graph, GetComponent coupling), Unity asset dependencies (prefabs/scenes
  → scripts), search the codebase by meaning, trace change impact, or record semantic
  relationships between systems. Trigger when the user asks about Unity code structure,
  "what uses X", "what calls Y", "find the class that handles Z", "what does this prefab
  depend on", "impact of changing W", or asks you to remember a conceptual link.
---

# Unity Knowledge Graph (ukg)

UnityプロジェクトのナレッジグラフをFalkorDBから参照・拡張するためのスキル。
**静的構造**（C#の型・メソッド・呼び出し・Asset依存）はC#抽出器が `source=static` で構築済み。
**意味的な繋がり**はあなた（LLM）が `source=semantic` のエッジ/概念として追記する。

## 前提

- FalkorDBが起動していること: `docker compose up -d`
- グラフが構築済みであること: `dotnet run --project src/Ukg.Cli -- index <UnityProjectPath>`
  （コード変更後は再実行。静的レイヤーのみ作り直し、意味エッジは保持される。消えた型は自動削除、
  ただし意味エッジが付いた型は `stale` 化して温存される）

CLIは全コマンドJSONを返す。`UKG_REDIS`(既定 localhost:6379) / `UKG_GRAPH`(既定 unity) で接続先を変更可。

## スキーマ

ノード: `Class` `Interface` `Struct` `Enum` `Method` `Namespace` `Asset` `Concept`
（各ノードに安定キー `key`＝型は完全修飾名 / メソッドは `型FQN.名前(引数型)` / Assetはguid。
`name` `path` `source` を持つ。型/メソッド/Concept には意味検索用の埋め込みが付く）

| エッジ | source | 意味 |
|---|---|---|
| `INHERITS` | static | Class → 基底Class |
| `IMPLEMENTS` | static | Class → Interface |
| `REFERENCES` | static | 型 → フィールド/プロパティ/引数/戻り値で使う型 |
| `DECLARED_IN` | static | Method → 宣言した型 |
| `CALLS` | static | Method → 呼び出すMethod（プロジェクト内のみ） |
| `USES_COMPONENT` | static | 型 → `GetComponent<T>` / `[RequireComponent(typeof(T))]` の T |
| `IN_NAMESPACE` | static | 型 → Namespace |
| `DEPENDS_ON` | static | Asset → 参照先Asset（prefab→script等） |
| `SCRIPT_OF` | static | .cs Asset → Class |
| `PART_OF` | semantic | メンバ → Concept(コミュニティ/概念) |
| `RELATES_TO` `RESPONSIBLE_FOR` `COLLABORATES_WITH` | semantic | あなたが付与する意味的関係 |

`Class` ノードには Unity 由来の `isMonoBehaviour` / `isScriptableObject` フラグが付くことがある。
意味エッジには `confidence` `rationale` `author` と時間性(`createdAt` `invalidAt`)が付く。

## 使い方（定型コマンド）

```bash
ukg() { dotnet run --project src/Ukg.Cli -- "$@"; }

ukg search "ダメージを処理するクラス"        # 意味検索（名前を知らなくても引ける）★まずこれ
ukg find PlayerController                  # 名前/キーでノード検索（完全一致）
ukg neighbors PlayerController             # 隣接エッジ（in/out 両方向、rel/source/confidence付き）
ukg deps Assets/Prefabs/Player.prefab      # AssetのDEPENDS_ON / SCRIPT_OF
ukg impact Character --depth 4             # Characterを変更したときの上流影響（推移的）
ukg query "MATCH (n) RETURN labels(n)[0], count(n)"   # 任意Cypher（読み取り専用）
```

## いつ何を使うか

- 「○○する/○○を担うクラスはどれ？」（名前を知らない）→ `search "<自然文>"` ★
- 「このクラスは何を継承/実装？何を参照/呼び出す？」→ `neighbors <Class>`
- 「このメソッドは何を呼ぶ／誰に呼ばれる？」→ `query` で `(:Method)-[:CALLS]->(:Method)` を辿る
- 「このコンポーネントは何を GetComponent している？」→ `neighbors` の `USES_COMPONENT`
- 「このprefab/sceneは何に依存？」→ `deps <assetPath>`
- 「Xを変更すると何が壊れる？影響範囲は？」→ `impact <name>`（CALLS/USES_COMPONENT/継承/参照を逆にたどる）
- 「このスクリプトを使っているprefabは？」→ `query` で
  `(:Asset)-[:DEPENDS_ON]->(:Asset)-[:SCRIPT_OF]->(:Class {name:'X'})` を辿る
- 複雑な探索は `query` で生Cypher（既定は読み取り専用。書き込みは `--write` 明示時のみ）

## 意味エッジ・概念の付与（あなたの役割）

コードを読んで理解した「意味的な繋がり」をグラフに残す。静的解析では取れない設計意図を記録する。

```bash
# 既存ノード間の意味的関係
ukg sem add --from PlayerController --to Inventory \
  --rel COLLABORATES_WITH --confidence 0.85 \
  --why "PlayerController pushes picked-up items into Inventory"

# 静的解析に無い抽象概念を立て、構成要素を PART_OF で束ねる
ukg concept add --name "戦闘システム" --summary "ダメージ計算と被弾処理を担う一連の型"
ukg sem add --from PlayerController --to "戦闘システム" --rel PART_OF --confidence 0.8

ukg sem ls                                 # 有効な意味エッジ一覧（confidence降順）
ukg sem ls --all                           # 無効化(supersede)済みも含む
```

リレーションの使い分け:
- `RESPONSIBLE_FOR` — AがBという責務/機能を担う
- `COLLABORATES_WITH` — AとBが協調して1つの機能を実現
- `PART_OF` — AがBという大きな仕組み/概念の一部
- `RELATES_TO` — 上記に当てはまらない一般的な関連

ルール:
- `--rel` は上記4種のみ（静的エッジ名は使わない）。
- 推測には必ず `--confidence`(0〜1) と `--why`(根拠) を付ける。
- 既存の意味エッジを新しい理解で**置き換える**ときは `--supersede`（旧エッジは論理無効化され履歴に残る）。
- **意味エッジと静的エッジを混同しない**。継承/参照/呼び出し/GetComponentは抽出器が自動で張るので
  `sem add` しない。意味エッジは「コードからは自明でない設計上の繋がり」に限定する。
- 再インデックスしても意味エッジ・概念は保持される。安心して育ててよい。

## コミュニティ（自動生成の概念層）

`index` 時に構造グラフからコミュニティ(凝集した型クラスタ)を自動検出し、`Concept` ノードとして
`community:` キーで作る。`ukg community` で単体再計算も可能。これらは機械生成の粗い束ねなので、
あなたが `concept add` でより意味のある概念に整理し直してよい。
