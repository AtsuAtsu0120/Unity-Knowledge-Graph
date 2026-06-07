using System.IO;
using UnityEditor;
using UnityEngine;

namespace Ukg.Exporter
{
    /// <summary>
    /// アセット import 時に Unity が自動で呼ぶフック。マニフェストをデバウンスして自動再生成し、
    /// ヘッドレスの <c>ukg index</c>（既定マニフェスト自動検出）が常に真値を拾えるようにする（ADR-015）。
    /// ukg は Unity にアクセスせず、このパッケージが書いたファイルを読むだけ（疎結合）。
    /// </summary>
    public sealed class UkgAutoExport : AssetPostprocessor
    {
        // Assets の import を Unity が検知 → ここが呼ばれる。export 自体は遅延スケジュールする。
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            if (!UkgAutoExportScheduler.Enabled) return;
            if (imported.Length + deleted.Length + moved.Length == 0) return;
            UkgAutoExportScheduler.MarkDirty();
        }
    }

    /// <summary>デバウンス制御。連続 import を束ねて落ち着いてから一度だけ export する。</summary>
    [InitializeOnLoad]
    internal static class UkgAutoExportScheduler
    {
        private const string PrefKey = "Ukg.Exporter.AutoExport";
        private const string MenuPath = "Tools/UKG/Auto-export on import";
        private const double DebounceSeconds = 1.5;

        private static bool _dirty;
        private static double _due;

        static UkgAutoExportScheduler() => EditorApplication.update += Tick;

        public static bool Enabled => EditorPrefs.GetBool(PrefKey, true); // 既定 ON

        public static void MarkDirty()
        {
            _dirty = true;
            _due = EditorApplication.timeSinceStartup + DebounceSeconds;
        }

        private static void Tick()
        {
            if (!_dirty) return;
            if (EditorApplication.timeSinceStartup < _due) return;
            // コンパイル/更新/再生中は避けて次フレームへ持ち越す
            if (EditorApplication.isCompiling || EditorApplication.isUpdating
                || EditorApplication.isPlayingOrWillChangePlaymode) { _due = EditorApplication.timeSinceStartup + DebounceSeconds; return; }

            _dirty = false;
            try
            {
                // プロジェクト直下（Assets 外）に書く＝再 import ループは起きない
                var path = Path.Combine(Directory.GetCurrentDirectory(), UkgExporter.DefaultOutput);
                UkgExporter.Export(path);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UKG] 自動 export に失敗: {e.Message}");
            }
        }

        [MenuItem(MenuPath)]
        private static void Toggle() => EditorPrefs.SetBool(PrefKey, !Enabled);

        [MenuItem(MenuPath, validate = true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }
    }
}
