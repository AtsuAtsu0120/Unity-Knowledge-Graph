using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Ukg.Exporter
{
    /// <summary>
    /// AssetDatabase の正確な依存と script GUID→型を JSON マニフェストへ書き出す Editor ツール（ADR-008）。
    /// ヘッドレスの <c>ukg index --unity-manifest &lt;path&gt;</c> がこれを優先採用する。
    /// </summary>
    public static class UkgExporter
    {
        public const string DefaultOutput = "ukg-unity-manifest.json";

        [MenuItem("Tools/UKG/Export Dependency Manifest")]
        public static void ExportFromMenu()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), DefaultOutput);
            var count = Export(path);
            Debug.Log($"[UKG] {count} アセットを書き出しました: {path}");
        }

        /// <summary>
        /// バッチ実行用エントリ。
        /// Unity -batchmode -quit -projectPath &lt;proj&gt; -executeMethod Ukg.Exporter.UkgExporter.ExportBatch -ukgOut &lt;path&gt;
        /// </summary>
        public static void ExportBatch()
        {
            string outPath = DefaultOutput;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-ukgOut") outPath = args[i + 1];
            if (!Path.IsPathRooted(outPath))
                outPath = Path.Combine(Directory.GetCurrentDirectory(), outPath);

            var count = Export(outPath);
            Debug.Log($"[UKG] batch export: {count} assets -> {outPath}");
        }

        /// <summary>マニフェストを生成して書き出す。戻り値は対象アセット数。</summary>
        public static int Export(string outputPath)
        {
            var assets = new List<AssetEntry>();

            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                // プロジェクト内の実ファイルのみ（Packages や フォルダは除外）
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                if (!File.Exists(path)) continue;

                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;

                // 直接依存（Unity が解決した真値）→ guid 化、自己参照と外部を除外
                var deps = AssetDatabase.GetDependencies(path, false)
                    .Where(d => d != path && d.StartsWith("Assets/", StringComparison.Ordinal))
                    .Select(AssetDatabase.AssetPathToGUID)
                    .Where(g => !string.IsNullOrEmpty(g) && g != guid)
                    .Distinct()
                    .ToArray();

                // .cs は MonoScript から厳密に型名を引く（ファイル名一致ではない）
                string scriptType = "";
                if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var mono = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                    var type = mono != null ? mono.GetClass() : null;
                    if (type != null)
                        scriptType = (type.FullName ?? type.Name).Replace('+', '.'); // 入れ子型 + を . に正規化
                }

                assets.Add(new AssetEntry
                {
                    guid = guid,
                    path = path,
                    scriptType = scriptType,
                    dependencies = deps,
                });
            }

            var manifest = new Manifest
            {
                schema = 1,
                unityVersion = Application.unityVersion,
                generatedAt = DateTime.UtcNow.ToString("o"),
                assets = assets.OrderBy(a => a.path, StringComparer.Ordinal).ToArray(),
            };

            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(outputPath, JsonUtility.ToJson(manifest, true));
            return assets.Count;
        }

        [Serializable]
        public class Manifest
        {
            public int schema;
            public string unityVersion;
            public string generatedAt;
            public AssetEntry[] assets;
        }

        [Serializable]
        public class AssetEntry
        {
            public string guid;
            public string path;
            public string scriptType; // 空 = スクリプトでない / 型解決不可
            public string[] dependencies;
        }
    }
}
