using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OctoCapture.Models;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    /// <summary>
    /// 앱 내 동영상 플레이어 + 앞뒤 트림 편집.
    /// [ 시작 지점 / 끝 지점 ]으로 구간을 지정하고 저장하거나 목록에 새 항목으로 추가한다.
    /// </summary>
    public partial class VideoPlayerWindow : Window
    {
        private readonly CaptureItem _item;
        private readonly CaptureController _controller;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };

        private bool _isPlaying;
        private bool _dragging;
        private bool _closed;
        private bool _trimBusy;
        private TimeSpan _duration = TimeSpan.Zero;
        private TimeSpan? _trimStart, _trimEnd;

        /// <summary>이 플레이어가 열고 있는 캡쳐 항목 (같은 항목의 중복 플레이어 방지용)</summary>
        public CaptureItem Item => _item;

        public VideoPlayerWindow(CaptureItem item, CaptureController controller)
        {
            InitializeComponent();
            _item = item;
            _controller = controller;
            Title = $"OctoCapture - {item.Title}";

            if (item.VideoPath == null || !File.Exists(item.VideoPath))
            {
                MessageBox.Show("동영상 파일을 찾을 수 없습니다.", "OctoCapture");
                Loaded += (_, _) => Close();
                return;
            }

            Player.Source = new Uri(item.VideoPath);
            Player.Volume = VolumeSlider.Value;
            VolumeSlider.ValueChanged += (_, _) => Player.Volume = VolumeSlider.Value;

            // 탐색 슬라이더: 드래그 중에는 타이머 갱신을 멈추고 사용자가 위치를 결정
            SeekSlider.AddHandler(PreviewMouseDownEvent,
                new MouseButtonEventHandler((_, _) => _dragging = true), true);
            SeekSlider.AddHandler(PreviewMouseUpEvent,
                new MouseButtonEventHandler((_, _) =>
                {
                    _dragging = false;
                    Player.Position = TimeSpan.FromSeconds(SeekSlider.Value);
                }), true);
            SeekSlider.ValueChanged += (_, e) =>
            {
                if (_dragging) Player.Position = TimeSpan.FromSeconds(e.NewValue);
                UpdateTimeLabel();
            };

            _timer.Tick += (_, _) =>
            {
                if (_dragging) return;
                SeekSlider.Value = Player.Position.TotalSeconds;
                UpdateTimeLabel();
            };

            SizeChanged += (_, _) => UpdateTrimUi();
            PreviewKeyDown += OnKey;
            Loaded += (_, _) => { Player.Play(); _isPlaying = true; _timer.Start(); };
            Closed += (_, _) =>
            {
                // 파일 핸들 해제 (삭제/이동이 막히지 않도록)
                _closed = true;
                _timer.Stop();
                Player.Stop();
                Player.Close();
                Player.Source = null;
            };
        }

        // ---------- 재생 ----------

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            _duration = Player.NaturalDuration.HasTimeSpan
                ? Player.NaturalDuration.TimeSpan
                : _item.Duration ?? TimeSpan.Zero;
            SeekSlider.Maximum = Math.Max(0.1, _duration.TotalSeconds);
            UpdateTimeLabel();
            UpdateTrimUi();
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            Player.Pause();
            Player.Position = TimeSpan.Zero;
            _isPlaying = false;
            PlayBtn.Content = "▶ 재생";
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

        private void TogglePlay()
        {
            if (_isPlaying) { Player.Pause(); PlayBtn.Content = "▶ 재생"; }
            else { Player.Play(); PlayBtn.Content = "⏸ 일시정지"; }
            _isPlaying = !_isPlaying;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Space:
                    TogglePlay();
                    e.Handled = true;
                    break;
                case Key.Left:
                    Player.Position = Player.Position - TimeSpan.FromSeconds(5) < TimeSpan.Zero
                        ? TimeSpan.Zero : Player.Position - TimeSpan.FromSeconds(5);
                    e.Handled = true;
                    break;
                case Key.Right:
                    Player.Position += TimeSpan.FromSeconds(5);
                    e.Handled = true;
                    break;
            }
        }

        private void UpdateTimeLabel() =>
            TimeLabel.Text = $"{Format(Player.Position)} / {Format(_duration)}";

        private static string Format(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss\.f");

        // ---------- 트림 ----------

        private void SetTrimStart_Click(object sender, RoutedEventArgs e)
        {
            _trimStart = Player.Position;
            if (_trimEnd.HasValue && _trimEnd.Value <= _trimStart.Value) _trimEnd = null;
            UpdateTrimUi();
        }

        private void SetTrimEnd_Click(object sender, RoutedEventArgs e)
        {
            _trimEnd = Player.Position;
            if (_trimStart.HasValue && _trimStart.Value >= _trimEnd.Value) _trimStart = null;
            UpdateTrimUi();
        }

        private void ResetTrim_Click(object sender, RoutedEventArgs e)
        {
            _trimStart = _trimEnd = null;
            UpdateTrimUi();
        }

        private (TimeSpan Start, TimeSpan End) EffectiveRange() =>
            (_trimStart ?? TimeSpan.Zero, _trimEnd ?? _duration);

        private bool HasTrim => _trimStart.HasValue || _trimEnd.HasValue;

        private void UpdateTrimUi()
        {
            var (start, end) = EffectiveRange();
            TrimLabel.Text = HasTrim
                ? $"구간: {Format(start)} ~ {Format(end)} ({(end - start).TotalSeconds:0.0}초)"
                : "구간: 전체";

            // 슬라이더 위에 선택 구간 밴드 표시
            if (_duration.TotalSeconds <= 0 || SeekHost.ActualWidth <= 0)
            {
                TrimBand.Width = 0;
                return;
            }
            double w = SeekHost.ActualWidth;
            double x1 = start.TotalSeconds / _duration.TotalSeconds * w;
            double x2 = end.TotalSeconds / _duration.TotalSeconds * w;
            TrimBand.Margin = new Thickness(Math.Max(0, x1), 0, 0, 0);
            TrimBand.Width = Math.Max(0, Math.Min(x2, w) - x1);
            TrimBand.Visibility = HasTrim ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool ValidateRange(out TimeSpan start, out TimeSpan len)
        {
            var (s, e) = EffectiveRange();
            start = s;
            len = e - s;
            if (len.TotalSeconds < 0.2)
            {
                MessageBox.Show("선택 구간이 너무 짧습니다. 시작/끝 지점을 다시 지정해주세요.",
                    "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        /// <summary>[저장] - 선택 구간만 남기고 현재 캡쳐 항목에 덮어쓴다.</summary>
        private async void TrimOverwrite_Click(object sender, RoutedEventArgs e)
        {
            if (_item.VideoPath == null) return;
            if (!HasTrim)
            {
                MessageBox.Show("잘라낼 구간이 지정되지 않았습니다.\n[ 시작 지점 / 끝 지점 ] 버튼으로 구간을 먼저 지정하세요.",
                    "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ValidateRange(out var start, out var len)) return;
            if (_trimBusy) return; // 변환 중 중복 클릭 방지
            _trimBusy = true;

            try
            {
                string? ffmpeg = await FfmpegService.EnsureFfmpegAsync();
                if (ffmpeg == null) return;

                string input = _item.VideoPath;
                Directory.CreateDirectory(RecordingCoordinator.TempDir);
                string output = System.IO.Path.Combine(RecordingCoordinator.TempDir,
                    $"trim_{_item.Id}_{DateTime.Now:HHmmss_fff}.mp4");

                bool ok = await BusyWindow.RunAsync("구간 잘라내는 중… 잠시만 기다려주세요.",
                    () => FfmpegService.TrimConvertAsync(ffmpeg, input, output,
                        "mp4", start, len, _controller.Settings.GifFps));
                if (!ok || !File.Exists(output))
                {
                    if (!_closed)
                        MessageBox.Show("잘라내기에 실패했습니다.", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_closed)
                {
                    // 변환 중 플레이어가 닫힘: 항목만 갱신하고 닫힌 창의 미디어/타이머는 건드리지 않는다
                    _item.ReplaceVideo(output, len);
                    return;
                }

                // 이전 파일 핸들을 놓아야 교체/삭제가 가능
                _timer.Stop();
                Player.Pause();
                Player.Close();
                Player.Source = null;

                _item.ReplaceVideo(output, len);

                // 새 파일로 다시 재생
                _trimStart = _trimEnd = null;
                Player.Source = new Uri(output);
                Player.Play();
                _isPlaying = true;
                PlayBtn.Content = "⏸ 일시정지";
                _timer.Start();
                UpdateTrimUi();
            }
            catch (Exception ex)
            {
                if (!_closed)
                    MessageBox.Show($"저장에 실패했습니다.\n{ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _trimBusy = false; }
        }

        /// <summary>선택 구간을 잘라 캡쳐 목록에 새 항목으로 추가</summary>
        private async void TrimToList_Click(object sender, RoutedEventArgs e)
        {
            if (_item.VideoPath == null || !ValidateRange(out var start, out var len)) return;

            string? ffmpeg = await FfmpegService.EnsureFfmpegAsync();
            if (ffmpeg == null) return;

            try
            {
                Directory.CreateDirectory(RecordingCoordinator.TempDir);
                string output = System.IO.Path.Combine(RecordingCoordinator.TempDir,
                    $"trim_{DateTime.Now:yyyyMMdd_HHmmss_fff}.mp4");

                bool ok = await BusyWindow.RunAsync("구간 잘라내는 중… 잠시만 기다려주세요.",
                    () => FfmpegService.TrimConvertAsync(ffmpeg, _item.VideoPath, output,
                        "mp4", start, len, _controller.Settings.GifFps));
                if (!ok)
                {
                    MessageBox.Show("잘라내기에 실패했습니다.", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var newItem = CaptureItem.FromVideo(output, $"{_item.Title} (잘라냄)",
                    _item.PixelWidth, _item.PixelHeight, len, _item.Thumbnail);
                _controller.AddVideo(newItem);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"잘라내기에 실패했습니다.\n{ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void External_Click(object sender, RoutedEventArgs e)
        {
            if (_item.VideoPath == null || !File.Exists(_item.VideoPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo(_item.VideoPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"동영상을 열 수 없습니다.\n{ex.Message}", "OctoCapture");
            }
        }
    }
}
