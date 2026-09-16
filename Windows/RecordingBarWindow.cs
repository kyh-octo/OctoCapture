using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    /// <summary>
    /// 녹화 영역 테두리 표시 창 (클릭 통과, 캡쳐 영역 바깥에 그려짐)
    /// </summary>
    public class RecordingFrameWindow : Window
    {
        private const int BorderPx = 3;
        private static readonly Brush RecordingBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
        private static readonly Brush PausedBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23));
        private readonly Border _border;

        /// <summary>일시정지 중에는 테두리를 주황색으로 표시</summary>
        public void SetPaused(bool paused) => _border.BorderBrush = paused ? PausedBrush : RecordingBrush;

        public RecordingFrameWindow(RECT region)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            _border = new Border
            {
                BorderBrush = RecordingBrush,
                BorderThickness = new Thickness(BorderPx),
            };
            Content = _border;
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.MakeClickThrough(hwnd);
                NativeMethods.ExcludeFromCapture(hwnd); // 테두리가 녹화 영상에 찍히지 않도록
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    region.Left - BorderPx, region.Top - BorderPx,
                    region.Width + BorderPx * 2, region.Height + BorderPx * 2,
                    NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
            };
        }
    }

    /// <summary>
    /// 녹화 컨트롤 바: [● 녹화 시작] [경과 시간] [소리 옵션] [✕ 취소]
    /// 녹화 영역 아래(또는 위)에 표시된다.
    /// </summary>
    public class RecordingBarWindow : Window
    {
        public event Action? StartRequested;
        public event Action? StopRequested;
        public event Action? PauseToggleRequested;
        public event Action? Cancelled;
        public event Action? RegionChangeRequested;

        public bool SystemAudio => _sysAudio.IsChecked == true;
        public bool Microphone => _mic.IsChecked == true;

        private readonly Button _mainBtn;
        private readonly Button _pauseBtn;
        private readonly Button _regionBtn;
        private readonly TextBlock _elapsed;
        private bool _paused;
        private TimeSpan _lastElapsed;
        private readonly CheckBox _sysAudio;
        private readonly CheckBox _mic;
        private readonly Button _cancelBtn;
        private readonly RECT _region;
        private bool _recording;

        public RecordingBarWindow(RECT region, bool systemAudioDefault, bool micDefault)
        {
            _region = region;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 7, 10, 7) };

            // 드래그 손잡이 (버튼이 아닌 곳을 잡아도 이동 가능)
            panel.Children.Add(new TextBlock
            {
                Text = "⠿",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x8E)),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = System.Windows.Input.Cursors.SizeAll,
                ToolTip = "드래그하여 이동",
            });

            _mainBtn = new Button
            {
                Content = "●  녹화 시작",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0xC9, 0x30, 0x30)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 14, 6),
                FontWeight = FontWeights.Bold,
            };
            _mainBtn.Click += (_, _) =>
            {
                if (_recording) StopRequested?.Invoke();
                else StartRequested?.Invoke();
            };
            panel.Children.Add(_mainBtn);

            _regionBtn = new Button
            {
                Content = "⬚ 영역 변경",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = "녹화 영역을 다시 지정합니다 (직접 지정/창/단위/전체 화면)",
            };
            _regionBtn.Click += (_, _) => RegionChangeRequested?.Invoke();
            panel.Children.Add(_regionBtn);

            // 녹화 중에만 보이는 일시정지/재개 버튼
            _pauseBtn = new Button
            {
                Content = "⏸ 일시정지",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(8, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "녹화를 잠시 멈추거나 이어서 녹화합니다",
            };
            _pauseBtn.Click += (_, _) => PauseToggleRequested?.Invoke();
            panel.Children.Add(_pauseBtn);

            _elapsed = new TextBlock
            {
                Text = "00:00",
                Foreground = Brushes.White,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 14, 0),
            };
            panel.Children.Add(_elapsed);

            _sysAudio = new CheckBox
            {
                Content = "시스템 소리",
                Foreground = Brushes.White,
                IsChecked = systemAudioDefault,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            panel.Children.Add(_sysAudio);

            _mic = new CheckBox
            {
                Content = "마이크",
                Foreground = Brushes.White,
                IsChecked = micDefault,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
            };
            panel.Children.Add(_mic);

            _cancelBtn = new Button
            {
                Content = "✕",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x40)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
            };
            _cancelBtn.Click += (_, _) => Cancelled?.Invoke();
            panel.Children.Add(_cancelBtn);

            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = panel,
            };

            // 전체 화면/최대화 창 녹화처럼 바가 영역 안에 놓여도 영상에 찍히지 않도록 캡쳐에서 제외
            SourceInitialized += (_, _) => NativeMethods.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
            Loaded += (_, _) => PositionNearRegion();
            // 버튼이 아닌 영역을 잡고 드래그하면 창 이동
            MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { /* 버튼 클릭과 경합 시 무시 */ } };
        }

        private void PositionNearRegion()
        {
            var source = PresentationSource.FromVisual(this);
            double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int barW = (int)(ActualWidth * scale);
            int barH = (int)(ActualHeight * scale);

            // 녹화 영역이 속한 모니터 안에 배치 (모니터 경계에 걸치지 않도록)
            RECT vs = ScreenCaptureService.MonitorRectFromPoint(
                _region.Left + _region.Width / 2, _region.Top + _region.Height / 2);
            int x = Math.Clamp(_region.Left + (_region.Width - barW) / 2, vs.Left, Math.Max(vs.Left, vs.Right - barW));
            int y = _region.Bottom + 12;
            if (y + barH > vs.Bottom) y = _region.Top - barH - 12;   // 아래 공간이 없으면 위
            if (y < vs.Top) y = _region.Bottom - barH - 12;           // 위도 없으면 영역 안쪽 하단

            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, barW, barH,
                NativeMethods.SWP_SHOWWINDOW);
        }

        public void EnterRecordingState()
        {
            _recording = true;
            _mainBtn.Content = "■  녹화 종료";
            _regionBtn.Visibility = Visibility.Collapsed; // 녹화 중에는 영역 변경 불가
            _pauseBtn.Visibility = Visibility.Visible;
            _sysAudio.IsEnabled = false;
            _mic.IsEnabled = false;
            _cancelBtn.Visibility = Visibility.Collapsed;
        }

        /// <summary>일시정지 상태 표시 갱신</summary>
        public void SetPaused(bool paused)
        {
            _paused = paused;
            _pauseBtn.Content = paused ? "▶ 재개" : "⏸ 일시정지";
            UpdateElapsed(_lastElapsed);
        }

        public void SetBusy(string text)
        {
            _mainBtn.IsEnabled = false;
            _mainBtn.Content = text;
            _pauseBtn.IsEnabled = false;
        }

        public void UpdateElapsed(TimeSpan t)
        {
            _lastElapsed = t;
            string time = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
            _elapsed.Text = _paused ? $"⏸ {time}" : time;
        }
    }
}
