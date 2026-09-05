using System.Windows;
using OctoCapture.Models;
using OctoCapture.Services;

namespace OctoCapture
{
    public partial class App : Application
    {
        private const string MutexName = "OctoCapture_SingleInstance";
        private const string ShowEventName = "OctoCapture_ShowMainWindow";

        private Mutex? _mutex;
        private bool _ownsMutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showWait;

        private AppSettings _settings = null!;
        private CaptureController _controller = null!;
        private RecordingCoordinator _recorder = null!;
        private HotkeyManager _hotkeys = null!;
        private TrayIconService _tray = null!;
        private MainWindow _mainWindow = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            // ---- 전역 예외 처리: 예기치 못한 오류로 앱이 통째로 죽지 않도록 ----
            DispatcherUnhandledException += (_, ex) =>
            {
                ErrorLog.Write("DispatcherUnhandledException", ex.Exception);
                MessageBox.Show($"오류가 발생했습니다.\n{ex.Exception.Message}\n\n자세한 내용: {ErrorLog.LogPath}",
                    "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
                ex.Handled = true; // 앱 종료 방지
            };
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
                ErrorLog.Write("AppDomain.UnhandledException", ex.ExceptionObject);
            TaskScheduler.UnobservedTaskException += (_, ex) =>
            {
                ErrorLog.Write("UnobservedTaskException", ex.Exception);
                ex.SetObserved();
            };

            // ---- 단일 인스턴스 ----
            _mutex = new Mutex(true, MutexName, out bool isNew);
            _ownsMutex = isNew;
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            if (!isNew)
            {
                // 이미 실행 중이면 기존 인스턴스의 창을 띄우고 종료
                _showEvent.Set();
                Shutdown();
                return;
            }

            base.OnStartup(e);

            RecordingCoordinator.CleanTempFiles(); // 이전 비정상 종료 잔여물 정리

            _settings = AppSettings.Load();
            _controller = new CaptureController(_settings);
            _recorder = new RecordingCoordinator(_controller);
            _hotkeys = new HotkeyManager();
            _mainWindow = new MainWindow(_controller, _recorder);
            MainWindow = _mainWindow;
            _tray = new TrayIconService(_controller, _recorder, _mainWindow);

            _controller.ItemCaptured += (item, copied) =>
            {
                if (copied)
                    _tray.ShowBalloon("캡쳐 완료", $"{item.Title} - 클립보드에 복사되었습니다.");
            };

            ApplySettings();

            // 다른 인스턴스가 실행되면 창 표시 신호 수신
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(() => _mainWindow.ShowFromTray()),
                null, -1, false);

            bool startMinimized = e.Args.Contains("--minimized");
            if (!startMinimized)
                _mainWindow.Show();
        }

        /// <summary>설정 변경 후 단축키/시작프로그램 재적용</summary>
        public void ApplySettings()
        {
            _settings.Save();
            StartupManager.SetStartup(_settings.RunAtStartup);

            _hotkeys.UnregisterAll();
            var failed = new List<string>();
            void Reg(string keyText, string name, Action action)
            {
                if (string.IsNullOrWhiteSpace(keyText)) return;
                if (_hotkeys.Register(keyText, action) < 0)
                    failed.Add($"{name} ({keyText})");
            }

            Reg(_settings.HotkeyRegionCapture, "직접 캡쳐", _controller.CaptureRegion);
            Reg(_settings.HotkeyWindowCapture, "창 캡쳐", _controller.CaptureWindow);
            Reg(_settings.HotkeyUnitCapture, "단위별 캡쳐", _controller.CaptureUnit);
            Reg(_settings.HotkeyMonitorCapture, "화면 캡쳐", _controller.CaptureMonitor);
            Reg(_settings.HotkeyFullCapture, "전체 캡쳐", _controller.CaptureFull);
            Reg(_settings.HotkeyScrollCapture, "스크롤 캡쳐", _controller.CaptureScroll);
            Reg(_settings.HotkeyRecord, "녹화", _recorder.Toggle);
            Reg(_settings.HotkeyShowMain, "메인 창 열기", () => _mainWindow.ShowFromTray());

            if (failed.Count > 0)
                _tray.ShowBalloon("단축키 등록 실패",
                    $"다른 프로그램이 사용 중입니다: {string.Join(", ", failed)}");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _showWait?.Unregister(null);
            _hotkeys?.Dispose();
            _tray?.Dispose();
            RecordingCoordinator.CleanTempFiles();
            _showEvent?.Dispose();
            if (_ownsMutex) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
