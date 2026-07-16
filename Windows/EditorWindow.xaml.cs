using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Path = System.Windows.Shapes.Path;
using OctoCapture.Models;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    public partial class EditorWindow : Window
    {
        private enum Tool { Pen, Line, Arrow, Rect, Ellipse, Text, Mosaic, Crop }

        private readonly CaptureItem _item;
        private readonly CaptureController _controller;

        private Tool _tool = Tool.Pen;
        private Color _color = Color.FromRgb(0xFF, 0x3B, 0x30);
        private double Thickness => ThicknessSlider?.Value ?? 3;

        private int _pxW, _pxH;
        private double _zoom = 1.0;

        private bool _dragging;
        private Point _start;
        private UIElement? _preview;       // 드래그 중 임시 요소
        private Polyline? _penLine;
        private TextBox? _editingText;

        private readonly Stack<EditAction> _undoStack = new();

        public EditorWindow(CaptureItem item, CaptureController controller)
        {
            InitializeComponent();
            _item = item;
            _controller = controller;

            var img = item.LoadFullImage();
            if (img == null) { Close(); return; }
            SetBase(img);

            ThicknessSlider.ValueChanged += (_, _) =>
            {
                if (ThicknessLabel != null) ThicknessLabel.Text = ((int)ThicknessSlider.Value).ToString();
            };
            PreviewKeyDown += OnKey;
            Loaded += (_, _) => FitToWindow();
        }

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
            double vw = Scroller.ViewportWidth - 48, vh = Scroller.ViewportHeight - 48;
            if (vw <= 0 || vh <= 0) return;
            double fit = Math.Min(1.0, Math.Min(vw / _pxW, vh / _pxH));
            SetZoom(fit);
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

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            CommitTextIfEditing();
            ClipboardService.CopyImage(RenderComposite());
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            CommitTextIfEditing();
            SaveImage(RenderComposite(), _controller.Settings);
        }

        private void AddToList_Click(object sender, RoutedEventArgs e)
        {
            CommitTextIfEditing();
            _controller.AddImage(RenderComposite(), $"{_item.Title} (편집)");
        }

        public static void SaveImage(BitmapSource image, AppSettings settings)
        {
            string def = settings.ImageFormat.ToLowerInvariant();
            var dlg = new SaveFileDialog
            {
                FileName = $"OctoCapture_{DateTime.Now:yyyyMMdd_HHmmss}",
                InitialDirectory = settings.GetEffectiveSaveFolder(),
                Filter = "PNG 이미지|*.png|JPEG 이미지|*.jpg|BMP 이미지|*.bmp",
                FilterIndex = def == "jpg" ? 2 : def == "bmp" ? 3 : 1,
            };
            if (dlg.ShowDialog() != true) return;

            BitmapEncoder encoder = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 92 },
                ".bmp" => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder(),
            };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(dlg.FileName);
            encoder.Save(fs);
        }

        // ---------- 마우스 입력 ----------

        private Point ClampToImage(Point p) =>
            new(Math.Clamp(p.X, 0, _pxW), Math.Clamp(p.Y, 0, _pxH));

        private void EditRoot_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_editingText != null) { CommitTextIfEditing(); return; }
            var pos = ClampToImage(e.GetPosition(EditRoot));

            if (_tool == Tool.Text)
            {
                StartTextEdit(pos);
                return;
            }

            _dragging = true;
            _start = pos;
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
            _dragging = false;
            EditRoot.ReleaseMouseCapture();
            var pos = ClampToImage(e.GetPosition(EditRoot));
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
                    // 크기가 사실상 0이면 무시
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

        private BitmapSource RenderComposite()
        {
            // 진행 중 미리보기 요소는 제외하고 렌더
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

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_editingText != null) return; // 텍스트 입력 중에는 단축키 무시
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.Z: Undo(); e.Handled = true; break;
                    case Key.C: Copy_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                    case Key.S: Save_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                }
            }
        }

        private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            e.Handled = true;
            SetZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
        }

        private abstract class EditAction
        {
            public abstract void Undo(EditorWindow w);
        }

        private sealed class ShapeAction : EditAction
        {
            private readonly UIElement _shape;
            public ShapeAction(UIElement shape) => _shape = shape;
            public override void Undo(EditorWindow w) => w.ShapeCanvas.Children.Remove(_shape);
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
            public override void Undo(EditorWindow w)
            {
                w.ShapeCanvas.Children.Clear();
                w.SetBase(_prevBase);
                foreach (var s in _prevShapes) w.ShapeCanvas.Children.Add(s);
                w.FitToWindow();
            }
        }
    }
}
