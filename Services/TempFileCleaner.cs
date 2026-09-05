using System.IO;

namespace OctoCapture.Services
{
    /// <summary>
    /// 임시 파일 삭제 유틸.
    /// - 클립보드(파일 드롭 목록)가 참조 중인 파일은 지우지 않는다 (붙여넣기가 깨지지 않도록).
    /// - MediaElement 등이 비동기로 핸들을 놓는 경우를 위해 실패 시 지연 재시도한다.
    /// </summary>
    public static class TempFileCleaner
    {
        private static readonly int[] RetryDelaysMs = { 500, 1500, 4000, 10000 };

        /// <summary>즉시 삭제 시도. 클립보드 참조 중이면 건너뛴다. 성공/건너뜀 시 true.</summary>
        public static bool TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (ClipboardService.IsFileOnClipboard(path)) return true; // 붙여넣기 보호
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>즉시 삭제를 시도하고, 실패하면(파일 잠금 등) 백그라운드에서 몇 차례 재시도한다.</summary>
        public static void DeleteLater(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (TryDelete(path)) return;
            _ = Task.Run(async () =>
            {
                foreach (var delay in RetryDelaysMs)
                {
                    await Task.Delay(delay);
                    try
                    {
                        if (!File.Exists(path)) return;
                        if (ClipboardService.IsFileOnClipboard(path)) return;
                        File.Delete(path);
                        return;
                    }
                    catch { /* 다음 재시도 */ }
                }
                // 끝까지 실패하면 앱 시작/종료 시 CleanTempFiles가 정리
            });
        }
    }
}
