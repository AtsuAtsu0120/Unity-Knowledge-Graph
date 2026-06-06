# UKG Exporter (com.ukg.exporter)

Unity Knowledge Graph (`ukg`) 用の **Editor 専用** エクスポータ。Unity の `AssetDatabase` から
**正確なアセット依存**と **script GUID→型**を JSON マニフェストへ書き出す。ヘッドレスの
`ukg index --unity-manifest <path>` がこれを優先取り込みする（無ければ正規表現にフォールバック）。

ハイブリッド運用の意図は `../../docs/DECISIONS.md` ADR-008 を参照。

## なぜ必要か

ヘッドレスの `ukg` は Unity 非依存で動くぶん、アセット依存を `.meta`/YAML の guid 正規表現で
**近似**している（アセット粒度・無差別）。このパッケージを使うと、fileID・prefab variant・
nested prefab・sprite atlas 等を Unity が解決した**真値**の依存と、`MonoScript.GetClass()` による
**厳密な script→型対応**が得られる。

## インストール

Unity の Package Manager → **Add package from git URL**:

```
https://github.com/<owner>/unity-knowledge-graph.git?path=unity/com.ukg.exporter
```

またはローカルパスで `Add package from disk` → `unity/com.ukg.exporter/package.json`。

## 使い方

### エディタから
メニュー **Tools ▸ UKG ▸ Export Dependency Manifest**。
プロジェクト直下に `ukg-unity-manifest.json` を書き出す。

### バッチ（CI / ヘッドレス）
```bash
Unity -batchmode -quit \
  -projectPath /path/to/UnityProject \
  -executeMethod Ukg.Exporter.UkgExporter.ExportBatch \
  -ukgOut /path/to/ukg-unity-manifest.json
```

### 取り込み
```bash
ukg index /path/to/UnityProject --unity-manifest /path/to/ukg-unity-manifest.json
```
出力の `assetSource` が `unity-manifest` なら真値、`regex-fallback` なら近似。

## 出力フォーマット

```json
{
  "schema": 1,
  "unityVersion": "2022.3.0f1",
  "generatedAt": "2026-01-01T00:00:00.000Z",
  "assets": [
    { "guid": "…", "path": "Assets/…", "scriptType": "Game.PlayerController", "dependencies": ["…"] }
  ]
}
```
- `scriptType`: `.cs` のとき `MonoScript.GetClass().FullName`（入れ子型の `+` は `.` に正規化）。非スクリプトは空。
- `dependencies`: `AssetDatabase.GetDependencies(path, recursive:false)` を guid 化し、自己参照と Assets 外を除外。
