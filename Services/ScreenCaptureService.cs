using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OctoCapture.Services
{
    /// <summary>
    /// GDI BitBlt 기반 화면 캡쳐. 모든 좌표는 물리 픽셀(가상 화면 좌표계) 기준.
    /// GDI 핸들은 반드시 해제하여 누수를 방지한다.
    /// </summary>
    public static class ScreenCaptureService
    {
        /// <summary>가상 화면 전체 영역(물리 픽셀)</summary>
        public static RECT VirtualScreen
        {
            get
            {
                int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
                int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
                int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
                int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
                return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            }
        }

        /// <summary>커서가 위치한 모니터 영역(물리 픽셀)</summary>
        public static RECT CurrentMonitorRect
        {
            get
            {
                NativeMethods.GetCursorPos(out POINT pt);
                return MonitorRectFromPoint(pt.X, pt.Y);
            }
        }

        public static RECT MonitorRectFromPoint(int x, int y)
        {
            IntPtr hMon = NativeMethods.MonitorFromPoint(new POINT { X = x, Y = y }, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX_Wrapper();
            if (info.Get(hMon, out RECT rect)) return rect;
            return VirtualScreen;
        }

        private struct MONITORINFOEX_Wrapper
        {
            public bool Get(IntPtr hMon, out RECT rect)
            {
                var mi = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    rect = mi.rcMonitor;
                    return true;
                }
                rect = default;
                return false;
            }
        }

        /// <summary>지정 영역(물리 픽셀, 가상 화면 좌표)을 캡쳐한다.</summary>
        public static BitmapSource CaptureRegion(int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("캡쳐 영역 크기가 잘못되었습니다.");

            IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memDC = IntPtr.Zero, hBitmap = IntPtr.Zero, oldObj = IntPtr.Zero;
            try
            {
                memDC = NativeMethods.CreateCompatibleDC(screenDC);
                hBitmap = NativeMethods.CreateCompatibleBitmap(screenDC, width, height);
                oldObj = NativeMethods.SelectObject(memDC, hBitmap);
                NativeMethods.BitBlt(memDC, 0, 0, width, height, screenDC, x, y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                // HBitmap과의 연결을 끊고 독립적인 관리 메모리 비트맵으로 복사
                var result = new WriteableBitmap(source);
                result.Freeze();
                return result;
            }
            finally
            {
                if (oldObj != IntPtr.Zero) NativeMethods.SelectObject(memDC, oldObj);
                if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
                if (memDC != IntPtr.Zero) NativeMethods.DeleteDC(memDC);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
            }
        }

        public static BitmapSource CaptureRegion(RECT rect) =>
            CaptureRegion(rect.Left, rect.Top, rect.Width, rect.Height);

        /// <summary>가상 화면 전체 캡쳐</summary>
        public static BitmapSource CaptureFullScreen() => CaptureRegion(VirtualScreen);

        /// <summary>커서가 있는 모니터 캡쳐</summary>
        public static BitmapSource CaptureCurrentMonitor() => CaptureRegion(CurrentMonitorRect);

        /// <summary>창 캡쳐(DWM 확장 프레임 경계 기준, 화면에서 잘라냄)</summary>
        public static BitmapSource CaptureWindow(IntPtr hWnd)
        {
            RECT rect = NativeMethods.GetExtendedFrameBounds(hWnd);
            // 화면 밖으로 나간 부분은 잘라냄
            RECT vs = VirtualScreen;
            int l = Math.Max(rect.Left, vs.Left);
            int t = Math.Max(rect.Top, vs.Top);
            int r = Math.Min(rect.Right, vs.Right);
            int b = Math.Min(rect.Bottom, vs.Bottom);
            return CaptureRegion(l, t, r - l, b - t);
        }
    }
}
