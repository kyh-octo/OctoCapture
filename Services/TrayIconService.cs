using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace OctoCapture.Services
{
    /// <summary>
    /// 트레이 아이콘 + 컨텍스트 메뉴. 아이콘은 앱 리소스(OctoCapture.ico)에서 로드한다.
    /// Dispose 시 아이콘 핸들까지 해제하여 누수를 방지한다.
    /// </summary>
    public sealed class TrayIconService : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly WinForms.NotifyIcon _icon;
        private readonly Drawing.Icon _trayIcon;
        private readonly IntPtr _hIcon;
        private readonly CaptureController _controller;
        private readonly RecordingCoordinator _recorder;
        private readonly MainWindow _main;
        private readonly WinForms.ToolStripMenuItem _startupItem;
        private readonly WinForms.ToolStripMenuItem _recordItem;
        private bool _disposed;

        public TrayIconService(CaptureController controller, RecordingCoordinator recorder, MainWindow main)
        {
            _controller = controller;
            _recorder = recorder;
            _main = main;

            var (icon, hIcon) = LoadIcon();
            _trayIcon = icon;
            _hIcon = hIcon;

            var menu = new WinForms.ContextMenuStrip();
            var openItem = new WinForms.ToolStripMenuItem("열기(&O)");
            openItem.Font = new Drawing.Font(openItem.Font, Drawing.FontStyle.Bold);
            openItem.Click += (_, _) => _main.ShowFromTray();
            menu.Items.Add(openItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            menu.Items.Add("직접 캡쳐", null, (_, _) => _controller.CaptureRegion());
            menu.Items.Add("창 캡쳐", null, (_, _) => _controller.CaptureWindow());
            menu.Items.Add("단위별 캡쳐", null, (_, _) => _controller.CaptureUnit());
            menu.Items.Add("화면 캡쳐", null, (_, _) => _controller.CaptureMonitor());
            menu.Items.Add("전체 캡쳐", null, (_, _) => _controller.CaptureFull());
            menu.Items.Add("스크롤 캡쳐", null, (_, _) => _controller.CaptureScroll());
            menu.Items.Add(new WinForms.ToolStripSeparator());

            _recordItem = new WinForms.ToolStripMenuItem("화면 녹화 시작/종료");
            _recordItem.Click += (_, _) => _recorder.Toggle();
            menu.Items.Add(_recordItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            menu.Items.Add("설정…", null, (_, _) => OpenSettings());
            _startupItem = new WinForms.ToolStripMenuItem("시작프로그램 등록");
            _startupItem.Click += (_, _) => ToggleStartup();
            menu.Items.Add(_startupItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            menu.Items.Add("종료(&X)", null, (_, _) => System.Windows.Application.Current.Shutdown());

            menu.Opening += (_, _) =>
            {
                _startupItem.Checked = _controller.Settings.RunAtStartup;
                _recordItem.Text = _recorder.IsRecording ? "■ 녹화 종료" : "⏺ 화면 녹화";
            };

            _icon = new WinForms.NotifyIcon
            {
                Icon = icon,
                Text = "OctoCapture - 화면 캡쳐",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _icon.DoubleClick += (_, _) => _main.ShowFromTray();
        }

        private void OpenSettings()
        {
            _main.ShowFromTray();
            var win = new Windows.SettingsWindow(_controller.Settings) { Owner = _main };
            if (win.ShowDialog() == true)
                ((App)System.Windows.Application.Current).ApplySettings();
        }

        private void ToggleStartup()
        {
            _controller.Settings.RunAtStartup = !_controller.Settings.RunAtStartup;
            StartupManager.SetStartup(_controller.Settings.RunAtStartup);
            _controller.Settings.Save();
        }

        public void ShowBalloon(string title, string text)
        {
            if (_disposed) return;
            _icon.ShowBalloonTip(1500, title, text, WinForms.ToolTipIcon.Info);
        }

        /// <summary>
        /// 앱 리소스(OctoCapture.ico)에서 트레이 아이콘을 로드한다.
        /// 실패 시 파란 원 + 흰 렌즈 모양을 런타임에 그려 대체한다.
        /// </summary>
        private static (Drawing.Icon, IntPtr) LoadIcon()
        {
            try
            {
                var uri = new Uri("pack://application:,,,/OctoCapture.ico");
                var res = System.Windows.Application.GetResourceStream(uri);
                if (res != null)
                {
                    using var stream = res.Stream;
                    // 트레이 표시에 적합한 크기(작은 아이콘)를 선택
                    int side = WinForms.SystemInformation.SmallIconSize.Width;
                    var loaded = new Drawing.Icon(stream, new Drawing.Size(side, side));
                    return (loaded, IntPtr.Zero); // 스트림 기반 아이콘은 Dispose()로만 해제
                }
            }
            catch { /* 리소스 로드 실패 시 아래 폴백 사용 */ }

            using var bmp = new Drawing.Bitmap(32, 32);
            using (var g = Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var body = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x2F, 0x9B, 0xFF));
                g.FillEllipse(body, 1, 1, 30, 30);
                using var lensOuter = new Drawing.SolidBrush(Drawing.Color.White);
                g.FillEllipse(lensOuter, 8, 8, 16, 16);
                using var lensInner = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x1E, 0x1E, 0x22));
                g.FillEllipse(lensInner, 12, 12, 8, 8);
            }
            IntPtr hIcon = bmp.GetHicon();
            return (Drawing.Icon.FromHandle(hIcon), hIcon);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _icon.Visible = false;
            _icon.Dispose();
            _trayIcon.Dispose();
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }
    }
}
