using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

        public MainWindow(CaptureController controller, RecordingCoordinator recorder)
        {
            InitializeComponent();
            _controller = controller;
            _recorder = recorder;

            HistoryList.ItemsSource = _controller.History;
            _controller.ItemCaptured += OnItemCaptured;
            // 트레이에 숨어있는 동안 생략했던 원본 미리보기를 창이 보일 때 로드
            IsVisibleChanged += (_, _) => { if (IsVisible) UpdatePreview(Selected); };
            UpdatePreview(null);
        }

        private void OnItemCaptured(CaptureItem item)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_controller.History.Count > 0)
                    HistoryList.SelectedIndex = 0;
            });
        }

        private CaptureItem? Selected => HistoryList.SelectedItem as CaptureItem;

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

        private void HistoryList_SelectionChanged(object s, SelectionChangedEventArgs e) =>
            UpdatePreview(Selected);

        private void HistoryList_MouseDoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (Selected is { Kind: CaptureItemKind.Image } item)
                _controller.OpenEditor(item);
            else if (Selected is { Kind: CaptureItemKind.Video })
                Play_Click(s, e);
        }

        private void UpdatePreview(CaptureItem? item)
        {
            bool has = item != null;
            EmptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            PreviewImage.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            BtnEdit.IsEnabled = has; // 이미지: 편집기, 동영상: 플레이어(트림)
            BtnCopy.IsEnabled = has;
            BtnSave.IsEnabled = has;
            BtnDelete.IsEnabled = has;
            PlayButton.Visibility = item?.Kind == CaptureItemKind.Video ? Visibility.Visible : Visibility.Collapsed;

            if (item == null)
            {
                PreviewImage.Source = null; // 이전 큰 이미지 참조 해제
                PreviewTitle.Text = "미리보기";
                return;
            }

            PreviewTitle.Text = $"{item.Title}  ·  {item.Info}";
            // 창이 숨겨져 있으면(트레이) 원본 디코딩을 미뤄 메모리를 아낀다
            PreviewImage.Source = item.Kind == CaptureItemKind.Image && IsVisible
                ? item.LoadFullImage()   // 필요할 때만 디코딩 (히스토리는 PNG 바이트로만 유지)
                : item.Thumbnail;
        }

        private void Play_Click(object s, RoutedEventArgs e)
        {
            if (Selected is not { Kind: CaptureItemKind.Video } item) return;
            var player = new VideoPlayerWindow(item, _controller);
            player.Show();
            player.Activate();
        }

        private void Edit_Click(object s, RoutedEventArgs e)
        {
            if (Selected is { Kind: CaptureItemKind.Image } item)
                _controller.OpenEditor(item);
            else if (Selected is { Kind: CaptureItemKind.Video })
                Play_Click(s, e); // 동영상 편집(트림)은 플레이어 창에서
        }

        private void Copy_Click(object s, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null) return;
            bool ok = item.Kind == CaptureItemKind.Image
                ? item.PngData != null && ClipboardService.CopyImage(item.PngData)
                : item.VideoPath != null && ClipboardService.CopyFile(item.VideoPath);
            if (!ok)
                MessageBox.Show("클립보드 복사에 실패했습니다.", "OctoCapture");
        }

        private async void Save_Click(object s, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null) return;

            if (item.Kind == CaptureItemKind.Image)
            {
                var img = item.LoadFullImage();
                if (img != null) EditorWindow.SaveImage(img, _controller.Settings);
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

            string ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
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
        }

        private void Delete_Click(object s, RoutedEventArgs e)
        {
            var item = Selected;
            if (item == null) return;
            _controller.History.Remove(item);
            if (item.Kind == CaptureItemKind.Video && item.VideoPath != null)
                try { File.Delete(item.VideoPath); } catch { }
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
