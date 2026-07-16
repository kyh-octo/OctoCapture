using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    public enum PickerMode { TopLevelWindow, UnitControl }

    /// <summary>
    /// 창 캡쳐/단위별 캡쳐용 픽커. 프리즈 프레임 위에서 마우스를 움직이면
    /// 해당 위치의 창(또는 컨트롤)이 강조되고 클릭으로 선택한다. Esc로 취소.
    /// </summary>
    public class WindowPickerWindow : OverlayWindowBase
    {
        public WindowInfo? Selected { get; private set; }

        /// <summary>모드 바에서 다른 캡쳐 방식을 선택한 경우 (호출측에서 해당 모드로 전환)</summary>
        public Services.CaptureMode? SwitchRequest { get; private set; }

        private readonly BitmapSource _frozen;
        private readonly List<WindowInfo> _windows;
        private readonly PickerMode _mode;
        private readonly RectangleGeometry _holeGeometry = new();
        private readonly Rectangle _highlight;
        private readonly TextBlock _label;
        private readonly Border _labelHost;

        public WindowPickerWindow(BitmapSource frozen, PickerMode mode, string hintText,
            Services.CaptureMode? barMode = null, List<WindowInfo>? customTargets = null)
        {
            _frozen = frozen;
            _mode = mode;

            // 프리즈 시점의 창 목록 스냅샷 (customTargets가 있으면 그것을 사용 - 예: 모니터 목록)
            _windows = customTargets ?? (mode == PickerMode.UnitControl
                ? WindowEnumerator.GetWindowsWithChildren()
                : WindowEnumerator.GetTopLevelWindows());

            var root = new Grid();
            root.Children.Add(new Image { Source = _frozen, Stretch = Stretch.Fill });

            var outerGeometry = new RectangleGeometry();
            var combined = new GeometryGroup { FillRule = FillRule.EvenOdd };
            combined.Children.Add(outerGeometry);
            combined.Children.Add(_holeGeometry);
            root.Children.Add(new Path { Data = combined, Fill = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) });

            var canvas = new Canvas();
            _highlight = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x2B)),
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
            };
            canvas.Children.Add(_highlight);

            _label = new TextBlock { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(6, 3, 6, 3) };
            _labelHost = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                CornerRadius = new CornerRadius(3),
                Child = _label,
                Visibility = Visibility.Collapsed,
            };
            canvas.Children.Add(_labelHost);
            root.Children.Add(canvas);

            // 힌트/모드 바는 캔버스에 절대 좌표로 배치 (커서가 있는 모니터 기준 - 모니터 경계에 걸치지 않도록)
            var hint = new TextBlock
            {
                Text = hintText,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 30)),
                Padding = new Thickness(14, 8, 14, 8),
                FontSize = 14,
            };
            canvas.Children.Add(hint);

            CaptureModeBar? bar = null;
            if (barMode is Services.CaptureMode currentMode)
            {
                bar = new CaptureModeBar(currentMode,
                    m => { SwitchRequest = m; Selected = null; Close(); },
                    () => { Selected = null; Close(); });
                canvas.Children.Add(bar);
            }

            Content = root;

            Loaded += (_, _) =>
            {
                if (bar != null) PlaceTopCenterOnCursorMonitor(bar, 16);
                PlaceTopCenterOnCursorMonitor(hint, bar == null ? 40 : 76);
                Activate();
                Focus();
                UpdateHover();
            };
            MouseMove += (_, _) => UpdateHover();
            MouseLeftButtonDown += (_, _) =>
            {
                if (_hovered != null) { Selected = _hovered; Close(); }
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Selected = null; Close(); } };
            SizeChanged += (_, _) => outerGeometry.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        }

        private WindowInfo? _hovered;

        private void UpdateHover()
        {
            var (x, y) = CursorPhysical();
            _hovered = _mode == PickerMode.UnitControl
                ? WindowEnumerator.HitTestSmallest(_windows, x, y)
                : WindowEnumerator.HitTest(_windows, x, y);

            if (_hovered == null)
            {
                _highlight.Visibility = Visibility.Collapsed;
                _labelHost.Visibility = Visibility.Collapsed;
                _holeGeometry.Rect = Rect.Empty;
                return;
            }

            Rect dip = PhysicalToDip(_hovered.Bounds);
            _holeGeometry.Rect = dip;
            _highlight.Visibility = Visibility.Visible;
            Canvas.SetLeft(_highlight, dip.X);
            Canvas.SetTop(_highlight, dip.Y);
            _highlight.Width = Math.Max(0, dip.Width);
            _highlight.Height = Math.Max(0, dip.Height);

            string title = string.IsNullOrEmpty(_hovered.Title) ? "(제목 없음)" : _hovered.Title;
            if (title.Length > 60) title = title[..60] + "…";
            _label.Text = $"{title}  ·  {_hovered.Bounds.Width} × {_hovered.Bounds.Height}";
            _labelHost.Visibility = Visibility.Visible;
            double labelY = dip.Y - 26;
            if (labelY < 0) labelY = dip.Y + 4;
            Canvas.SetLeft(_labelHost, Math.Max(0, dip.X));
            Canvas.SetTop(_labelHost, Math.Max(0, labelY));
        }

        /// <summary>선택된 창 영역을 프리즈 프레임에서 잘라 반환</summary>
        public BitmapSource? GetCroppedImage()
        {
            if (Selected == null) return null;
            RECT b = Selected.Bounds;
            int l = Math.Max(b.Left, VirtualScreenRect.Left);
            int t = Math.Max(b.Top, VirtualScreenRect.Top);
            int r = Math.Min(b.Right, VirtualScreenRect.Right);
            int bo = Math.Min(b.Bottom, VirtualScreenRect.Bottom);
            if (r - l <= 0 || bo - t <= 0) return null;
            var crop = new Int32Rect(l - VirtualScreenRect.Left, t - VirtualScreenRect.Top, r - l, bo - t);
            var cropped = new CroppedBitmap(_frozen, crop);
            var result = new WriteableBitmap(cropped);
            result.Freeze();
            return result;
        }
    }
}
