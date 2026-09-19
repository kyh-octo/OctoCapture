using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OctoCapture.Models
{
    /// <summary>앱 전역 설정. %AppData%\OctoCapture\settings.json 에 저장된다.</summary>
    public class AppSettings
    {
        // ---- 단축키 ("Ctrl+Shift+A" 형식) ----
        public string HotkeyRegionCapture { get; set; } = "Ctrl+Shift+A";   // 직접(영역) 캡쳐
        public string HotkeyWindowCapture { get; set; } = "Ctrl+Shift+W";   // 창 캡쳐
        public string HotkeyUnitCapture { get; set; } = "Ctrl+Shift+E";     // 단위별 캡쳐
        public string HotkeyMonitorCapture { get; set; } = "Ctrl+Shift+M";  // 화면(현재 모니터) 캡쳐
        public string HotkeyFullCapture { get; set; } = "Ctrl+Shift+F";     // 전체(모든 모니터) 캡쳐
        public string HotkeyScrollCapture { get; set; } = "Ctrl+Shift+L";   // 스크롤 캡쳐
        public string HotkeyRecord { get; set; } = "Ctrl+Shift+R";          // 녹화 시작/중지
        public string HotkeyPauseRecord { get; set; } = "Ctrl+Shift+P";     // 녹화 일시정지/재개
        public string HotkeyShowMain { get; set; } = "Ctrl+Shift+O";        // 메인 창 열기

        // ---- 일반 ----
        public bool RunAtStartup { get; set; } = false;
        public bool MinimizeToTrayOnClose { get; set; } = true;
        public bool CopyToClipboardOnCapture { get; set; } = true;
        public bool CheckForUpdates { get; set; } = true;   // 시작 시 GitHub 최신 릴리스 확인 후 설치 여부 질문
        [Obsolete("메인 화면 즉석 편집으로 대체됨. 기존 settings.json 호환을 위해 유지.")]
        public bool OpenEditorAfterCapture { get; set; } = false;
        public string SaveFolder { get; set; } = "";
        public string ImageFormat { get; set; } = "png"; // png | jpg | bmp

        // ---- 녹화 ----
        public bool RecordSystemAudio { get; set; } = true;
        public bool RecordMicrophone { get; set; } = false;
        public int RecordFps { get; set; } = 30;
        public int GifFps { get; set; } = 15;
        public string VideoFormat { get; set; } = "mp4"; // mp4 | gif | webp

        [JsonIgnore]
        public static string SettingsDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OctoCapture");

        [JsonIgnore]
        public static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public string GetEffectiveSaveFolder()
        {
            if (!string.IsNullOrWhiteSpace(SaveFolder) && Directory.Exists(SaveFolder))
                return SaveFolder;
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "OctoCapture");
            Directory.CreateDirectory(def);
            return def;
        }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch { /* 손상된 설정 파일이면 기본값으로 재생성 */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch { /* 저장 실패는 치명적이지 않음 */ }
        }
    }
}
