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
    /// 커서 옆에 픽셀 단위 돋보기가 따라다녀 경계를 정확히 맞출 수 있다.
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

        // ---- 돋보기 ----
        private const int MagSrcPixels = 17;       // 확대할 원본 픽셀 수 (홀수 → 중앙 픽셀이 커서)
        private const double MagBoxSize = 136;     // 돋보기 이미지 크기(DIP) → 픽셀당 8 DIP
        private const double MagOffset = 28;       // 커서와의 간격(DIP)
        private readonly Border _magHost;
        private readonly Image _magImage;
        private readonly Rectangle _magCursorPixel;
        private readonly TextBlock _magText;

        private bool _dragging;
        private (int X, int Y) _start;

        public RegionSelectorWindow(BitmapSource frozen,
            string hintText = "드래그하여 영역을 선택하세요 (Esc: 취소)",
            Services.CaptureMode? barMode = null,
            (string Label, Services.CaptureMode Mode)[]? barItems = null)
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
                    () => { SelectedRect = null; Close(); },
                    barItems);
                canvas.Children.Add(bar);
            }

            // ---- 돋보기 (커서 옆에 간격을 두고 표시, 클릭을 방해하지 않음) ----
            _magImage = new Image { Width = MagBoxSize, Height = MagBoxSize, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_magImage, BitmapScalingMode.NearestNeighbor); // 픽셀 격자가 보이도록
            double pixelDip = MagBoxSize / MagSrcPixels;
            _magCursorPixel = new Rectangle
            {
                Width = pixelDip, Height = pixelDip,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
                StrokeThickness = 1.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var magGrid = new Grid { Width = MagBoxSize, Height = MagBoxSize };
            magGrid.Children.Add(_magImage);
            // 십자선 (반투명) - 커서 픽셀의 행/열을 따라간다
            magGrid.Children.Add(_magCursorPixel);
            _magText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 11,
                Margin = new Thickness(2, 4, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var magStack = new StackPanel();
            magStack.Children.Add(magGrid);
            magStack.Children.Add(_magText);
            _magHost = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 0x22, 0x22, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x9B, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(3),
                Child = magStack,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            canvas.Children.Add(_magHost);

            Content = root;

            Loaded += (_, _) =>
            {
                outerGeometry.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
                if (bar != null) PlaceTopCenterOnCursorMonitor(bar, 16);
                PlaceTopCenterOnCursorMonitor(_hint, bar == null ? 40 : 76);
                var (cx, cy) = CursorPhysical();
                UpdateMagnifier(cx, cy, null);
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
            var cur = CursorPhysical();
            RECT? sel = _dragging ? MakeRect(_start, cur) : null;
            if (sel is RECT r) UpdateSelectionVisual(r);
            UpdateMagnifier(cur.X, cur.Y, sel);
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

        /// <summary>
        /// 커서 주변 픽셀을 확대해 커서 옆에 표시한다. 화면 가장자리에서는 반대쪽으로 뒤집는다.
        /// </summary>
        private void UpdateMagnifier(int x, int y, RECT? selection)
        {
            int half = MagSrcPixels / 2;
            int maxX = _frozen.PixelWidth - MagSrcPixels, maxY = _frozen.PixelHeight - MagSrcPixels;
            if (maxX < 0 || maxY < 0) return;

            // 원본에서 잘라낼 영역 (가장자리에서는 안쪽으로 밀어 넣고 커서 픽셀 표시 위치로 보정)
            int fx = x - VirtualScreenRect.Left, fy = y - VirtualScreenRect.Top;
            int sx = Math.Clamp(fx - half, 0, maxX), sy = Math.Clamp(fy - half, 0, maxY);
            _magImage.Source = new CroppedBitmap(_frozen, new Int32Rect(sx, sy, MagSrcPixels, MagSrcPixels));

            double pixelDip = MagBoxSize / MagSrcPixels;
            int px = Math.Clamp(fx - sx, 0, MagSrcPixels - 1), py = Math.Clamp(fy - sy, 0, MagSrcPixels - 1);
            _magCursorPixel.Margin = new Thickness(px * pixelDip, py * pixelDip, 0, 0);

            _magText.Text = selection is RECT sel
                ? $"{x}, {y}   ·   {sel.Width} × {sel.Height}"
                : $"{x}, {y}";

            // 커서가 있는 모니터 기준으로 배치: 아래쪽 절반이면 커서 위로, 오른쪽 절반이면 커서 왼쪽으로
            // (모니터 밖으로 나가거나 모니터 경계를 넘지 않도록 최종 클램프)
            Point dip = PhysicalToDip(x, y);
            Rect mon = CursorMonitorDip();
            double w = _magHost.ActualWidth > 0 ? _magHost.ActualWidth : MagBoxSize + 8;
            double h = _magHost.ActualHeight > 0 ? _magHost.ActualHeight : MagBoxSize + 30;
            bool lowerHalf = dip.Y > mon.Y + mon.Height / 2;
            bool rightHalf = dip.X > mon.X + mon.Width / 2;
            double left = rightHalf ? dip.X - MagOffset - w : dip.X + MagOffset;
            double top = lowerHalf ? dip.Y - MagOffset - h : dip.Y + MagOffset;
            left = Math.Clamp(left, mon.X, Math.Max(mon.X, mon.Right - w));
            top = Math.Clamp(top, mon.Y, Math.Max(mon.Y, mon.Bottom - h));
            Canvas.SetLeft(_magHost, left);
            Canvas.SetTop(_magHost, top);
            _magHost.Visibility = Visibility.Visible;
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
