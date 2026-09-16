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
        private enum State { Idle, Armed, Recording, Paused, Stopping }

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

        /// <summary>녹화 진행 중(일시정지 포함)</summary>
        public bool IsRecording => _state is State.Recording or State.Paused;
        public bool IsPaused => _state == State.Paused;

        /// <summary>단축키/버튼 진입점: 대기→영역지정, 지정됨→시작, 녹화 중(일시정지 포함)→종료</summary>
        public void Toggle()
        {
            switch (_state)
            {
                case State.Idle: BeginRegionSelect(); break;
                case State.Armed: StartRecording(); break;
                case State.Recording:
                case State.Paused: StopRecording(); break;
            }
        }

        /// <summary>녹화 일시정지 ↔ 재개 (컨트롤 바 버튼 / 단축키 / 트레이)</summary>
        public void TogglePause()
        {
            if (_recorder == null) return;
            try
            {
                if (_state == State.Recording)
                {
                    _recorder.Pause();
                    _stopwatch.Stop();           // 경과 시간도 멈춤
                    _state = State.Paused;
                    _bar?.SetPaused(true);
                    _frame?.SetPaused(true);
                }
                else if (_state == State.Paused)
                {
                    _recorder.Resume();
                    _stopwatch.Start();
                    _state = State.Recording;
                    _bar?.SetPaused(false);
                    _frame?.SetPaused(false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"일시정지/재개 중 오류: {ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>마지막으로 사용한 영역 지정 방식 (영역 변경 시 이 방식으로 다시 시작)</summary>
        private CaptureMode _areaMode = CaptureMode.Region;

        /// <summary>
        /// 영역 지정 시작. restoreRegion이 있으면 [영역 변경] 재지정 흐름이며,
        /// 취소 시 이전 영역/오디오 설정으로 대기 상태를 복원한다.
        /// </summary>
        private async void BeginRegionSelect(RECT? restoreRegion = null, bool? sysAudio = null, bool? mic = null,
            BitmapSource? frozenOverride = null)
        {
            _state = State.Armed;
            try
            {
                foreach (Window w in Application.Current.Windows)
                    if (w is MainWindow && w.IsVisible) { w.Hide(); await Task.Delay(180); }

                // 영역 지정 방식 선택 루프: 직접 지정 / 창 / 단위 / 전체 화면 (모드 바로 전환 가능)
                // 캡쳐 모드 바의 [화면 녹화]에서 넘어온 경우 같은 프리즈 프레임을 그대로 사용
                var frozen = frozenOverride ?? ScreenCaptureService.CaptureFullScreen();
                CaptureMode mode = _areaMode;
                RECT? picked = null;
                while (picked == null)
                {
                    if (mode == CaptureMode.Region)
                    {
                        var selector = new RegionSelectorWindow(frozen,
                            "녹화할 영역을 드래그하세요 (Esc: 취소)", mode, Windows.CaptureModeBar.RecordingItems);
                        selector.ShowDialog();
                        if (selector.SwitchRequest is CaptureMode next)
                        {
                            if (next == CaptureMode.Capture) { HandOffToCapture(frozen); return; }
                            mode = next; continue;
                        }
                        if (selector.SelectedRect is RECT r) { picked = r; break; }
                    }
                    else
                    {
                        var (pickerMode, hint, targets) = mode switch
                        {
                            CaptureMode.Window => (PickerMode.TopLevelWindow, "녹화할 창을 클릭하세요 (Esc: 취소)", (List<WindowInfo>?)null),
                            CaptureMode.Unit => (PickerMode.UnitControl, "녹화할 영역(컨트롤)을 클릭하세요 (Esc: 취소)", null),
                            _ => (PickerMode.TopLevelWindow, "녹화할 모니터를 클릭하세요 (Esc: 취소)", CaptureController.GetMonitorTargets()),
                        };
                        var picker = new WindowPickerWindow(frozen, pickerMode, hint, mode, targets,
                            Windows.CaptureModeBar.RecordingItems);
                        picker.ShowDialog();
                        if (picker.SwitchRequest is CaptureMode next)
                        {
                            if (next == CaptureMode.Capture) { HandOffToCapture(frozen); return; }
                            mode = next; continue;
                        }
                        if (picker.Selected != null) { picked = picker.Selected.Bounds; break; }
                    }

                    // 취소 (Esc / ✕)
                    if (restoreRegion is RECT prev)
                    {
                        // [영역 변경] 도중 취소 → 이전 대기 상태(영역/오디오 옵션) 복원
                        ArmRegion(prev, frozen, sysAudio, mic);
                        return;
                    }
                    _state = State.Idle;
                    _startThumbnail = null;
                    CaptureController.ShowMainWindow();
                    return;
                }
                _areaMode = mode;

                if (!ArmRegion(picked.Value, frozen, sysAudio, mic))
                {
                    _state = State.Idle;
                    CaptureController.ShowMainWindow();
                }
            }
            catch (Exception ex)
            {
                CleanupUi();
                _state = State.Idle;
                _startThumbnail = null;
                CaptureController.ShowMainWindow();
                MessageBox.Show($"녹화 준비 중 오류: {ex.Message}", "OctoCapture", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 영역을 확정하고 테두리/컨트롤 바를 띄운다 (대기 상태).
        /// 썸네일은 바가 뜨기 전의 프리즈 프레임에서 잘라 쓴다 (바가 찍히지 않음).
        /// </summary>
        private bool ArmRegion(RECT region, BitmapSource frozen, bool? sysAudio, bool? mic)
        {
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
            if (_region.Width < 16 || _region.Height < 16) return false;

            _startThumbnail = CropFrozen(frozen, _region);

            _frame = new RecordingFrameWindow(_region);
            _frame.Show();

            _bar = new RecordingBarWindow(_region,
                sysAudio ?? _controller.Settings.RecordSystemAudio,
                mic ?? _controller.Settings.RecordMicrophone);
            _bar.StartRequested += StartRecording;
            _bar.StopRequested += StopRecording;
            _bar.PauseToggleRequested += TogglePause;
            _bar.Cancelled += CancelArmed;
            _bar.RegionChangeRequested += ChangeRegion;
            _bar.Show();
            _state = State.Armed;
            return true;
        }

        private static BitmapSource? CropFrozen(BitmapSource frozen, RECT region)
        {
            try
            {
                RECT vs = ScreenCaptureService.VirtualScreen;
                int x = Math.Max(0, region.Left - vs.Left), y = Math.Max(0, region.Top - vs.Top);
                int w = Math.Min(region.Width, frozen.PixelWidth - x), h = Math.Min(region.Height, frozen.PixelHeight - y);
                if (w <= 0 || h <= 0) return null;
                var crop = new WriteableBitmap(new CroppedBitmap(frozen, new Int32Rect(x, y, w, h)));
                crop.Freeze();
                return crop;
            }
            catch { return null; }
        }

        private void CancelArmed()
        {
            CleanupUi();
            _state = State.Idle;
            _startThumbnail = null;
            CaptureController.ShowMainWindow(); // 취소 후 메인 창 표시
        }

        /// <summary>
        /// 캡쳐 모드 바의 [화면 녹화]에서 호출: 같은 프리즈 프레임으로 녹화 영역 지정을 시작한다.
        /// 녹화 중이면 거부(false). 대기(Armed) 상태였다면 기존 바/테두리를 닫고 새로 지정한다.
        /// </summary>
        public bool BeginRegionSelectFrom(BitmapSource frozen)
        {
            if (_state is State.Recording or State.Stopping) return false;
            if (_state == State.Armed) CleanupUi();
            BeginRegionSelect(frozenOverride: frozen);
            return true;
        }

        /// <summary>
        /// 녹화 영역 지정 바의 [화면 캡쳐]: 같은 프리즈 프레임으로 캡쳐 흐름에 넘긴다.
        /// 현재 메서드(BeginRegionSelect)가 반환된 뒤 디스패처에서 시작해 중첩을 피한다.
        /// </summary>
        private void HandOffToCapture(BitmapSource frozen)
        {
            _state = State.Idle;
            _startThumbnail = null;
            Application.Current.Dispatcher.BeginInvoke(() => _controller.StartInteractiveWithFrozen(frozen));
        }

        /// <summary>
        /// 녹화 시작 전 [영역 변경]: 바/테두리를 닫고 영역 지정을 다시 시작한다.
        /// 현재 오디오 체크 상태를 유지하고, 재지정을 취소하면 이전 영역으로 복귀한다.
        /// </summary>
        private async void ChangeRegion()
        {
            if (_state != State.Armed || _bar == null) return;
            bool sysAudio = _bar.SystemAudio, mic = _bar.Microphone;
            RECT previous = _region;
            CleanupUi();
            await Task.Delay(180); // 닫힌 바/테두리가 화면에서 사라진 뒤 프리즈 프레임을 찍는다 (잔상 방지)
            BeginRegionSelect(previous, sysAudio, mic);
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

                // 썸네일은 영역 확정 시 프리즈 프레임에서 잘라둠 (없으면 지금 캡쳐)
                _startThumbnail ??= ScreenCaptureService.CaptureRegion(_region);

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
                _startThumbnail = null; // 전체 해상도 비트맵 해제
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
            if (_state is not (State.Recording or State.Paused)) return;
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
                bool copied = _controller.Settings.CopyToClipboardOnCapture
                              && ClipboardService.CopyFile(e.FilePath);
                _controller.AddVideo(item, copied);

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

        /// <summary>앱 시작/종료 시 임시 녹화 파일 정리 (클립보드가 참조 중인 파일은 남긴다)</summary>
        public static void CleanTempFiles()
        {
            try
            {
                if (!Directory.Exists(TempDir)) return;
                foreach (var f in Directory.GetFiles(TempDir))
                    TempFileCleaner.TryDelete(f);
            }
            catch { }
        }
    }
}
