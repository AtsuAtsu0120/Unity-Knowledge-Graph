---
name: unity-knowledge-graph
description: >
  Query and enrich the Unity project's knowledge graph (FalkorDB) via the `ukg` CLI.
  Use when you need to understand C# class relationships (inheritance, interfaces,
  references), Unity asset dependencies (prefabs/scenes → scripts), trace change
  impact, or record semantic relationships between systems. Trigger when the user
  asks about Unity code structure, "what uses X", "what does this prefab depend on",
  "impact of changing Y", or asks you to remember a conceptual link between components.
---

# Unity Knowledge Graph (ukg)

UnityプロジェクトのナレッジグラフをFalkorDBから参照・拡張するためのスキル。
**静的構造**（C#クラス関連・Asset依存）はC#抽出器が `source=static` で構築済み。
**意味的な繋がり**はあなた（LLM）が `source=semantic` のエッジとして追記する。

## 前提

- FalkorDBが起動していること: `docker compose up -d`
- グラフが構築済みであること: `dotnet run --project src/Ukg.Cli -- index <UnityProjectPath>`
  （コード変更後は再実行。静的レイヤーのみ作り直し、意味エッジは保持される）

CLIは全コマンドJSONを返す。`UKG_REDIS`(既定 localhost:6379) / `UKG_GRAPH`(既定 unity) で接続先を変更可。

## スキーマ

ノード: `Class` `Interface` `Struct` `Enum` `Namespace` `Asset` `Concept`
（各ノードに安定キー `key`＝クラスは完全修飾名 / Assetはguid。`name` `path` `source` を持つ）

| エッジ | source | 意味 |
|---|---|---|
| `INHERITS` | static | Class → 基底Class |
| `IMPLEMENTS` | static | Class → Interface |
| `REFERENCES` | static | 型 → フィールド/プロパティで使う型 |
| `IN_NAMESPACE` | static | 型 → Namespace |
| `DEPENDS_ON` | static | Asset → 参照先Asset（prefab→script等） |
| `SCRIPT_OF` | static | .cs Asset → Class（コードとAssetの橋渡し） |
| `RELATES_TO` `PART_OF` `RESPONSIBLE_FOR` `COLLABORATES_WITH` | semantic | あなたが付与する意味的関係 |

`Class` ノードには Unity 由来の `isMonoBehaviour` / `isScriptableObject` フラグが付くことがある。

## 使い方（定型コマンド）

```bash
ukg() { dotnet run --project src/Ukg.Cli -- "$@"; }

ukg find PlayerController                  # 名前/キーでノード検索
ukg neighbors PlayerController             # 隣接エッジ（in/out 両方向、relとsource付き）
ukg deps Assets/Prefabs/Player.prefab      # AssetのDEPENDS_ON / SCRIPT_OF
ukg impact Character                        # Characterを変更したときの上流影響（推移的）
ukg query "MATCH (n) RETURN labels(n)[0], count(n)"   # 任意Cypher
```

## いつ何を使うか

- 「このクラスは何を継承/実装？何を参照？」→ `neighbors <Class>`
- 「このprefab/sceneは何に依存？」→ `deps <assetPath>`
- 「Xを変更すると何が壊れる？影響範囲は？」→ `impact <name>`（推移的な逆依存）
- 「このスクリプトを使っているprefabは？」→ `query` で `(:Asset)-[:DEPENDS_ON]->(:Asset)-[:SCRIPT_OF]->(:Class {name:'X'})` を辿る
- 複雑な探索は `query` で生Cypherを書く

## 意味エッジの付与（あなたの役割）

コードを読んで理解した「意味的な繋がり」をグラフに残す。静的解析では取れない設計意図を記録する。

```bash
ukg sem add --from PlayerController --to Inventory \
  --rel COLLABORATES_WITH --confidence 0.85 \
  --why "PlayerController pushes picked-up items into Inventory"

ukg sem ls                                 # 既存の意味エッジ一覧
```

リレーションの使い分け:
- `RESPONSIBLE_FOR` — AがBという責務/機能を担う
- `COLLABORATES_WITH` — AとBが協調して1つの機能を実現
- `PART_OF` — AがBという大きな仕組み/システムの一部
- `RELATES_TO` — 上記に当てはまらない一般的な関連

ルール:
- `--rel` は上記4種のみ（静的エッジ名は使わない）。
- 推測には必ず `--confidence`(0〜1) と `--why`(根拠) を付ける。
- **意味エッジと静的エッジを混同しない**。静的に分かる継承/参照は `sem add` しない
  （抽出器が自動で張る）。意味エッジは「コードからは自明でない設計上の繋がり」に限定する。
- 再インデックスしても意味エッジは保持される。安心して育ててよい。
