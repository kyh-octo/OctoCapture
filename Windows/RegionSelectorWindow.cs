using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    /// <summary>
    /// 직접(영역) 캡쳐 / 녹화 영역 지정용 드래그 선택 오버레이.
    /// 프리즈 프레임 위에서 드래그하여 영역을 선택한다. Esc로 취소.
    /// </summary>
    public class RegionSelectorWindow : OverlayWindowBase
    {
        public RECT? SelectedRect { get; private set; }  // 물리 픽셀, 가상 화면 좌표

        /// <summary>모드 바에서 다른 캡쳐 방식을 선택한 경우 (호출측에서 해당 모드로 전환)</summary>
        public Services.CaptureMode? SwitchRequest { get; private set; }

        private readonly BitmapSource _frozen;
        private readonly Path _dimPath;
        private readonly RectangleGeometry _holeGeometry = new();
        private readonly Rectangle _selBorder;
        private readonly TextBlock _sizeLabel;
        private readonly Border _sizeLabelHost;
        private readonly TextBlock _hint;

        private bool _dragging;
        private (int X, int Y) _start;

        public RegionSelectorWindow(BitmapSource frozen,
            string hintText = "드래그하여 영역을 선택하세요 (Esc: 취소)",
            Services.CaptureMode? barMode = null)
        {
            _frozen = frozen;

            var root = new Grid();
            root.Children.Add(new Image { Source = _frozen, Stretch = Stretch.Fill });

            var outerGeometry = new RectangleGeometry();
            var combined = new GeometryGroup { FillRule = FillRule.EvenOdd };
            combined.Children.Add(outerGeometry);
            combined.Children.Add(_holeGeometry);
            _dimPath = new Path { Data = combined, Fill = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)) };
            root.Children.Add(_dimPath);

            var canvas = new Canvas();
            _selBorder = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xFF)),
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent,
                Visibility = Visibility.Collapsed,
            };
            canvas.Children.Add(_selBorder);

            _sizeLabel = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 12,
                Margin = new Thickness(6, 3, 6, 3),
            };
            _sizeLabelHost = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
                CornerRadius = new CornerRadius(3),
                Child = _sizeLabel,
                Visibility = Visibility.Collapsed,
            };
            canvas.Children.Add(_sizeLabelHost);
            root.Children.Add(canvas);

            // 힌트/모드 바는 캔버스에 절대 좌표로 배치 (커서가 있는 모니터 기준 - 모니터 경계에 걸치지 않도록)
            _hint = new TextBlock
            {
                Text = hintText,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 30)),
                Padding = new Thickness(14, 8, 14, 8),
                FontSize = 14,
            };
            canvas.Children.Add(_hint);

            CaptureModeBar? bar = null;
            if (barMode is Services.CaptureMode currentMode)
            {
                bar = new CaptureModeBar(currentMode,
                    m => { SwitchRequest = m; SelectedRect = null; Close(); },
                    () => { SelectedRect = null; Close(); });
                canvas.Children.Add(bar);
            }

            Content = root;

            Loaded += (_, _) =>
            {
                outerGeometry.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
                if (bar != null) PlaceTopCenterOnCursorMonitor(bar, 16);
                PlaceTopCenterOnCursorMonitor(_hint, bar == null ? 40 : 76);
                Activate();
                Focus();
            };
            SizeChanged += (_, _) => outerGeometry.Rect = new Rect(0, 0, ActualWidth, ActualHeight);

            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { SelectedRect = null; Close(); } };
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _start = CursorPhysical();
            _hint.Visibility = Visibility.Collapsed;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_dragging) return;
            var cur = CursorPhysical();
            UpdateSelectionVisual(MakeRect(_start, cur));
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();
            var cur = CursorPhysical();
            var rect = MakeRect(_start, cur);
            if (rect.Width < 3 || rect.Height < 3)
            {
                // 너무 작은 선택은 무시하고 다시 선택
                _selBorder.Visibility = Visibility.Collapsed;
                _sizeLabelHost.Visibility = Visibility.Collapsed;
                _holeGeometry.Rect = Rect.Empty;
                _hint.Visibility = Visibility.Visible;
                return;
            }
            SelectedRect = rect;
            Close();
        }

        private static RECT MakeRect((int X, int Y) a, (int X, int Y) b) => new()
        {
            Left = Math.Min(a.X, b.X),
            Top = Math.Min(a.Y, b.Y),
            Right = Math.Max(a.X, b.X),
            Bottom = Math.Max(a.Y, b.Y),
        };

        private void UpdateSelectionVisual(RECT physical)
        {
            Rect dip = PhysicalToDip(physical);
            _holeGeometry.Rect = dip;

            _selBorder.Visibility = Visibility.Visible;
            Canvas.SetLeft(_selBorder, dip.X);
            Canvas.SetTop(_selBorder, dip.Y);
            _selBorder.Width = dip.Width;
            _selBorder.Height = dip.Height;

            _sizeLabel.Text = $"{physical.Width} × {physical.Height}";
            _sizeLabelHost.Visibility = Visibility.Visible;
            double labelY = dip.Y - 26;
            if (labelY < 0) labelY = dip.Y + 4;
            Canvas.SetLeft(_sizeLabelHost, dip.X);
            Canvas.SetTop(_sizeLabelHost, labelY);
        }

        /// <summary>선택 영역을 프리즈 프레임에서 잘라 반환(오버레이 자신이 찍히지 않음)</summary>
        public BitmapSource? GetCroppedImage()
        {
            if (SelectedRect is not RECT sel) return null;
            var crop = new Int32Rect(
                Math.Max(0, sel.Left - VirtualScreenRect.Left),
                Math.Max(0, sel.Top - VirtualScreenRect.Top),
                Math.Min(sel.Width, _frozen.PixelWidth),
                Math.Min(sel.Height, _frozen.PixelHeight));
            var cropped = new CroppedBitmap(_frozen, crop);
            var result = new WriteableBitmap(cropped); // 원본 프레임 참조를 끊어 메모리 해제 가능하게
            result.Freeze();
            return result;
        }
    }
}
