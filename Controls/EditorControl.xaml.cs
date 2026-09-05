using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OctoCapture.Models;
using Path = System.Windows.Shapes.Path;

namespace OctoCapture.Controls
{
    /// <summary>
    /// 메인 화면에 내장되는 이미지 편집기 (알캡쳐 스타일 즉석 편집).
    /// 펜/도형/텍스트/모자이크/자르기, [저장]=현재 캡쳐 덮어쓰기, [새 캡쳐 저장]=목록에 추가.
    /// </summary>
    public partial class EditorControl : UserControl
    {
        private enum Tool { Pen, Line, Arrow, Rect, Ellipse, Text, Mosaic, Crop }

        /// <summary>[새 캡쳐 저장] 클릭 시 합성 결과와 원본 항목을 전달</summary>
        public event Action<BitmapSource, CaptureItem>? SaveAsNewRequested;

        /// <summary>[저장](덮어쓰기) 후 알림 (미리보기 제목 갱신용)</summary>
        public event Action<CaptureItem>? Edited;

        public CaptureItem? CurrentItem { get; private set; }

        private Tool _tool = Tool.Pen;
        private Color _color = Color.FromRgb(0xFF, 0x3B, 0x30);
        private double Thickness => ThicknessSlider?.Value ?? 3;

        private int _pxW, _pxH;
        private double _zoom = 1.0;

        private bool _dragging;
        private Point _start;
        private UIElement? _preview;
        private Polyline? _penLine;
        private TextBox? _editingText;

        private readonly Stack<EditAction> _undoStack = new();

        public EditorControl()
        {
            InitializeComponent();
            ThicknessSlider.ValueChanged += (_, _) =>
            {
                if (ThicknessLabel != null) ThicknessLabel.Text = ((int)ThicknessSlider.Value).ToString();
            };
            SizeChanged += (_, _) => { if (_undoStack.Count == 0 && CurrentItem != null) FitToWindow(); };
            // Alt+Tab 등으로 드래그 중 마우스 캡처를 잃으면 마지막 위치로 도형을 확정 (고아 미리보기 방지)
            EditRoot.LostMouseCapture += (_, _) => { if (_dragging) FinishDrag(_lastPos); };
        }

        private Point _lastPos;

        // ---------- 항목 로드/해제 ----------

        /// <summary>캡쳐 항목을 편집기에 로드한다 (기존 편집 내용은 버려짐).</summary>
        public void LoadItem(CaptureItem item)
        {
            var img = item.LoadFullImage();
            if (img == null) { ClearEditor(); return; }
            CurrentItem = item;
            ResetEditState();
            SetBase(img);
            Dispatcher.BeginInvoke(FitToWindow, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>편집기를 비운다 (선택 해제/삭제 시).</summary>
        public void ClearEditor()
        {
            CurrentItem = null;
            ResetEditState();
            BaseImage.Source = null;
            _pxW = _pxH = 0;
            StatusSize.Text = "";
        }

        private void ResetEditState()
        {
            CancelTextEdit();
            _dragging = false;
            _preview = null;
            _penLine = null;
            ShapeCanvas.Children.Clear();
            _undoStack.Clear();
        }

        public bool HasImage => CurrentItem != null && BaseImage.Source != null;

        /// <summary>저장하지 않은 편집 내용이 있는가</summary>
        public bool IsDirty => _undoStack.Count > 0 || _editingText != null || _dragging;

        /// <summary>텍스트 도구로 입력 중인가 (전역 단축키 라우팅 판단용)</summary>
        public bool IsEditingText => _editingText != null;

        private void SetBase(BitmapSource source)
        {
            _pxW = source.PixelWidth;
            _pxH = source.PixelHeight;
            BaseImage.Source = source;
            BaseImage.Width = _pxW; BaseImage.Height = _pxH;
            EditRoot.Width = _pxW; EditRoot.Height = _pxH;
            ShapeCanvas.Width = _pxW; ShapeCanvas.Height = _pxH;
            StatusSize.Text = $"{_pxW} × {_pxH}";
        }

        private void FitToWindow()
        {
            if (_pxW == 0 || _pxH == 0) return;
            double vw = Scroller.ViewportWidth - 30, vh = Scroller.ViewportHeight - 30;
            if (vw <= 0 || vh <= 0) return;
            SetZoom(Math.Min(1.0, Math.Min(vw / _pxW, vh / _pxH)));
        }

        private void SetZoom(double zoom)
        {
            _zoom = Math.Clamp(zoom, 0.1, 8.0);
            ZoomHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
            StatusZoom.Text = $"확대 {(int)(_zoom * 100)}%";
        }

        // ---------- 툴바 ----------

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
                _tool = Enum.Parse<Tool>(tag);
            CommitTextIfEditing();
        }

        private void Swatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Background is SolidColorBrush sb)
            {
                _color = sb.Color;
                CurrentColorBox.Background = new SolidColorBrush(_color);
            }
        }

        private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

        /// <summary>[저장] - 편집 결과를 현재 캡쳐 항목에 덮어쓴다.</summary>
        private void SaveOverwrite_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentItem == null) return;
            CommitTextIfEditing();
            var composite = RenderComposite();
            CurrentItem.SetImage(composite);
            // 저장된 결과를 새 기준으로 다시 로드 (도형은 이미지에 구워졌고 실행취소 스택 초기화)
            ResetEditState();
            SetBase(composite);
            Edited?.Invoke(CurrentItem);
        }

        /// <summary>[새 캡쳐 저장] - 편집 결과를 목록에 새 항목으로 추가한다.</summary>
        private void SaveAsNew_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentItem == null) return;
            CommitTextIfEditing();
            SaveAsNewRequested?.Invoke(RenderComposite(), CurrentItem);
        }

        // ---------- 키보드 (메인 창에서 라우팅) ----------

        /// <summary>Ctrl+Z 등 편집 단축키 처리. 처리했으면 true.</summary>
        public bool HandleKey(KeyEventArgs e)
        {
            if (!HasImage || _editingText != null) return false;
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                Undo();
                return true;
            }
            return false;
        }

        // ---------- 마우스 입력 ----------

        private Point ClampToImage(Point p) =>
            new(Math.Clamp(p.X, 0, _pxW), Math.Clamp(p.Y, 0, _pxH));

        private void EditRoot_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!HasImage) return;
            if (_editingText != null) { CommitTextIfEditing(); return; }
            var pos = ClampToImage(e.GetPosition(EditRoot));

            if (_tool == Tool.Text)
            {
                StartTextEdit(pos);
                return;
            }

            _dragging = true;
            _start = pos;
            _lastPos = pos;
            EditRoot.CaptureMouse();

            var brush = new SolidColorBrush(_color);
            switch (_tool)
            {
                case Tool.Pen:
                    _penLine = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = Thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    };
                    _penLine.Points.Add(pos);
                    _preview = _penLine;
                    break;
                case Tool.Line:
                    _preview = new Line
                    {
                        X1 = pos.X, Y1 = pos.Y, X2 = pos.X, Y2 = pos.Y,
                        Stroke = brush, StrokeThickness = Thickness,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    };
                    break;
                case Tool.Arrow:
                    _preview = new Path
                    {
                        Stroke = brush, Fill = brush,
                        StrokeThickness = Thickness, StrokeLineJoin = PenLineJoin.Round,
                    };
                    break;
                case Tool.Rect:
                    _preview = new Rectangle { Stroke = brush, StrokeThickness = Thickness };
                    break;
                case Tool.Ellipse:
                    _preview = new Ellipse { Stroke = brush, StrokeThickness = Thickness };
                    break;
                case Tool.Mosaic:
                case Tool.Crop:
                    _preview = new Rectangle
                    {
                        Stroke = _tool == Tool.Crop ? Brushes.White : Brushes.Silver,
                        StrokeThickness = 1.5,
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                    };
                    break;
            }
            if (_preview != null)
                ShapeCanvas.Children.Add(_preview);
        }

        private void EditRoot_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _preview == null) return;
            var pos = ClampToImage(e.GetPosition(EditRoot));
            _lastPos = pos;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                // 버튼이 떼진 채로 이동 이벤트가 오면(캡처 상실 후) 드래그 종료
                FinishDrag(pos);
                return;
            }

            switch (_tool)
            {
                case Tool.Pen:
                    var last = _penLine!.Points[^1];
                    if ((pos - last).Length >= 1.2) _penLine.Points.Add(pos);
                    break;
                case Tool.Line:
                    var ln = (Line)_preview;
                    ln.X2 = pos.X; ln.Y2 = pos.Y;
                    break;
                case Tool.Arrow:
                    ((Path)_preview).Data = BuildArrowGeometry(_start, pos, Thickness);
                    break;
                case Tool.Rect:
                case Tool.Ellipse:
                case Tool.Mosaic:
                case Tool.Crop:
                    var r = MakeRect(_start, pos);
                    var shape = (Shape)_preview;
                    Canvas.SetLeft(shape, r.X);
                    Canvas.SetTop(shape, r.Y);
                    shape.Width = r.Width;
                    shape.Height = r.Height;
                    break;
            }
        }

        private void EditRoot_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            FinishDrag(ClampToImage(e.GetPosition(EditRoot)));
        }

        /// <summary>드래그 종료 처리 (MouseUp / 캡처 상실 공용)</summary>
        private void FinishDrag(Point pos)
        {
            if (!_dragging) return;
            _dragging = false;
            if (EditRoot.IsMouseCaptured) EditRoot.ReleaseMouseCapture();
            var preview = _preview;
            _preview = null;
            _penLine = null;
            if (preview == null) return;

            switch (_tool)
            {
                case Tool.Mosaic:
                {
                    ShapeCanvas.Children.Remove(preview);
                    var r = MakeRect(_start, pos);
                    if (r.Width >= 4 && r.Height >= 4) ApplyMosaic(r);
                    break;
                }
                case Tool.Crop:
                {
                    ShapeCanvas.Children.Remove(preview);
                    var r = MakeRect(_start, pos);
                    if (r.Width >= 4 && r.Height >= 4) ApplyCrop(r);
                    break;
                }
                default:
                {
                    if ((_tool is Tool.Rect or Tool.Ellipse) && ((Shape)preview).Width < 2 && ((Shape)preview).Height < 2)
                    {
                        ShapeCanvas.Children.Remove(preview);
                        return;
                    }
                    _undoStack.Push(new ShapeAction(preview));
                    break;
                }
            }
        }

        private static Rect MakeRect(Point a, Point b) => new(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private static Geometry BuildArrowGeometry(Point start, Point end, double thickness)
        {
            var v = end - start;
            double len = v.Length;
            if (len < 1) return new LineGeometry(start, end);
            var dir = v / len;
            var perp = new Vector(-dir.Y, dir.X);
            double headLen = Math.Min(len * 0.5, thickness * 4 + 8);
            double headW = thickness * 2 + 4;
            Point basePt = end - dir * headLen;

            var g = new GeometryGroup();
            g.Children.Add(new LineGeometry(start, basePt));
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(end, true, true);
                ctx.LineTo(basePt + perp * headW, true, true);
                ctx.LineTo(basePt - perp * headW, true, true);
            }
            g.Children.Add(head);
            return g;
        }

        // ---------- 텍스트 ----------

        private void StartTextEdit(Point pos)
        {
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(_color),
                FontSize = 10 + Thickness * 3,
                FontWeight = FontWeights.SemiBold,
                MinWidth = 40,
                AcceptsReturn = false,
            };
            Canvas.SetLeft(tb, pos.X);
            Canvas.SetTop(tb, pos.Y);
            ShapeCanvas.Children.Add(tb);
            _editingText = tb;

            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CommitTextIfEditing(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelTextEdit(); e.Handled = true; }
            };
            tb.LostFocus += (_, _) => CommitTextIfEditing();
            Dispatcher.BeginInvoke(() => tb.Focus());
        }

        private void CommitTextIfEditing()
        {
            var tb = _editingText;
            if (tb == null) return;
            _editingText = null;

            string text = tb.Text;
            double x = Canvas.GetLeft(tb), y = Canvas.GetTop(tb);
            double fontSize = tb.FontSize;
            ShapeCanvas.Children.Remove(tb);
            if (string.IsNullOrWhiteSpace(text)) return;

            var block = new TextBlock
            {
                Text = text,
                Foreground = tb.Foreground,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);
            ShapeCanvas.Children.Add(block);
            _undoStack.Push(new ShapeAction(block));
        }

        private void CancelTextEdit()
        {
            if (_editingText == null) return;
            ShapeCanvas.Children.Remove(_editingText);
            _editingText = null;
        }

        // ---------- 모자이크 / 자르기 ----------

        private void ApplyMosaic(Rect region)
        {
            var composite = RenderComposite();
            var crop = new Int32Rect((int)region.X, (int)region.Y, (int)region.Width, (int)region.Height);
            if (crop.Width <= 0 || crop.Height <= 0) return;
            var cropped = new CroppedBitmap(composite, crop);

            int block = (int)Math.Max(6, 4 + Thickness * 2);
            double scale = 1.0 / block;
            var down = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
            var small = new WriteableBitmap(down);
            small.Freeze();

            var img = new Image
            {
                Source = small,
                Width = region.Width,
                Height = region.Height,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(img, region.X);
            Canvas.SetTop(img, region.Y);
            ShapeCanvas.Children.Add(img);
            _undoStack.Push(new ShapeAction(img));
        }

        private void ApplyCrop(Rect region)
        {
            var composite = RenderComposite();
            var crop = new Int32Rect((int)region.X, (int)region.Y, (int)region.Width, (int)region.Height);
            if (crop.Width <= 0 || crop.Height <= 0) return;
            var cropped = new WriteableBitmap(new CroppedBitmap(composite, crop));
            cropped.Freeze();

            var prevBase = (BitmapSource)BaseImage.Source;
            var prevShapes = ShapeCanvas.Children.Cast<UIElement>().ToList();
            _undoStack.Push(new RebaseAction(prevBase, prevShapes));

            ShapeCanvas.Children.Clear();
            SetBase(cropped);
            FitToWindow();
        }

        // ---------- 합성 / 실행취소 ----------

        /// <summary>현재 편집 상태를 합성한 이미지 (복사/파일 저장/덮어쓰기에 사용)</summary>
        public BitmapSource RenderComposite()
        {
            CommitTextIfEditing();
            if (_preview != null) ShapeCanvas.Children.Remove(_preview);
            EditRoot.UpdateLayout();
            var rtb = new RenderTargetBitmap(_pxW, _pxH, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(EditRoot);
            rtb.Freeze();
            if (_preview != null) ShapeCanvas.Children.Add(_preview);
            return rtb;
        }

        private void Undo()
        {
            CancelTextEdit();
            if (_undoStack.Count == 0) return;
            _undoStack.Pop().Undo(this);
        }

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
        }

        private abstract class EditAction
        {
            public abstract void Undo(EditorControl c);
        }

        private sealed class ShapeAction : EditAction
        {
            private readonly UIElement _shape;
            public ShapeAction(UIElement shape) => _shape = shape;
            public override void Undo(EditorControl c) => c.ShapeCanvas.Children.Remove(_shape);
        }

        private sealed class RebaseAction : EditAction
        {
            private readonly BitmapSource _prevBase;
            private readonly List<UIElement> _prevShapes;
            public RebaseAction(BitmapSource prevBase, List<UIElement> prevShapes)
            {
                _prevBase = prevBase;
                _prevShapes = prevShapes;
            }
            public override void Undo(EditorControl c)
            {
                c.ShapeCanvas.Children.Clear();
                c.SetBase(_prevBase);
                foreach (var s in _prevShapes) c.ShapeCanvas.Children.Add(s);
                c.FitToWindow();
            }
        }
    }
}
