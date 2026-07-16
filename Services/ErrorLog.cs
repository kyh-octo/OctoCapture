using System.IO;
using OctoCapture.Models;

namespace OctoCapture.Services
{
    /// <summary>크래시/오류를 %AppData%\OctoCapture\error.log 에 기록</summary>
    public static class ErrorLog
    {
        public static string LogPath => Path.Combine(AppSettings.SettingsDir, "error.log");

        public static void Write(string context, object? error)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.SettingsDir);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}\n{error}\n\n");
            }
            catch { /* 로깅 실패는 무시 */ }
        }
    }
}
