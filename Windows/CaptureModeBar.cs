using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OctoCapture.Services;
using CaptureMode = OctoCapture.Services.CaptureMode;

namespace OctoCapture.Windows
{
    /// <summary>
    /// 캡쳐 오버레이 상단 중앙에 표시되는 작은 모드 바.
    /// 캡쳐 도중 방식을 전환하거나 취소할 수 있다 (프리즈 프레임은 유지됨).
    /// </summary>
    public class CaptureModeBar : Border
    {
        private static readonly Brush NormalBg = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        private static readonly Brush HoverBg = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52));
        private static readonly Brush ActiveBg = new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0xAF));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xFF));

        /// <summary>캡쳐용 모드 목록 (마지막 항목은 녹화로 전환)</summary>
        public static readonly (string Label, CaptureMode Mode)[] CaptureItems =
        {
            ("⬚ 직접", CaptureMode.Region),
            ("🗔 창", CaptureMode.Window),
            ("▣ 단위", CaptureMode.Unit),
            ("🖵 화면", CaptureMode.Monitor),
            ("🖥 전체", CaptureMode.Full),
            ("⇅ 스크롤", CaptureMode.Scroll),
            ("⏺ 화면 녹화", CaptureMode.Record),
        };

        /// <summary>녹화 영역 지정용 모드 목록 (마지막 항목은 캡쳐로 전환)</summary>
        public static readonly (string Label, CaptureMode Mode)[] RecordingItems =
        {
            ("⬚ 직접 지정", CaptureMode.Region),
            ("🗔 창", CaptureMode.Window),
            ("▣ 단위", CaptureMode.Unit),
            ("🖥 전체 화면", CaptureMode.Monitor),
            ("📷 화면 캡쳐", CaptureMode.Capture),
        };

        private static bool IsSwitchItem(CaptureMode m) => m is CaptureMode.Record or CaptureMode.Capture;

        private bool _dragging;
        private Point _dragOffset;

        public CaptureModeBar(CaptureMode current, Action<CaptureMode> onSelect, Action onCancel,
            (string Label, CaptureMode Mode)[]? items = null)
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 0x22, 0x22, 0x26));
            BorderBrush = Accent;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(8);
            Cursor = Cursors.Arrow;

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 6, 8, 6) };

            // 드래그 손잡이 (버튼이 아닌 곳 어디를 잡아도 이동 가능)
            panel.Children.Add(new TextBlock
            {
                Text = "⠿",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x8E)),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0),
                Cursor = Cursors.SizeAll,
                ToolTip = "드래그하여 이동",
            });

            foreach (var (label, mode) in items ?? CaptureItems)
            {
                var m = mode; // 클로저 캡쳐
                if (IsSwitchItem(mode))
                    panel.Children.Add(MakeSeparator()); // 캡쳐 ↔ 녹화 전환 버튼은 구분선 뒤에 강조 표시
                panel.Children.Add(MakeItem(label, mode == current, () => onSelect(m), emphasize: IsSwitchItem(mode)));
            }

            panel.Children.Add(MakeSeparator());
            panel.Children.Add(MakeItem("✕ 취소", false, onCancel));

            Child = panel;

            // 버튼이 아닌 영역을 잡고 드래그하면 바를 이동 (오버레이로의 이벤트 전달도 차단)
            MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (Parent is not Canvas canvas) return;
                double left = Canvas.GetLeft(this), top = Canvas.GetTop(this);
                if (double.IsNaN(left) || double.IsNaN(top)) return;
                _dragging = true;
                var p = e.GetPosition(canvas);
                _dragOffset = new Point(p.X - left, p.Y - top);
                CaptureMouse();
            };
            MouseMove += (_, e) =>
            {
                if (!_dragging || Parent is not Canvas canvas) return;
                var p = e.GetPosition(canvas);
                Canvas.SetLeft(this, Math.Clamp(p.X - _dragOffset.X, 0, Math.Max(0, canvas.ActualWidth - ActualWidth)));
                Canvas.SetTop(this, Math.Clamp(p.Y - _dragOffset.Y, 0, Math.Max(0, canvas.ActualHeight - ActualHeight)));
            };
            MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _dragging = false;
                ReleaseMouseCapture();
            };
        }

        private static System.Windows.Shapes.Rectangle MakeSeparator() => new()
        {
            Width = 1,
            Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5C)),
            Margin = new Thickness(6, 3, 6, 3),
        };

        private static readonly Brush SwitchBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));

        private static Border MakeItem(string text, bool active, Action onClick, bool emphasize = false)
        {
            var item = new Border
            {
                Background = active ? ActiveBg : NormalBg,
                BorderBrush = active ? Accent : (emphasize ? SwitchBorder : Brushes.Transparent),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = Brushes.White,
                    FontSize = 13,
                    FontWeight = emphasize ? FontWeights.SemiBold : FontWeights.Normal,
                },
            };
            if (!active)
            {
                item.MouseEnter += (_, _) => item.Background = HoverBg;
                item.MouseLeave += (_, _) => item.Background = NormalBg;
            }
            item.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
            return item;
        }
    }
}
