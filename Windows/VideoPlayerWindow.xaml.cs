using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OctoCapture.Models;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    /// <summary>
    /// 앱 내 동영상 플레이어 + 구간 편집.
    /// [ 시작 지점 / 끝 지점 ]으로 구간을 지정한 뒤
    ///  - "선택 구간 남기기": 구간만 남김 (앞뒤 자르기)
    ///  - "선택 구간 제거": 구간을 잘라내고 앞뒤를 이어붙임
    /// [저장]=현재 캡쳐에 덮어쓰기, [새 캡쳐 저장]=목록에 새 항목으로 추가.
    /// </summary>
    public partial class VideoPlayerWindow : Window
    {
        private static readonly Brush KeepBandBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x3F, 0xA9, 0xF5));
        private static readonly Brush RemoveBandBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0x3E, 0x3E));
        private static readonly Brush KeepHandleBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xF5));
        private static readonly Brush RemoveHandleBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x3E, 0x3E));
        private const double HandleWidth = 16, HandleHeight = 26;
        private static readonly TimeSpan MinRange = TimeSpan.FromSeconds(0.2);

        private Border? _handleDragging; // 드래그 중인 [ ] 핸들

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

        /// <summary>"선택 구간 제거" 모드인가 (아니면 "선택 구간 남기기")</summary>
        private bool RemoveMode => RadioRemove?.IsChecked == true;

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
                if (_dragging || _handleDragging != null) return;
                SeekSlider.Value = Player.Position.TotalSeconds;
                UpdateTimeLabel();
            };

            SetupHandle(StartHandle, isStart: true);
            SetupHandle(EndHandle, isStart: false);

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

        // ---------- 구간 지정 ----------

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

        private void RangeMode_Changed(object sender, RoutedEventArgs e) => UpdateTrimUi();

        // ---------- [ ] 핸들 드래그 ----------

        private void SetupHandle(System.Windows.Controls.Border handle, bool isStart)
        {
            handle.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                _handleDragging = handle;
                handle.CaptureMouse();
                if (_isPlaying) { Player.Pause(); _isPlaying = false; PlayBtn.Content = "▶ 재생"; } // 정밀 조절을 위해 일시정지
                DragLabel.Visibility = Visibility.Visible;
                UpdateHandleFromMouse(handle, isStart, e.GetPosition(SeekHost).X);
            };
            handle.MouseMove += (_, e) =>
            {
                if (_handleDragging != handle) return;
                UpdateHandleFromMouse(handle, isStart, e.GetPosition(SeekHost).X);
            };
            handle.MouseLeftButtonUp += (_, e) =>
            {
                if (_handleDragging != handle) return;
                e.Handled = true;
                EndHandleDrag(handle);
            };
            handle.LostMouseCapture += (_, _) => { if (_handleDragging == handle) EndHandleDrag(handle); };
        }

        private void EndHandleDrag(System.Windows.Controls.Border handle)
        {
            _handleDragging = null;
            if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
            DragLabel.Visibility = Visibility.Collapsed;
        }

        /// <summary>마우스 X 좌표를 시간으로 바꿔 시작/끝 지점을 갱신하고 해당 프레임을 미리보기한다.</summary>
        private void UpdateHandleFromMouse(System.Windows.Controls.Border handle, bool isStart, double x)
        {
            if (_duration.TotalSeconds <= 0 || SeekHost.ActualWidth <= 0) return;
            double frac = Math.Clamp(x / SeekHost.ActualWidth, 0, 1);
            var t = TimeSpan.FromSeconds(frac * _duration.TotalSeconds);

            if (isStart)
            {
                var end = _trimEnd ?? _duration;
                if (t > end - MinRange) t = end - MinRange;
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
                _trimStart = t;
            }
            else
            {
                var start = _trimStart ?? TimeSpan.Zero;
                if (t < start + MinRange) t = start + MinRange;
                if (t > _duration) t = _duration;
                _trimEnd = t;
            }

            Player.Position = t;                 // 해당 지점 프레임 미리보기 (ScrubbingEnabled)
            SeekSlider.Value = t.TotalSeconds;
            UpdateTimeLabel();
            UpdateTrimUi();

            // 핸들 위에 시간 라벨 표시
            DragLabelText.Text = Format(t);
            DragLabel.UpdateLayout();
            double lx = Canvas.GetLeft(handle) + HandleWidth / 2 - DragLabel.ActualWidth / 2;
            Canvas.SetLeft(DragLabel, Math.Clamp(lx, 0, Math.Max(0, SeekHost.ActualWidth - DragLabel.ActualWidth)));
            Canvas.SetTop(DragLabel, -22);
        }

        private (TimeSpan Start, TimeSpan End) EffectiveRange() =>
            (_trimStart ?? TimeSpan.Zero, _trimEnd ?? _duration);

        private bool HasTrim => _trimStart.HasValue || _trimEnd.HasValue;

        private void UpdateTrimUi()
        {
            if (TrimLabel == null || TrimBand == null) return; // 초기화 전
            var (start, end) = EffectiveRange();
            string prefix = RemoveMode ? "제거 구간" : "남길 구간";
            TrimLabel.Text = HasTrim
                ? $"{prefix}: {Format(start)} ~ {Format(end)} ({(end - start).TotalSeconds:0.0}초)"
                : $"{prefix}: (미지정)";
            TrimLabel.Foreground = RemoveMode
                ? new SolidColorBrush(Color.FromRgb(0xF0, 0x6A, 0x6A))
                : new SolidColorBrush(Color.FromRgb(0x3F, 0xA9, 0xF5));
            TrimBand.Background = RemoveMode ? RemoveBandBrush : KeepBandBrush;
            var handleBrush = RemoveMode ? RemoveHandleBrush : KeepHandleBrush;
            StartHandle.Background = handleBrush;
            EndHandle.Background = handleBrush;
            // 구간 미지정 시엔 양 끝에 반투명으로 표시 (드래그로 바로 지정 가능)
            StartHandle.Opacity = EndHandle.Opacity = HasTrim ? 1.0 : 0.55;

            // 슬라이더 위에 선택 구간 밴드 + [ ] 핸들 표시
            if (_duration.TotalSeconds <= 0 || SeekHost.ActualWidth <= 0)
            {
                TrimBand.Width = 0;
                StartHandle.Visibility = EndHandle.Visibility = Visibility.Collapsed;
                return;
            }
            double w = SeekHost.ActualWidth;
            double x1 = start.TotalSeconds / _duration.TotalSeconds * w;
            double x2 = end.TotalSeconds / _duration.TotalSeconds * w;
            TrimBand.Margin = new Thickness(Math.Max(0, x1), 0, 0, 0);
            TrimBand.Width = Math.Max(0, Math.Min(x2, w) - x1);
            TrimBand.Visibility = HasTrim ? Visibility.Visible : Visibility.Collapsed;

            double top = (SeekHost.ActualHeight - HandleHeight) / 2;
            Canvas.SetLeft(StartHandle, Math.Clamp(x1 - HandleWidth / 2, 0, Math.Max(0, w - HandleWidth)));
            Canvas.SetLeft(EndHandle, Math.Clamp(x2 - HandleWidth / 2, 0, Math.Max(0, w - HandleWidth)));
            Canvas.SetTop(StartHandle, top);
            Canvas.SetTop(EndHandle, top);
            StartHandle.Visibility = EndHandle.Visibility = Visibility.Visible;
        }

        /// <summary>구간 유효성 검사. 결과 영상의 길이(newLength)도 계산한다.</summary>
        private bool ValidateRange(out TimeSpan start, out TimeSpan end, out TimeSpan newLength)
        {
            var (s, e) = EffectiveRange();
            start = s; end = e;
            TimeSpan len = e - s;
            newLength = RemoveMode ? _duration - len : len;

            if (!HasTrim)
            {
                MessageBox.Show(RemoveMode
                        ? "제거할 구간이 지정되지 않았습니다.\n[ 시작 지점 / 끝 지점 ] 버튼으로 구간을 먼저 지정하세요."
                        : "잘라낼 구간이 지정되지 않았습니다.\n[ 시작 지점 / 끝 지점 ] 버튼으로 구간을 먼저 지정하세요.",
                    "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (len.TotalSeconds < 0.2)
            {
                MessageBox.Show("선택 구간이 너무 짧습니다. 시작/끝 지점을 다시 지정해주세요.",
                    "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            if (RemoveMode && newLength.TotalSeconds < 0.2)
            {
                MessageBox.Show("구간을 제거하면 남는 영상이 없습니다. 제거 구간을 줄여주세요.",
                    "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }
            return true;
        }

        /// <summary>현재 모드에 따라 구간 남기기/제거 결과를 output에 생성</summary>
        private Task<bool> ProcessRangeAsync(string ffmpeg, string input, string output, TimeSpan start, TimeSpan end)
        {
            return RemoveMode
                ? FfmpegService.RemoveRangeAsync(ffmpeg, input, output, start, end, _duration)
                : FfmpegService.TrimConvertAsync(ffmpeg, input, output, "mp4", start, end - start, _controller.Settings.GifFps);
        }

        private string BusyText => RemoveMode ? "구간 제거 중… 잠시만 기다려주세요." : "구간 잘라내는 중… 잠시만 기다려주세요.";
        private string FailText => RemoveMode ? "구간 제거에 실패했습니다." : "잘라내기에 실패했습니다.";

        /// <summary>[저장] - 구간 남기기/제거 결과로 현재 캡쳐 항목을 덮어쓴다.</summary>
        private async void TrimOverwrite_Click(object sender, RoutedEventArgs e)
        {
            if (_item.VideoPath == null || _trimBusy) return;
            if (!ValidateRange(out var start, out var end, out var newLength)) return;
            _trimBusy = true;

            try
            {
                string? ffmpeg = await FfmpegService.EnsureFfmpegAsync();
                if (ffmpeg == null) return;

                string input = _item.VideoPath;
                Directory.CreateDirectory(RecordingCoordinator.TempDir);
                string output = System.IO.Path.Combine(RecordingCoordinator.TempDir,
                    $"trim_{_item.Id}_{DateTime.Now:HHmmss_fff}.mp4");

                bool ok = await BusyWindow.RunAsync(BusyText, () => ProcessRangeAsync(ffmpeg, input, output, start, end));
                if (!ok || !File.Exists(output))
                {
                    if (!_closed)
                        MessageBox.Show(FailText, "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (_closed)
                {
                    // 변환 중 플레이어가 닫힘: 항목만 갱신하고 닫힌 창의 미디어/타이머는 건드리지 않는다
                    _item.ReplaceVideo(output, newLength);
                    return;
                }

                // 이전 파일 핸들을 놓아야 교체/삭제가 가능
                _timer.Stop();
                Player.Pause();
                Player.Close();
                Player.Source = null;

                _item.ReplaceVideo(output, newLength);

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

        /// <summary>[새 캡쳐 저장] - 구간 남기기/제거 결과를 캡쳐 목록에 새 항목으로 추가</summary>
        private async void TrimToList_Click(object sender, RoutedEventArgs e)
        {
            if (_item.VideoPath == null || _trimBusy) return;
            if (!ValidateRange(out var start, out var end, out var newLength)) return;
            _trimBusy = true;

            try
            {
                string? ffmpeg = await FfmpegService.EnsureFfmpegAsync();
                if (ffmpeg == null) return;

                string input = _item.VideoPath;
                Directory.CreateDirectory(RecordingCoordinator.TempDir);
                string output = System.IO.Path.Combine(RecordingCoordinator.TempDir,
                    $"trim_{_item.Id}_{DateTime.Now:HHmmss_fff}.mp4");

                bool ok = await BusyWindow.RunAsync(BusyText, () => ProcessRangeAsync(ffmpeg, input, output, start, end));
                if (!ok || !File.Exists(output))
                {
                    if (!_closed)
                        MessageBox.Show(FailText, "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string suffix = RemoveMode ? " (구간 제거)" : " (잘라냄)";
                var newItem = CaptureItem.FromVideo(output, $"{_item.Title}{suffix}",
                    _item.PixelWidth, _item.PixelHeight, newLength, _item.Thumbnail);
                _controller.AddVideo(newItem);
            }
            catch (Exception ex)
            {
                if (!_closed)
                    MessageBox.Show($"저장에 실패했습니다.\n{ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _trimBusy = false; }
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
