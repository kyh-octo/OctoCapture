using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OctoCapture.Models;
using OctoCapture.Windows;
using ScreenRecorderLib;

namespace OctoCapture.Services
{
    /// <summary>
    /// 화면 녹화 상태 머신: 대기 → 영역 지정(바 표시) → 녹화 중 → 완료.
    /// ScreenRecorderLib(Windows Graphics Capture + Media Foundation H.264) 사용.
    /// </summary>
    public class RecordingCoordinator
    {
        private enum State { Idle, Armed, Recording, Stopping }

        private readonly CaptureController _controller;
        private State _state = State.Idle;

        private Recorder? _recorder;
        private RecordingBarWindow? _bar;
        private RecordingFrameWindow? _frame;
        private RECT _region;
        private BitmapSource? _startThumbnail;
        private readonly Stopwatch _stopwatch = new();
        private readonly DispatcherTimer _timer;

        public static string TempDir => Path.Combine(Path.GetTempPath(), "OctoCapture");

        public RecordingCoordinator(CaptureController controller)
        {
            _controller = controller;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => _bar?.UpdateElapsed(_stopwatch.Elapsed);
        }

        public bool IsRecording => _state == State.Recording;

        /// <summary>단축키/버튼 진입점: 대기→영역지정, 지정됨→시작, 녹화 중→종료</summary>
        public void Toggle()
        {
            switch (_state)
            {
                case State.Idle: BeginRegionSelect(); break;
                case State.Armed: StartRecording(); break;
                case State.Recording: StopRecording(); break;
            }
        }

        private async void BeginRegionSelect()
        {
            _state = State.Armed;
            try
            {
                foreach (Window w in Application.Current.Windows)
                    if (w is MainWindow && w.IsVisible) { w.Hide(); await Task.Delay(180); }

                var frozen = ScreenCaptureService.CaptureFullScreen();
                var selector = new RegionSelectorWindow(frozen, "녹화할 영역을 드래그하세요 (Esc: 취소)");
                selector.ShowDialog();
                if (selector.SelectedRect is not RECT region)
                {
                    _state = State.Idle;
                    CaptureController.ShowMainWindow(); // 취소 후 메인 창 표시
                    return;
                }

                // 녹화 영역은 한 모니터 내로 제한 (영역 중심이 속한 모니터 기준으로 잘라냄)
                RECT mon = ScreenCaptureService.MonitorRectFromPoint(
                    region.Left + region.Width / 2, region.Top + region.Height / 2);
                _region = new RECT
                {
                    Left = Math.Max(region.Left, mon.Left),
                    Top = Math.Max(region.Top, mon.Top),
                    Right = Math.Min(region.Right, mon.Right),
                    Bottom = Math.Min(region.Bottom, mon.Bottom),
                };
                if (_region.Width < 16 || _region.Height < 16)
                {
                    _state = State.Idle;
                    CaptureController.ShowMainWindow();
                    return;
                }

                _frame = new RecordingFrameWindow(_region);
                _frame.Show();

                _bar = new RecordingBarWindow(_region, _controller.Settings.RecordSystemAudio, _controller.Settings.RecordMicrophone);
                _bar.StartRequested += StartRecording;
                _bar.StopRequested += StopRecording;
                _bar.Cancelled += CancelArmed;
                _bar.Show();
            }
            catch (Exception ex)
            {
                CleanupUi();
                _state = State.Idle;
                CaptureController.ShowMainWindow();
                MessageBox.Show($"녹화 준비 중 오류: {ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void CancelArmed()
        {
            CleanupUi();
            _state = State.Idle;
            CaptureController.ShowMainWindow(); // 취소 후 메인 창 표시
        }

        private void StartRecording()
        {
            if (_state != State.Armed || _bar == null) return;
            try
            {
                bool sysAudio = _bar.SystemAudio;
                bool mic = _bar.Microphone;

                Directory.CreateDirectory(TempDir);
                string output = Path.Combine(TempDir, $"rec_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                // 시작 시점 썸네일 (ffmpeg 없이도 목록에 미리보기 제공)
                _startThumbnail = ScreenCaptureService.CaptureRegion(_region);

                RECT mon = ScreenCaptureService.MonitorRectFromPoint(
                    _region.Left + _region.Width / 2, _region.Top + _region.Height / 2);

                var displays = Recorder.GetDisplays();
                var display = FindDisplay(displays, mon) ?? DisplayRecordingSource.MainMonitor;

                var options = new RecorderOptions
                {
                    SourceOptions = new SourceOptions
                    {
                        RecordingSources = new List<RecordingSourceBase> { display },
                    },
                    OutputOptions = new OutputOptions
                    {
                        RecorderMode = RecorderMode.Video,
                        SourceRect = new ScreenRect(
                            _region.Left - mon.Left, _region.Top - mon.Top,
                            _region.Width, _region.Height),
                    },
                    AudioOptions = new AudioOptions
                    {
                        IsAudioEnabled = sysAudio || mic,
                        IsOutputDeviceEnabled = sysAudio,
                        IsInputDeviceEnabled = mic,
                    },
                    VideoEncoderOptions = new VideoEncoderOptions
                    {
                        Encoder = new H264VideoEncoder
                        {
                            BitrateMode = H264BitrateControlMode.Quality,
                            EncoderProfile = H264Profile.Main,
                        },
                        Quality = 70,
                        Framerate = _controller.Settings.RecordFps,
                        IsFixedFramerate = false,
                    },
                    MouseOptions = new MouseOptions { IsMousePointerEnabled = true },
                };

                _recorder = Recorder.CreateRecorder(options);
                _recorder.OnRecordingComplete += Recorder_Complete;
                _recorder.OnRecordingFailed += Recorder_Failed;
                _recorder.Record(output);

                _state = State.Recording;
                _stopwatch.Restart();
                _timer.Start();
                _bar.EnterRecordingState();
            }
            catch (Exception ex)
            {
                DisposeRecorder();
                CleanupUi();
                _state = State.Idle;
                CaptureController.ShowMainWindow();
                MessageBox.Show($"녹화를 시작할 수 없습니다.\n{ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>MONITORINFOEX.szDevice와 RecordableDisplay.DeviceName을 매칭해 녹화 소스를 찾는다.</summary>
        private static RecordingSourceBase? FindDisplay(List<RecordableDisplay> displays, RECT mon)
        {
            IntPtr hMon = NativeMethods.MonitorFromPoint(
                new POINT { X = mon.Left + mon.Width / 2, Y = mon.Top + mon.Height / 2 },
                NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref mi))
            {
                var match = displays.FirstOrDefault(d =>
                    string.Equals(d.DeviceName, mi.szDevice, StringComparison.OrdinalIgnoreCase));
                if (match != null) return new DisplayRecordingSource(match);
            }
            return null;
        }

        private void StopRecording()
        {
            if (_state != State.Recording) return;
            _state = State.Stopping;
            _timer.Stop();
            _stopwatch.Stop();
            _bar?.SetBusy("저장 중…");
            try { _recorder?.Stop(); }
            catch (Exception ex)
            {
                DisposeRecorder();
                CleanupUi();
                _state = State.Idle;
                MessageBox.Show($"녹화 종료 중 오류: {ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Recorder_Complete(object? sender, RecordingCompleteEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                DisposeRecorder();
                CleanupUi();
                _state = State.Idle;

                var item = CaptureItem.FromVideo(
                    e.FilePath, "화면 녹화",
                    _region.Width, _region.Height,
                    _stopwatch.Elapsed, _startThumbnail);
                _startThumbnail = null;
                _controller.AddVideo(item);

                if (_controller.Settings.CopyToClipboardOnCapture)
                    ClipboardService.CopyFile(e.FilePath);

                CaptureController.ShowMainWindow(); // 녹화 완료 후 메인 창에서 결과 확인
            });
        }

        private void Recorder_Failed(object? sender, RecordingFailedEventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                DisposeRecorder();
                CleanupUi();
                _state = State.Idle;
                _startThumbnail = null;
                CaptureController.ShowMainWindow();
                MessageBox.Show($"녹화에 실패했습니다.\n{e.Error}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }

        private void DisposeRecorder()
        {
            if (_recorder != null)
            {
                _recorder.OnRecordingComplete -= Recorder_Complete;
                _recorder.OnRecordingFailed -= Recorder_Failed;
                try { _recorder.Dispose(); } catch { }
                _recorder = null;
            }
        }

        private void CleanupUi()
        {
            _timer.Stop();
            _bar?.Close(); _bar = null;
            _frame?.Close(); _frame = null;
        }

        /// <summary>앱 시작/종료 시 임시 녹화 파일 정리</summary>
        public static void CleanTempFiles()
        {
            try
            {
                if (!Directory.Exists(TempDir)) return;
                foreach (var f in Directory.GetFiles(TempDir))
                    try { File.Delete(f); } catch { /* 사용 중 파일은 무시 */ }
            }
            catch { }
        }
    }
}
