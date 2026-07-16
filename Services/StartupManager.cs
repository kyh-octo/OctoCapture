using Microsoft.Win32;

namespace OctoCapture.Services
{
    /// <summary>시작프로그램 등록/해제 (HKCU Run 키)</summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "OctoCapture";

        public static bool IsRegistered()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) != null;
        }

        public static bool SetStartup(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return false;
                if (enable)
                {
                    string exe = Environment.ProcessPath ?? "";
                    if (string.IsNullOrEmpty(exe)) return false;
                    key.SetValue(AppName, $"\"{exe}\" --minimized");
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
                return true;
            }
            catch { return false; }
        }
    }
}
