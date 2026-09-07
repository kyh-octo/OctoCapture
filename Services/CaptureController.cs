using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using OctoCapture.Models;
using OctoCapture.Windows;

namespace OctoCapture.Services
{
    /// <summary>
    /// 캡쳐 방식. 오버레이의 모드 바에서 전환할 수 있다.
    /// Record / Capture 는 실제 캡쳐 방식이 아니라 "녹화로 전환" / "캡쳐로 전환" 신호용 가상 모드.
    /// </summary>
    public enum CaptureMode { Region, Window, Unit, Monitor, Full, Scroll, Record, Capture }

    /// <summary>
    /// 모든 캡쳐 모드의 진입점. 단축키/트레이/메인 창에서 호출된다.
    /// 캡쳐 오버레이 상단에 모드 바가 표시되어 방식 전환/취소가 가능하며,
    /// 전환해도 프리즈 프레임(정지 화면)은 유지된다. 모드 바의 [화면 녹화]로 녹화 흐름에 넘길 수 있다.
    /// </summary>
    public class CaptureController
    {
        public ObservableCollection<CaptureItem> History { get; } = new();
        public AppSettings Settings { get; }

        /// <summary>녹화 코디네이터 (캡쳐 ↔ 녹화 전환용, App에서 주입)</summary>
        public RecordingCoordinator? Recording { get; set; }

        /// <summary>캡쳐/추가 완료 알림 (item, 클립보드 복사 여부)</summary>
        public event Action<CaptureItem, bool>? ItemCaptured;

        private bool _busy;
        private BitmapSource? _handoffFrozen; // [화면 녹화]로 넘길 프리즈 프레임 (현재 흐름이 끝난 뒤 녹화 시작)

        public CaptureController(AppSettings settings)
        {
            Settings = settings;
        }

        // ---------- 캡쳐 진입점 ----------

        /// <summary>직접(영역) 캡쳐</summary>
        public void CaptureRegion() => _ = RunCaptureAsync(() => InteractiveCaptureAsync(CaptureMode.Region));

        /// <summary>창 캡쳐</summary>
        public void CaptureWindow() => _ = RunCaptureAsync(() => InteractiveCaptureAsync(CaptureMode.Window));

        /// <summary>단위별(컨트롤) 캡쳐</summary>
        public void CaptureUnit() => _ = RunCaptureAsync(() => InteractiveCaptureAsync(CaptureMode.Unit));

        /// <summary>화면(모니터) 캡쳐 - 모니터를 클릭해서 선택</summary>
        public void CaptureMonitor() => _ = RunCaptureAsync(() => InteractiveCaptureAsync(CaptureMode.Monitor));

        /// <summary>스크롤 캡쳐</summary>
        public void CaptureScroll() => _ = RunCaptureAsync(() => InteractiveCaptureAsync(CaptureMode.Scroll));

        /// <summary>전체(모든 모니터) 캡쳐 - 즉시 완료</summary>
        public void CaptureFull() => _ = RunCaptureAsync(() =>
        {
            AddImage(ScreenCaptureService.CaptureFullScreen(), "전체 캡쳐");
            return Task.CompletedTask;
        });

        /// <summary>녹화 영역 지정 바의 [화면 캡쳐]에서 넘어온 경우: 같은 프리즈 프레임으로 캡쳐 시작</summary>
        public void StartInteractiveWithFrozen(BitmapSource frozen) =>
            _ = RunCaptureAsync(() => InteractiveCaptureAsync(CaptureMode.Region, frozen));

        // ---------- 대화형 캡쳐 루프 ----------

        /// <summary>
        /// 프리즈 프레임 위에서 캡쳐를 진행한다. 모드 바로 방식을 전환하면
        /// 같은 프리즈 프레임을 유지한 채 해당 방식의 오버레이로 교체된다.
        /// [화면 녹화]를 누르면 녹화 코디네이터에 프리즈 프레임을 넘기고 종료한다.
        /// </summary>
        private async Task InteractiveCaptureAsync(CaptureMode mode, BitmapSource? frozenOverride = null)
        {
            var frozen = frozenOverride ?? ScreenCaptureService.CaptureFullScreen();

            while (true)
            {
                switch (mode)
                {
                    case CaptureMode.Region:
                    {
                        var win = new RegionSelectorWindow(frozen,
                            "드래그하여 영역을 선택하세요 (Esc: 취소)", mode);
                        win.ShowDialog();
                        if (win.SwitchRequest is CaptureMode next)
                        {
                            if (next == CaptureMode.Record) { HandOffToRecording(frozen); return; }
                            mode = next; continue;
                        }
                        var img = win.GetCroppedImage();
                        if (img != null) AddImage(img, "직접 캡쳐");
                        return;
                    }

                    case CaptureMode.Window:
                    {
                        var win = new WindowPickerWindow(frozen, PickerMode.TopLevelWindow,
                            "캡쳐할 창을 클릭하세요 (Esc: 취소)", mode);
                        win.ShowDialog();
                        if (win.SwitchRequest is CaptureMode next)
                        {
                            if (next == CaptureMode.Record) { HandOffToRecording(frozen); return; }
                            mode = next; continue;
                        }
                        var img = win.GetCroppedImage();
                        if (img != null)
                        {
                            string title = string.IsNullOrEmpty(win.Selected?.Title)
                                ? "창 캡쳐" : $"창 캡쳐 - {win.Selected!.Title}";
                            AddImage(img, title);
                        }
                        return;
                    }

                    case CaptureMode.Unit:
                    {
                        var win = new WindowPickerWindow(frozen, PickerMode.UnitControl,
                            "캡쳐할 영역(컨트롤)을 클릭하세요 (Esc: 취소)", mode);
                        win.ShowDialog();
                        if (win.SwitchRequest is CaptureMode next)
                        {
                            if (next == CaptureMode.Record) { HandOffToRecording(frozen); return; }
                            mode = next; continue;
                        }
                        var img = win.GetCroppedImage();
                        if (img != null) AddImage(img, "단위별 캡쳐");
                        return;
                    }

                    case CaptureMode.Monitor:
                    {
                        var win = new WindowPickerWindow(frozen, PickerMode.TopLevelWindow,
                            "캡쳐할 모니터를 클릭하세요 (Esc: 취소)", mode, GetMonitorTargets());
                        win.ShowDialog();
                        if (win.SwitchRequest is CaptureMode next)
                        {
                            if (next == CaptureMode.Record) { HandOffToRecording(frozen); return; }
                            mode = next; continue;
                        }
                        var img = win.GetCroppedImage();
                        if (img != null) AddImage(img, $"화면 캡쳐 - {win.Selected?.Title}");
                        return;
                    }

                    case CaptureMode.Full:
                    {
                        // 프리즈 프레임 전체가 곧 결과
                        AddImage(frozen, "전체 캡쳐");
                        return;
                    }

                    case CaptureMode.Scroll:
                    {
                        var win = new WindowPickerWindow(frozen, PickerMode.TopLevelWindow,
                            "스크롤 캡쳐할 창을 클릭하세요 - 자동으로 스크롤하며 이어붙입니다 (Esc: 취소)", mode);
                        win.ShowDialog();
                        if (win.SwitchRequest is CaptureMode next)
                        {
                            if (next == CaptureMode.Record) { HandOffToRecording(frozen); return; }
                            mode = next; continue;
                        }
                        if (win.Selected != null)
                            await ScrollCaptureAsync(win.Selected);
                        return;
                    }

                    default:
                        // 가상 모드(Record/Capture)가 직접 들어오는 일은 없음
                        mode = CaptureMode.Region;
                        continue;
                }
            }
        }

        /// <summary>
        /// 모드 바 [화면 녹화]: 프리즈 프레임을 기억해 두고 캡쳐 루프를 끝낸다.
        /// 실제 녹화 영역 지정은 RunCaptureAsync가 완전히 끝난 뒤(busy 해제 후) 시작되므로
        /// 녹화 → 캡쳐로 다시 전환해도 중첩/차단 없이 동작한다.
        /// </summary>
        private void HandOffToRecording(BitmapSource frozen) => _handoffFrozen = frozen;

        /// <summary>모니터 목록을 픽커 대상으로 변환 (녹화 영역 지정에서도 사용)</summary>
        public static List<WindowInfo> GetMonitorTargets()
        {
            var list = new List<WindowInfo>();
            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                list.Add(new WindowInfo
                {
                    Bounds = new RECT { Left = b.Left, Top = b.Top, Right = b.Right, Bottom = b.Bottom },
                    Title = $"모니터 {i + 1} ({b.Width}×{b.Height})",
                });
            }
            return list;
        }

        /// <summary>선택된 창을 자동 스크롤하며 이어붙여 캡쳐</summary>
        private async Task ScrollCaptureAsync(WindowInfo target)
        {
            IntPtr hWnd = target.Handle;
            NativeMethods.GetClientRect(hWnd, out RECT client);
            var origin = new POINT { X = 0, Y = 0 };
            NativeMethods.ClientToScreen(hWnd, ref origin);
            var rect = new RECT
            {
                Left = origin.X,
                Top = origin.Y,
                Right = origin.X + client.Width,
                Bottom = origin.Y + client.Height,
            };
            if (rect.Width <= 0 || rect.Height <= 0) return;

            var progress = new ScrollProgressWindow();
            progress.Show();
            try
            {
                var img = await ScrollCaptureService.CaptureAsync(
                    hWnd, rect, () => progress.StopRequested, progress.CancelToken);
                if (img != null && !progress.Cancelled)
                    AddImage(img, $"스크롤 캡쳐 - {target.Title}");
            }
            finally { progress.Close(); }
        }

        // ---------- 공통 ----------

        private async Task RunCaptureAsync(Func<Task> action)
        {
            if (_busy) return;
            _busy = true;
            _handoffFrozen = null;
            bool wasHidden = false;
            try
            {
                // 캡쳐에 우리 창이 찍히지 않도록 잠시 숨김
                foreach (Window w in Application.Current.Windows)
                    if (w is MainWindow && w.IsVisible) { w.Hide(); wasHidden = true; }
                if (wasHidden) await Task.Delay(180);

                await action();
            }
            catch (OperationCanceledException) { /* 사용자가 취소 */ }
            catch (Exception ex) { ShowError(ex); }
            finally
            {
                _busy = false;
                var handoff = _handoffFrozen;
                _handoffFrozen = null;
                if (handoff != null)
                {
                    // 호출 스택이 완전히 풀린 뒤 녹화 영역 지정 시작 (거부되면 메인 창 표시)
                    var frozen = handoff;
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                    {
                        if (!(Recording?.BeginRegionSelectFrom(frozen) ?? false))
                            ShowMainWindow();
                    });
                }
                else
                {
                    // 캡쳐 완료/취소 후에는 메인 창을 보여준다
                    ShowMainWindow();
                }
            }
        }

        /// <summary>메인 창을 표시/활성화.</summary>
        public static void ShowMainWindow()
        {
            if (Application.Current.MainWindow is not MainWindow mw) return;
            mw.ShowFromTray();
        }

        /// <summary>이미지를 히스토리에 추가. copyToClipboard=false면 클립보드를 건드리지 않는다(편집 저장 등).</summary>
        public void AddImage(BitmapSource image, string title, bool copyToClipboard = true)
        {
            var item = CaptureItem.FromImage(image, title);
            History.Insert(0, item);
            bool copied = copyToClipboard && Settings.CopyToClipboardOnCapture
                          && ClipboardService.CopyImage(image);
            ItemCaptured?.Invoke(item, copied);
            ScheduleMemoryTrim();
        }

        public void AddVideo(CaptureItem item, bool copiedToClipboard = false)
        {
            History.Insert(0, item);
            ItemCaptured?.Invoke(item, copiedToClipboard);
            ScheduleMemoryTrim();
        }

        /// <summary>
        /// 캡쳐 직후 대용량 화면 버퍼(LOH)가 빨리 회수되도록 잠시 후 GC를 유도한다.
        /// 히스토리에는 PNG 바이트와 작은 썸네일만 남으므로 상주 메모리가 낮게 유지된다.
        /// </summary>
        private static void ScheduleMemoryTrim()
        {
            _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Optimized);
            }, TaskScheduler.Default);
        }

        private static void ShowError(Exception ex)
        {
            MessageBox.Show($"캡쳐 중 오류가 발생했습니다.\n{ex.Message}", "OctoCapture",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
