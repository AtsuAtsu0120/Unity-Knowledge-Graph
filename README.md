# Unity Knowledge Graph (ukg)

UnityプロジェクトのC#クラス関連・Asset依存といった**静的構造**と、LLMが付与する
**意味的な繋がり**を、ひとつのナレッジグラフ（FalkorDB）に統合し、Claude等のLLMから
効率よく参照できるようにするツール。

- **静的・決定的な情報** → C#（Roslyn + GUID/YAMLパーサ）が抽出（`source=static`）
- **意味的な繋がり** → LLMがエッジとして追記（`source=semantic`）
- 両者は `source` プロパティで分離。再インデックスしてもLLM由来のエッジは保持される。

インターフェースは **Skill + CLI**。常時利用するツールでMCPの常駐コンテキスト消費を避け、
Skillの漸進的開示で軽量に保つ。コアは `Ukg.Core` に分離してあり、将来MCPサーバーを
薄く被せることも可能。

## 構成

```
src/
  Ukg.Core/         グラフモデル・FalkorDBクライアント・リポジトリ（共有ライブラリ）
  Ukg.Extractors/   CSharpExtractor(Roslyn) / AssetExtractor(.meta,YAML)
  Ukg.Cli/          ukg コマンド（index/query/find/neighbors/deps/impact/sem）
skills/
  unity-knowledge-graph/SKILL.md   Claude向け手続き知識
fixtures/
  SampleUnityProject/  E2E検証用の小さなサンプル
```

## 使い方

```bash
# 1. FalkorDB 起動
docker compose up -d

# 2. ビルド
dotnet build

# 3. Unityプロジェクトをインデックス（静的レイヤー構築）
dotnet run --project src/Ukg.Cli -- index /path/to/UnityProject

# 4. クエリ
dotnet run --project src/Ukg.Cli -- find PlayerController
dotnet run --project src/Ukg.Cli -- neighbors PlayerController
dotnet run --project src/Ukg.Cli -- deps Assets/Prefabs/Player.prefab
dotnet run --project src/Ukg.Cli -- query "MATCH (n) RETURN labels(n), count(n)"

# 5. LLMが意味的エッジを追記
dotnet run --project src/Ukg.Cli -- sem add --from Player --to InventorySystem \
  --rel COLLABORATES_WITH --confidence 0.8 --why "Player picks up items into inventory"
dotnet run --project src/Ukg.Cli -- sem ls
```

## グラフスキーマ

| 種別 | ラベル / タイプ |
|---|---|
| ノード | `Class` `Interface` `Struct` `Enum` `Namespace` `Asset` `Concept` |
| 静的エッジ (`source=static`) | `INHERITS` `IMPLEMENTS` `REFERENCES` `IN_NAMESPACE` `DEPENDS_ON` `SCRIPT_OF` |
| 意味エッジ (`source=semantic`) | `RELATES_TO` `PART_OF` `RESPONSIBLE_FOR` `COLLABORATES_WITH` |

設定は環境変数で上書き可能:

- `UKG_REDIS` — FalkorDB接続文字列（既定 `localhost:6379`）
- `UKG_GRAPH` — グラフ名（既定 `unity`）
