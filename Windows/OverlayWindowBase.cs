using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OctoCapture.Services;

namespace OctoCapture.Windows
{
    /// <summary>
    /// 가상 화면 전체를 덮는 프리즈 프레임 오버레이의 공통 기반.
    /// 물리 픽셀(가상 화면 좌표) ↔ 창 내부 DIP 좌표 변환을 제공한다.
    /// </summary>
    public abstract class OverlayWindowBase : Window
    {
        protected RECT VirtualScreenRect;

        protected OverlayWindowBase()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = System.Windows.Input.Cursors.Cross;
            VirtualScreenRect = ScreenCaptureService.VirtualScreen;
            // 대략적인 초기 위치(SourceInitialized에서 물리 픽셀로 정확히 배치)
            Left = 0; Top = 0; Width = 100; Height = 100;
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                    VirtualScreenRect.Left, VirtualScreenRect.Top,
                    VirtualScreenRect.Width, VirtualScreenRect.Height,
                    NativeMethods.SWP_SHOWWINDOW);
            };
        }

        /// <summary>현재 커서 위치(물리 픽셀, 가상 화면 좌표)</summary>
        protected static (int X, int Y) CursorPhysical()
        {
            NativeMethods.GetCursorPos(out POINT pt);
            return (pt.X, pt.Y);
        }

        /// <summary>물리 픽셀(가상 화면 좌표) → 이 창의 DIP 좌표</summary>
        protected Point PhysicalToDip(double px, double py)
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return new Point(px - VirtualScreenRect.Left, py - VirtualScreenRect.Top);
            // 창이 가상 화면을 물리 픽셀로 정확히 덮으므로, 창 픽셀 크기 대비 DIP 크기 비율로 변환
            double scaleX = ActualWidth / VirtualScreenRect.Width;
            double scaleY = ActualHeight / VirtualScreenRect.Height;
            return new Point((px - VirtualScreenRect.Left) * scaleX, (py - VirtualScreenRect.Top) * scaleY);
        }

        protected Rect PhysicalToDip(RECT r)
        {
            var p1 = PhysicalToDip(r.Left, r.Top);
            var p2 = PhysicalToDip(r.Right, r.Bottom);
            return new Rect(p1, p2);
        }

        /// <summary>커서가 있는 모니터 영역(이 창의 DIP 좌표)</summary>
        protected Rect CursorMonitorDip()
        {
            var (x, y) = CursorPhysical();
            RECT mon = ScreenCaptureService.MonitorRectFromPoint(x, y);
            return PhysicalToDip(mon);
        }

        /// <summary>
        /// 요소를 커서가 있는 모니터의 상단 중앙에 배치한다 (Canvas 자식이어야 함).
        /// 가상 화면 전체 기준으로 배치하면 모니터 경계에 걸칠 수 있어 모니터 단위로 배치한다.
        /// </summary>
        protected void PlaceTopCenterOnCursorMonitor(FrameworkElement el, double topOffset)
        {
            Rect mon = CursorMonitorDip();
            el.UpdateLayout();
            double left = mon.X + (mon.Width - el.ActualWidth) / 2;
            System.Windows.Controls.Canvas.SetLeft(el, Math.Max(mon.X, left));
            System.Windows.Controls.Canvas.SetTop(el, mon.Y + topOffset);
        }
    }
}
