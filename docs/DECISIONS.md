# 設計判断ログ (ADR)

ロードマップ(`docs/ROADMAP.md`)実装中の設計判断を記録する。1判断1セクション。
形式: 文脈 / 判断 / 理由 / 代替案 / 影響。

---

## ADR-001: 埋め込みは抽象 `IEmbedder` ＋ 既定はローカル決定的埋め込み

- **文脈**: P1のベクトル検索に埋め込みが要る。外部APIキー前提だとビルド/テスト/CIが回らず、
  再現性も落ちる。
- **判断**: `IEmbedder` を切り出し、既定実装を**フィーチャーハッシュ法によるローカル決定的埋め込み**
  (`HashingEmbedder`, 256次元, L2正規化) とする。実APIは将来差し替え可能にする。
- **理由**: APIキー無しでビルド・テスト・オフライン利用が完結。決定的＝同入力同ベクトルで
  キャッシュとゴールデンテストが成立。トークン共有による粗い意味類似は実用下限を満たす。
- **代替案**: OpenAI等を必須化 → CI不能・非決定的。ONNXローカルモデル同梱 → 依存と配布が重い。
- **影響**: 検索品質は実埋め込みに劣る。`UKG_EMBED_*` で実装差し替えの口を残す。

## ADR-002: スカラーはパラメータ化、マップ/リストはエスケープ済みインライン化

- **文脈**: 既存はCypher文字列インライン化。インジェクション面と巨大文字列の脆さ。
- **当初判断**: 全値を `GRAPH.QUERY g "CYPHER k=v ..." ` のパラメータ前置で渡す方針だった。
- **実測で判明した制約**: **FalkorDB のクエリパラメータはマップ値（`{k: v}`）を受け付けない**
  （`Failed to parse query parameter` エラー）。UNWIND バッチや props マップはパラメータ化不能。
- **改訂判断**:
  - スカラー（文字列・数値）と検索キーは `$q` 等のパラメータで渡す（`find`/`neighbors`/`deps` 等）。
  - マップ/リストを含むバッチ（ノード/エッジ upsert・埋め込み・props）は `Cypher` で
    **エスケープ済みリテラルとしてボディにインライン化**する。値は `Cypher.Str/Value` で
    完全エスケープ（ユニコード/ヌル/制御文字含む）するためインジェクションは成立しない。
  - ラベル/リレーションタイプ/プロパティ名は `Cypher.Ident`＋許可リストで検証してから埋め込む。
- **理由**: パラメータ化の本来の狙い（構造インジェクション排除・巨大生文字列の脆さ低減）は
  値の完全エスケープと識別子検証で達成できる。マップ非対応というDB制約に合わせた現実解。
- **影響**: `GraphClient.Query` はパラメータ任意。`Cypher.Str/Map/Value/List/Vecf32` が
  リテラル生成を一手に担い、エスケープ漏れはここだけ監査すればよい。

## ADR-003: 静的ノードのライフサイクル＝差分削除、semantic接続ノードは保護

- **文脈**: 旧実装は static ノードを削除せず蓄積。リネームで孤児が残る。
- **判断**: 再インデックス時、今回の抽出に現れない `source=static` ノードを削除する。ただし
  **semantic エッジが接続しているノードは削除せず `stale=true` を立てて温存**し、警告対象にする。
- **理由**: ゴミ蓄積を止めつつ、LLMが付けた意味的知識(の係り先)を破壊しない。完全削除は
  semantic 資産を失う。
- **影響**: `ApplyStaticLayer` に差分削除フェーズを追加。`stale` ノードは検索で減点/除外可能に。

## ADR-004: Roslyn はプロジェクト内 `Compilation` で解決、Unity API は構文パターンで検出

- **文脈**: 正確なシンボル解決が要るが、Unityエンジンアセンブリ(UnityEngine.dll等)はリポジトリに無い。
- **判断**: 全 `.cs` の構文木＋利用可能なBCL参照で1つの `Compilation` を組み、**プロジェクト内型は
  SemanticModelで厳密解決**。`GetComponent<T>` 等のUnity固有結合は、参照が無くても落ちないよう
  **構文パターン(呼び出し式・属性)で検出**するハイブリッドとする。
- **理由**: Unityアセンブリ非依存で動かす必要がある。プロジェクト内解決はSemanticModelで正確化でき、
  Unity結合は構文で十分拾える(型引数の型名で接続)。
- **代替案**: Unityインストールからdll解決 → 環境依存で壊れやすい。全部構文 → 同名衝突に弱い。
- **影響**: MonoBehaviour/ScriptableObject判定もベース型名＋(可能なら)シンボルの両建て。

## ADR-005: コミュニティ検出はラベル伝播(LPA)を内製、要約はLLM任意

- **文脈**: P3のクラスタリング。外部グラフ計算依存やGDSは重い。
- **判断**: **同期ラベル伝播法(LPA)** を内製してモジュール分割し、コミュニティごとに `Concept`
  ノードを生成・`PART_OF` で束ねる。要約はオフラインでは構成要素から機械生成、LLM接続時は
  差し替え可能にする。
- **理由**: LPAは実装が軽く決定論化(固定走査順)でテスト可能。FalkorDBバージョン差のある
  アルゴproc依存を避ける。
- **影響**: 大規模での厳密最適性はLeidenに劣るが、ナビゲーション用途には十分。

## ADR-006: 時間性は単純バイテンポラル、`now` は呼び出し側が注入

- **文脈**: P4のtemporal化。スクリプト/テストの決定性のため時刻乱用は避けたい。
- **判断**: semantic エッジに `createdAt`/`validFrom`/`invalidAt`/`commitSha` を持たせ、矛盾する
  新事実で旧事実に `invalidAt` を立てる論理無効化とする。時刻は CLI 境界で1度だけ取得し下層へ注入。
- **理由**: 履歴追跡と矛盾解消の最小形。時刻注入で下層を決定的に保ちテスト可能。
- **影響**: `AddSemanticEdge` のシグネチャに時刻/コミットを追加。既定検索は有効(invalidAt無し)のみ。

## ADR-007: P5 のスケールは「並列パース＋増分埋め込み」を実装、フル増分グラフ差分は次段送り

- **文脈**: ロードマップP5は差分インデックス・並列走査・性能計測。フルの「変更ファイルのみ
  グラフを部分更新」は、エッジの両端追跡・削除の局所化など波及が大きい。
- **判断**: 今段で実装するのは
  1) **並列パース**（`Parallel.ForEach` で全 `.cs` を並行構文解析）、
  2) **増分埋め込み**（`embedText` の内容ハッシュ差分で未変更ノードの再ベクトル化を回避）、
  3) 各ノードへ `contentHash` を保存（将来のファイル単位差分の土台）。
  静的レイヤーの**再構築自体は引き続きフル**（ライフサイクル差分削除つき）とする。
- **理由**: 並列パースと埋め込みキャッシュで再インデックスの主要コスト（パース・埋め込み生成）は
  実効的に削減できる。グラフ書き込みは UNWIND バッチで安価。フル部分更新は正確性リスクが高く、
  測定（`contentHash`）を先に入れて必要になってから踏み込むのが妥当。
- **影響**: 大規模での真の差分更新は未達。`contentHash` が入っているため、次段でファイル単位の
  抽出スキップを安全に追加できる。ROADMAP に「部分達成」と明記。

## ADR-008: 運用はハイブリッド — ヘッドレスエンジン ＋ Unity Editor エクスポータ(UPM)

- **文脈**: 現状は Unity API 非依存（ADR-004）でアセット依存を guid 正規表現 grep で近似している。
  精度（fileID/variant/nested prefab/atlas 解決、script→型の厳密対応）を上げたいが、全体を
  Unity Editor 内（フルUPM）へ移すと Editor 常駐前提・CIにUnity必須・Roslyn/Redis 同梱の重さを負う。
  主消費者は Claude であり、ヘッドレスで叩ける軽さは最大の利点。
- **判断**: **ハイブリッド**を採用する。
  1) グラフ構築・LLM導線・C#解析は**ヘッドレスの `ukg` に据え置き**（Unity非依存を維持）。
  2) **薄い Unity Editor エクスポータ**（UPMパッケージ `com.ukg.exporter`）を追加し、
     `AssetDatabase.GetDependencies` による**正確なアセット依存**と、`MonoScript.GetClass()` による
     **script guid→型の厳密マップ**を `ukg-unity-manifest.json` として書き出す。
  3) `ukg index --unity-manifest <path>` がマニフェストを**優先採用**し、無ければ従来の正規表現に
     **フォールバック**する。
- **理由**: 精度が本当に要る部分（アセット層・script橋渡し）だけ Unity に真値を出させ、それ以外は
  ヘッドレスのまま保つのが費用対効果最大。UPM(git URL)でUnityチームへ配布もできる。エンジン本体
  （net9＋Roslyn＋Redis）は UPM に入れず分離する（UPMはEditor C#のみ）。
- **代替案**: フルUPM（Editor内完結）→ 重く運用が硬い。ヘッドレス強化のみ → 精度が guid grep 止まり。
- **影響**: 新規 `unity/com.ukg.exporter/`（Editor専用）。`Ukg.Extractors` に `UnityManifest` 取り込みと
  マニフェスト優先のアセット層を追加。マニフェスト無しの既存挙動は不変（後方互換）。
  運用フロー（バッチ生成→index）は ROADMAP/README に記載。

## ADR-009: ライブ更新モデル — 構造はイベント駆動で自動、意味はLLMが手入れ

- **文脈**: 旧来は手動 `index` のみで鮮度が運用依存。最前線（Graphiti の増分・temporal、
  Sourcegraph/Glean のコミット駆動増分、agentic memory のリフレクション）に倣い、鮮度を
  仕組みで担保したい。ただし「LLMが全部更新」はアンチパターン（構造は決定的・無料な解析で出せる）。
- **判断**: 4点を実装し、**構造＝自動/決定的、意味＝LLMが継続キュレーション**で役割分担する。
  1. **増分no-op＋索引状態**: 追跡ファイルの内容ハッシュ集合を `UkgMeta` ノードに保存。
     `index` は前回と差分が無ければ抽出もDB書き込みもせず即スキップ（`upToDate`）。`--force` で強制。
  2. **stale伝播**: 変更/追加された `.cs` に属する型に係る**LLM由来の意味エッジを `needsReview` 化**。
     消えた型は既存のライフサイクルで `stale`。両者を `sem review`/`reflect` で surface。
  3. **読み取り時ガード**: `ukg status` が鮮度（`fresh`/`reviewPending`）を返す。SKILL は
     **構造の質問前に status→必要なら index** を Claude に課す（JIT 自己修復）。
  4. **リフレクション**: `ukg reflect` が要再確認/低confidence/孤児/重複概念/未整理コミュニティを集約。
     `sem confirm` で再確認、`sem add --supersede` で更新。LLM が意味層を定期的に手入れする。
  - **トリガ**: `ukg watch`（FileSystemWatcher＋デバウンス）、git hooks（`hooks/`）、CI（`.github/workflows/ukg.yml`）。
- **理由**: 構造は Roslyn が即時・厳密・無料に出せるのでイベント駆動で十分。LLM の価値は
  「コードから自明でない意味」の付与と、構造変化に追従した**意味の再確認/統合**。鮮度は
  バイテンポラル＋status＋stale伝播で「嘘をつかない」状態を保つ。
- **代替案**: フル部分グラフ更新（per-file patch）→ エッジが跨り正確性リスク大、no-op＋差分embedで
  実コストはほぼ取れるため見送り。LLMに構造も更新させる→遅い/非決定的/幻覚で却下。
- **影響**: `IndexState`、`UkgMeta`、`needsReview`/`reviewReason`/`confirmedAt` を追加。
  `status`/`watch`/`reflect`/`sem review`/`sem confirm`/`index --force` を追加。
  読み取りクエリは未作成グラフを「空」として扱うよう `GraphClient` を寛容化。

## ADR-010: 埋め込みは汎用プラグイン構造（OpenAI互換HTTP＋オフライン既定）＋次元安全な再索引

- **文脈**: P1 の意味検索を実戦品質にするには本物の埋め込みが要るが、特定ベンダーに固定したくない。
  実プロ検証では環境変数で差し替えたい。CI/オフラインはキー無しで動く必要がある。
- **判断**: `IEmbedder` を一般化し、設定で実装を選ぶ。
  - `IEmbedder` に `Id`（索引署名）と `EmbedBatch`（バッチ）を追加。`ApplyEmbeddings` はバッチ呼び出しに。
  - 実装: `HashingEmbedder`（既定・オフライン・無料）と **`HttpEmbedder`（OpenAI互換 `/v1/embeddings`）**。
    後者1つで OpenAI/Azure/Ollama/text-embeddings-inference/LM Studio 等を URL 設定だけで賄う（＝汎用化）。
  - `Embedders.FromEnvironment()` が `UKG_EMBEDDER`/`UKG_EMBED_*` から生成。
  - **次元安全な再索引**: 埋め込み器が変わると次元・ベクトルが非互換になる。`IndexState` に `EmbedderId`
    を持たせ、変化を検知したら埋め込みとベクトル索引を破棄→新次元で作り直し→全再埋め込みする。
- **理由**: OpenAI互換APIは事実上の標準で、1実装で多数のプロバイダ（クラウド/ローカル）を覆える。
  ベンダーロックを避けつつ実プロで本物の検索を試せる。既定はオフラインのままなのでCI/ビルドは無依存。
- **代替案**: ONNXローカルモデル同梱→依存と配布が重く、汎用化より特定化。当面は HTTP 抽象で汎用化し、
  必要なら後段で `OnnxEmbedder` を同じ口に足す。
- **影響**: `Embedding.cs` に `Id`/`EmbedBatch`/`HttpEmbedder`/`Embedders`。`IndexState.EmbedderId`、
  `GraphRepository` に `ResetEmbeddings`/`DropVectorIndexes`。環境変数:
  `UKG_EMBEDDER`(hashing|http) `UKG_EMBED_URL` `UKG_EMBED_MODEL` `UKG_EMBED_API_KEY` `UKG_EMBED_DIM`。

## ADR-011: 語彙あいまい検索（candidates）＋ miss シグナルで grep フォールバックを安全化

- **文脈**: KG の存在意義は「エージェント（Claude Code/Codex）が grep せずに即答を得る」こと。
  だが従来の入口は `find`（完全一致のみ。`PlayerCtrl` で `PlayerController` は引けない）と
  `search`（ベクトル意味検索。既定 `hashing` はコールドスタート品質が中）の二択で、
  「正確な名前を知らない／オフラインで精度が物足りない」谷があり、結局 grep に戻りたくなる。
  さらに「全くヒットしなければ grep にフォールバック」を成立させるには、グラフが
  **『分かりません』を正直に返す**必要がある（低スコアを無理にヒット扱いすると誤誘導になる）。
- **判断**:
  1. **`GraphRepository.Candidates(query, k, label)`** を追加。クエリを区切り文字＋camelCase 境界で
     トークン分割し、`name/key/doc/summary/signature` を**グラフ側で**部分一致スコアリングする
     （`reduce`＋`CONTAINS`）。ファイルは一切読まない＝**トークン不要**。
     score = トークン一致率 ＋ 名前の完全一致(+1.0)/部分一致(+0.5)/前方一致(+0.25) ボーナス。
  2. **CLI `candidates`** が結果に **miss シグナル**を付す: `confidence` を3段階
     （`high` = topScore≥1.0 / `low` = 0〜1.0 / `none` = 0件）で返し、段階別に
     grep フォールバック方針（`recommendation`）を明示する。
  3. 運用ループを確定：エージェントは `candidates`（語彙・トークン0）→ `confidence:none` の時のみ
     grep → 理解したら `sem add`/`concept add` で書き戻す。`candidates`＋`search` の二経路で
     `hashing` の弱点（同義語に弱い）を語彙一致が補完し、**トークン無しで grep 不要**を成立させる。
  4. 初期化を **`ukg-bootstrap` スキル**として用意。`index` で構造を無料生成 → Claude が
     コミュニティ/中心ノードを骨格に大まか把握 → `concept add`/`sem add` で意味を種付け →
     `reflect`/`candidates` で検証。「意味0スタート」を回避する。
- **理由**: 「意味検索はグラフの責務」をコードで全うする最小の一手（実装規模 S）。abbreviation
  （`ctrl`→`controller`）は部分一致では解けないが、その場合 `confidence:low` で候補を提示しつつ
  「的外れなら grep」と正直に誘導する＝嘘をつかない。API トークンは「コールドスタートを最初から
  高品質にしたい時だけの任意ブースト」に正しく格下げできる。トークン削減は「前払い投資→償却」
  （意味の書き戻しは1回、再利用は全エージェント・全セッション）。
- **代替案**: `find` をあいまい化して一本化 → `find`（完全一致）は他経路の前提でもあり別コマンドに分離。
  全文検索インデックス導入 → 現規模ではスキャンで十分、依存を増やさない。miss 判定を `search` 側にも
  付与 → 本 ADR では語彙経路に集中（ベクトルの距離閾値較正は次段）。
- **影響**: `GraphRepository.Candidates`/`Tokenize`/`CandidateLabels`、CLI `candidates`＋`TopScore`、
  `.claude/skills/ukg-bootstrap/`。統合テスト `Candidates_*` を追加。
