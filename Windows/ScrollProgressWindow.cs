using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    /// <summary>
    /// 스크롤 캡쳐 진행 표시 창. "여기까지 캡쳐"로 현재까지 완료, "취소"로 중단.
    /// 대상 창 캡쳐에 찍히지 않도록 화면 우상단에 배치하고 포커스를 뺏지 않는다.
    /// </summary>
    public class ScrollProgressWindow : Window
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken CancelToken => _cts.Token;
        public bool StopRequested { get; private set; }
        public bool Cancelled { get; private set; }

        public ScrollProgressWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x26));

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "스크롤 캡쳐 중…",
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(new TextBlock
            {
                Text = "창이 자동으로 스크롤되며 캡쳐됩니다.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var stopBtn = new Button { Content = "여기까지 캡쳐", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
            stopBtn.Click += (_, _) => StopRequested = true;
            var cancelBtn = new Button { Content = "취소", Padding = new Thickness(10, 5, 10, 5) };
            cancelBtn.Click += (_, _) => { Cancelled = true; _cts.Cancel(); };
            buttons.Children.Add(stopBtn);
            buttons.Children.Add(cancelBtn);
            panel.Children.Add(buttons);

            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = panel,
            };

            // 현재 모니터 우상단에 배치
            Loaded += (_, _) =>
            {
                RECT mon = ScreenCaptureService.CurrentMonitorRect;
                var source = PresentationSource.FromVisual(this);
                double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                Left = (mon.Right - ActualWidth * scale - 20) / scale;
                Top = (mon.Top + 20) / scale;
            };
            Closed += (_, _) => _cts.Dispose();
            // 버튼이 아닌 영역을 잡고 드래그하면 창 이동
            MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        }
    }
}
