using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OctoCapture.Models;
using OctoCapture.Services;
using OctoCapture.Windows;

namespace OctoCapture
{
    public partial class MainWindow : Window
    {
        private readonly CaptureController _controller;
        private readonly RecordingCoordinator _recorder;
        private CaptureItem? _observedItem;          // PropertyChanged 구독 중인 항목
        private bool _suppressSelectionChanged;      // 선택 되돌리기 중 재진입 방지
        private readonly HashSet<string> _converting = new(); // 진행 중인 변환 (항목ID:형식)
        private int _busyOps;                        // 진행 중인 변환/저장 작업 수

        public MainWindow(CaptureController controller, RecordingCoordinator recorder)
        {
            InitializeComponent();
            _controller = controller;
            _recorder = recorder;

            HistoryList.ItemsSource = _controller.History;
            _controller.ItemCaptured += OnItemCaptured;

            // 편집기 배선: [새 캡쳐 저장] → 목록에 추가, [저장] → 제목 갱신
            Editor.SaveAsNewRequested += (composite, source) =>
                _controller.AddImage(composite, $"{source.Title} (편집)", copyToClipboard: false);
            Editor.Edited += _ => RefreshPreviewTitle();

            // 트레이에 숨어있는 동안 생략했던 편집기 로드를 창이 보일 때 수행 (편집 중 내용은 보존됨)
            IsVisibleChanged += (_, _) => { if (IsVisible) UpdatePreview(Selected); };
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            UpdatePreview(null);
        }

        private void OnItemCaptured(CaptureItem item, bool copied)
        {
            Dispatcher.BeginInvoke(() =>
            {
                // 편집 중인 내용이 있으면 선택을 강제로 옮기지 않는다 (새 항목은 목록 맨 위에 추가됨)
                if (_controller.History.Count > 0 && !Editor.IsDirty)
                    HistoryList.SelectedIndex = 0;
            });
        }

        private CaptureItem? Selected => HistoryList.SelectedItem as CaptureItem;

        // ---------- 키보드 ----------

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Editor.IsVisible && Editor.HandleKey(e)) { e.Handled = true; return; }
            // 텍스트 입력 중에는 전역 단축키를 가로채지 않는다
            if (Editor.IsEditingText || Keyboard.FocusedElement is TextBox) return;
            if (Keyboard.Modifiers != ModifierKeys.Control || Selected == null) return;

            if (e.Key == Key.C) { Copy_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.S) { Save_Click(this, new RoutedEventArgs()); e.Handled = true; }
        }

        // ---------- 툴바 ----------

        private void BtnRegion_Click(object s, RoutedEventArgs e) => _controller.CaptureRegion();
        private void BtnWindow_Click(object s, RoutedEventArgs e) => _controller.CaptureWindow();
        private void BtnUnit_Click(object s, RoutedEventArgs e) => _controller.CaptureUnit();
        private void BtnMonitor_Click(object s, RoutedEventArgs e) => _controller.CaptureMonitor();
        private void BtnFull_Click(object s, RoutedEventArgs e) => _controller.CaptureFull();
        private void BtnScroll_Click(object s, RoutedEventArgs e) => _controller.CaptureScroll();
        private void BtnRecord_Click(object s, RoutedEventArgs e) => _recorder.Toggle();

        private void BtnSettings_Click(object s, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_controller.Settings) { Owner = this };
            if (win.ShowDialog() == true)
                ((App)Application.Current).ApplySettings();
        }

        // ---------- 목록 / 미리보기 ----------

        private void HistoryList_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged) return;

            // 저장하지 않은 편집 내용이 있으면 확인 후 이동
            var editing = Editor.CurrentItem;
            if (editing != null && editing != Selected && Editor.IsDirty && _controller.History.Contains(editing))
            {
                var r = MessageBox.Show(
                    "저장하지 않은 편집 내용이 있습니다.\n버리고 다른 항목으로 이동할까요?\n\n" +
                    "(편집 내용을 남기려면 [아니요]를 누른 뒤 [저장] 또는 [새 캡쳐 저장]을 사용하세요)",
                    "OctoCapture", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes)
                {
                    _suppressSelectionChanged = true;
                    HistoryList.SelectedItem = editing;
                    _suppressSelectionChanged = false;
                    return;
                }
            }
            UpdatePreview(Selected);
        }

        private void HistoryList_MouseDoubleClick(object s, MouseButtonEventArgs e)
        {
            if (Selected is { Kind: CaptureItemKind.Video })
                Play_Click(s, e);
        }

        private void UpdatePreview(CaptureItem? item)
        {
            // 이전 항목 구독 해제 후 새 항목 구독 (편집/트림으로 Info가 바뀌면 제목 갱신)
            if (_observedItem != null) _observedItem.PropertyChanged -= ObservedItem_Changed;
            _observedItem = item;
            if (_observedItem != null) _observedItem.PropertyChanged += ObservedItem_Changed;

            bool has = item != null;
            EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            BtnCopy.IsEnabled = has;
            BtnSave.IsEnabled = has;
            BtnDelete.IsEnabled = has;

            bool isImage = item?.Kind == CaptureItemKind.Image;
            bool isVideo = item?.Kind == CaptureItemKind.Video;

            VideoPane.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;

            if (isVideo)
            {
                Editor.Visibility = Visibility.Collapsed;
                Editor.ClearEditor();
                VideoThumb.Source = item!.Thumbnail;
            }
            else if (isImage)
            {
                Editor.Visibility = Visibility.Visible;
                // 같은 항목이 이미 로드되어 있으면 다시 로드하지 않는다 (편집 중 내용 보존)
                if (Editor.CurrentItem != item)
                {
                    // 창이 숨겨져 있으면(트레이) 원본 디코딩을 미뤄 메모리를 아낀다 (IsVisibleChanged에서 로드)
                    if (IsVisible) Editor.LoadItem(item!);
                    else Editor.ClearEditor();
                }
            }
            else
            {
                Editor.Visibility = Visibility.Collapsed;
                Editor.ClearEditor();
                VideoThumb.Source = null;
            }

            RefreshPreviewTitle();
        }

        private void ObservedItem_Changed(object? s, PropertyChangedEventArgs e) =>
            Dispatcher.BeginInvoke(RefreshPreviewTitle);

        private void RefreshPreviewTitle()
        {
            var item = Selected;
            PreviewTitle.Text = item == null ? "미리보기" : $"{item.Title}  ·  {item.Info}";
        }

        private void Play_Click(object s, RoutedEventArgs e)
        {
            if (Selected is not { Kind: CaptureItemKind.Video } item) return;
            // 같은 항목의 플레이어가 이미 열려 있으면 그 창을 앞으로 (중복 트림으로 인한 꼬임 방지)
            var existing = Application.Current.Windows.OfType<VideoPlayerWindow>().FirstOrDefault(w => w.Item == item);
            if (existing != null) { existing.Activate(); return; }
            var player = new VideoPlayerWindow(item, _controller);
            player.Show();
            player.Activate();
        }

        // ---------- 변환/저장 중 재진입 방지 ----------

        private void BeginBusy()
        {
            _busyOps++;
            ActionButtons.IsEnabled = false;
        }

        private void EndBusy()
        {
            _busyOps = Math.Max(0, _busyOps - 1);
            ActionButtons.IsEnabled = _busyOps == 0;
        }

        // ---------- 복사 (이미지: 편집 상태, 동영상: 형식 선택) ----------

        private void Copy_Click(object s, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null || _busyOps > 0) return;

            if (item.Kind == CaptureItemKind.Image)
            {
                // 편집 중인 내용까지 그대로 복사 (WYSIWYG)
                bool ok = Editor.HasImage && Editor.CurrentItem == item
                    ? ClipboardService.CopyImage(Editor.RenderComposite())
                    : item.PngData != null && ClipboardService.CopyImage(item.PngData);
                if (!ok) MessageBox.Show("클립보드 복사에 실패했습니다.", "OctoCapture");
                return;
            }

            // 동영상: 형식을 골라 파일로 복사
            var menu = new ContextMenu();
            foreach (var (label, format) in new[] { ("MP4로 복사", "mp4"), ("GIF로 복사", "gif"), ("WebP로 복사", "webp") })
            {
                var mi = new MenuItem { Header = label };
                string f = format;
                mi.Click += (_, _) => CopyVideoAs(item, f);
                menu.Items.Add(mi);
            }
            menu.PlacementTarget = BtnCopy;
            menu.IsOpen = true;
        }

        /// <summary>동영상을 지정 형식으로 변환(캐시 활용)해 파일로 클립보드에 복사</summary>
        private async void CopyVideoAs(CaptureItem item, string format)
        {
            if (item.VideoPath == null || !File.Exists(item.VideoPath)) return;

            if (format == "mp4")
            {
                if (!ClipboardService.CopyFile(item.VideoPath))
                    MessageBox.Show("클립보드 복사에 실패했습니다.", "OctoCapture");
                return;
            }

            // 이미 변환한 적이 있으면 재사용
            if (item.ConvertedCache.TryGetValue(format, out var cached) && File.Exists(cached))
            {
                if (!ClipboardService.CopyFile(cached))
                    MessageBox.Show("클립보드 복사에 실패했습니다.", "OctoCapture");
                return;
            }

            string key = $"{item.Id}:{format}";
            if (!_converting.Add(key)) return; // 같은 변환이 이미 진행 중
            BeginBusy();
            try
            {
                string? ffmpeg = await FfmpegService.EnsureFfmpegAsync();
                if (ffmpeg == null) return;

                Directory.CreateDirectory(RecordingCoordinator.TempDir);
                string output = System.IO.Path.Combine(RecordingCoordinator.TempDir, $"copy_{item.Id}.{format}");
                bool ok = await BusyWindow.RunAsync($"{format.ToUpper()} 변환 중… 잠시만 기다려주세요.",
                    () => FfmpegService.ConvertAsync(ffmpeg, item.VideoPath, output, format, _controller.Settings.GifFps));
                if (!ok)
                {
                    MessageBox.Show("변환에 실패했습니다.", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!_controller.History.Contains(item))
                {
                    // 변환 중 항목이 삭제됨: 결과물 정리 후 종료
                    TempFileCleaner.DeleteLater(output);
                    return;
                }
                item.ConvertedCache[format] = output;
                if (!ClipboardService.CopyFile(output))
                    MessageBox.Show("클립보드 복사에 실패했습니다.", "OctoCapture");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"복사에 실패했습니다.\n{ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _converting.Remove(key);
                EndBusy();
            }
        }

        // ---------- 파일 저장 ----------

        private async void Save_Click(object s, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null || _busyOps > 0) return;

            if (item.Kind == CaptureItemKind.Image)
            {
                // 편집 중인 내용까지 그대로 저장 (WYSIWYG)
                var img = Editor.HasImage && Editor.CurrentItem == item
                    ? Editor.RenderComposite()
                    : item.LoadFullImage();
                if (img != null) SaveImageToFile(img, _controller.Settings);
                return;
            }

            // 동영상 저장: MP4 복사 또는 ffmpeg로 GIF/WebP 변환
            if (item.VideoPath == null || !File.Exists(item.VideoPath)) return;
            string def = _controller.Settings.VideoFormat.ToLowerInvariant();
            var dlg = new SaveFileDialog
            {
                FileName = $"OctoCapture_{item.CreatedAt:yyyyMMdd_HHmmss}",
                InitialDirectory = _controller.Settings.GetEffectiveSaveFolder(),
                Filter = "MP4 동영상|*.mp4|GIF 애니메이션|*.gif|WebP 애니메이션|*.webp",
                FilterIndex = def == "gif" ? 2 : def == "webp" ? 3 : 1,
            };
            if (dlg.ShowDialog() != true) return;

            string ext = System.IO.Path.GetExtension(dlg.FileName).ToLowerInvariant();
            BeginBusy();
            try
            {
                if (ext == ".mp4")
                {
                    File.Copy(item.VideoPath, dlg.FileName, true);
                    return;
                }

                string? ffmpeg = await FfmpegService.EnsureFfmpegAsync();
                if (ffmpeg == null) return;

                string format = ext == ".gif" ? "gif" : "webp";
                bool ok = await BusyWindow.RunAsync($"{format.ToUpper()} 변환 중… 잠시만 기다려주세요.",
                    () => FfmpegService.ConvertAsync(ffmpeg, item.VideoPath, dlg.FileName, format, _controller.Settings.GifFps));
                if (!ok)
                    MessageBox.Show("변환에 실패했습니다.", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"저장에 실패했습니다.\n{ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { EndBusy(); }
        }

        /// <summary>이미지를 파일로 저장 (PNG/JPG/BMP)</summary>
        public static void SaveImageToFile(BitmapSource image, AppSettings settings)
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

            try
            {
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
            catch (Exception ex)
            {
                MessageBox.Show($"저장에 실패했습니다.\n{ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Delete_Click(object s, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null || _busyOps > 0) return;
            if (Editor.CurrentItem == item) Editor.ClearEditor();
            _controller.History.Remove(item);
            item.DeleteTempFiles(); // 클립보드가 참조 중인 파일은 보호됨
            UpdatePreview(Selected);
        }

        // ---------- 트레이 최소화 ----------

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_controller.Settings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
            Application.Current.Shutdown();
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        }
    }
}
