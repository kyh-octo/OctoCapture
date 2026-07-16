namespace OctoCapture.Services
{
    public class WindowInfo
    {
        public IntPtr Handle;
        public RECT Bounds;   // 물리 픽셀, 가상 화면 좌표
        public string Title = "";
        public bool IsChild;
    }

    /// <summary>창 캡쳐/단위 캡쳐용 창 목록 수집기. Z순서(위→아래)로 반환한다.</summary>
    public static class WindowEnumerator
    {
        /// <summary>보이는 최상위 창 목록 (Z순서). excludeHandles: 오버레이 등 제외할 창.</summary>
        public static List<WindowInfo> GetTopLevelWindows(HashSet<IntPtr>? excludeHandles = null)
        {
            var result = new List<WindowInfo>();
            uint myPid = (uint)Environment.ProcessId;
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (excludeHandles != null && excludeHandles.Contains(hWnd)) return true;
                // 우리 자신(오버레이/모드 바 등)은 캡쳐 대상에서 제외
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == myPid) return true;
                if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.IsIconic(hWnd)) return true;
                if (NativeMethods.IsWindowCloaked(hWnd)) return true;

                int exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

                RECT rect = NativeMethods.GetExtendedFrameBounds(hWnd);
                if (rect.Width <= 0 || rect.Height <= 0) return true;

                result.Add(new WindowInfo
                {
                    Handle = hWnd,
                    Bounds = rect,
                    Title = NativeMethods.GetWindowTitle(hWnd),
                });
                return true;
            }, IntPtr.Zero);
            return result; // EnumWindows는 Z순서 위→아래로 열거함
        }

        /// <summary>단위 캡쳐용: 최상위 창 + 모든 자식 컨트롤 사각형 목록.</summary>
        public static List<WindowInfo> GetWindowsWithChildren(HashSet<IntPtr>? excludeHandles = null)
        {
            var tops = GetTopLevelWindows(excludeHandles);
            var result = new List<WindowInfo>();
            foreach (var top in tops)
            {
                var children = new List<WindowInfo>();
                NativeMethods.EnumChildWindows(top.Handle, (hChild, _) =>
                {
                    if (!NativeMethods.IsWindowVisible(hChild)) return true;
                    if (!NativeMethods.GetWindowRect(hChild, out RECT r)) return true;
                    if (r.Width <= 4 || r.Height <= 4) return true;
                    children.Add(new WindowInfo { Handle = hChild, Bounds = r, IsChild = true, Title = top.Title });
                    return true;
                }, IntPtr.Zero);
                // 자식(더 안쪽)이 먼저 히트되도록 자식 → 부모 순서로 추가
                result.AddRange(children);
                result.Add(top);
            }
            return result;
        }

        /// <summary>주어진 점을 포함하는 첫 번째(=최상위) 창을 찾는다.</summary>
        public static WindowInfo? HitTest(List<WindowInfo> windows, int x, int y)
        {
            foreach (var w in windows)
                if (w.Bounds.Contains(x, y)) return w;
            return null;
        }

        /// <summary>
        /// 단위 캡쳐 히트테스트: 점을 포함하는 최상위 창을 먼저 찾고,
        /// 그 창(및 자식들) 중 점을 포함하는 가장 작은 사각형을 반환한다.
        /// </summary>
        public static WindowInfo? HitTestSmallest(List<WindowInfo> windows, int x, int y)
        {
            // 첫 번째로 히트되는 최상위 창 그룹 탐색
            WindowInfo? bestTop = null;
            foreach (var w in windows)
            {
                if (!w.IsChild && w.Bounds.Contains(x, y)) { bestTop = w; break; }
                if (w.IsChild && w.Bounds.Contains(x, y) && bestTop == null)
                {
                    // 자식이 먼저 나오는 구조이므로 자식이 속한 그룹의 최상위 창을 이후에 만나게 됨
                    bestTop = w;
                    break;
                }
            }
            if (bestTop == null) return null;

            // 같은 그룹에서 점을 포함하는 가장 작은 사각형 선택
            WindowInfo smallest = bestTop;
            long smallestArea = (long)smallest.Bounds.Width * smallest.Bounds.Height;
            foreach (var w in windows)
            {
                if (!w.Bounds.Contains(x, y)) continue;
                long area = (long)w.Bounds.Width * w.Bounds.Height;
                if (area < smallestArea)
                {
                    smallest = w;
                    smallestArea = area;
                }
                if (!w.IsChild) break; // 최상위 창을 지나면 아래 z순서 창들이므로 중단
            }
            return smallest;
        }
    }
}
