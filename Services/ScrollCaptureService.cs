using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OctoCapture.Services
{
    /// <summary>
    /// 스크롤 캡쳐: 대상 창을 휠 스크롤하며 반복 캡쳐 후 겹침을 찾아 세로로 이어붙인다.
    /// 행 해시 비교로 겹침 오프셋을 계산한다.
    /// </summary>
    public static class ScrollCaptureService
    {
        private const int MaxTotalHeight = 32000;   // 최대 결과 높이(픽셀) - 폭주 방지
        private const int MaxIterations = 60;
        private const int WheelDelta = -120 * 3;    // 한 번에 3틱 아래로

        /// <param name="stopRequested">true를 반환하면 지금까지 모은 프레임으로 완료한다.</param>
        public static async Task<BitmapSource?> CaptureAsync(IntPtr hWnd, RECT clientRect,
            Func<bool> stopRequested, CancellationToken ct)
        {
            // 대상 창 활성화 후 커서를 영역 중앙에 위치(휠 이벤트가 대상에 가도록)
            NativeMethods.SetForegroundWindow(hWnd);
            await Task.Delay(300, ct);
            int cx = clientRect.Left + clientRect.Width / 2;
            int cy = clientRect.Top + clientRect.Height / 2;
            NativeMethods.SetCursorPos(cx, cy);
            await Task.Delay(100, ct);

            var frames = new List<PixelFrame>();
            PixelFrame? prev = null;

            for (int i = 0; i < MaxIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                var shot = ScreenCaptureService.CaptureRegion(clientRect);
                var frame = PixelFrame.From(shot);

                if (prev != null && frame.IsIdenticalTo(prev))
                    break; // 더 이상 스크롤되지 않음 → 끝

                frames.Add(frame);
                prev = frame;

                long total = frames.Sum(f => (long)f.Height);
                if (total > MaxTotalHeight) break;
                if (stopRequested()) break;

                NativeMethods.SendMouseWheel(WheelDelta);
                await Task.Delay(350, ct); // 스크롤 애니메이션/렌더링 대기
            }

            if (frames.Count == 0) return null;
            if (frames.Count == 1) return frames[0].ToBitmap();

            return Stitch(frames);
        }

        private static BitmapSource Stitch(List<PixelFrame> frames)
        {
            int width = frames[0].Width;
            var rows = new List<byte[]>(); // 최종 이미지의 행 데이터(BGRA)

            // 첫 프레임 전체 추가
            for (int y = 0; y < frames[0].Height; y++)
                rows.Add(frames[0].GetRow(y));

            for (int i = 1; i < frames.Count; i++)
            {
                var prev = frames[i - 1];
                var cur = frames[i];

                int offset = FindScrollOffset(prev, cur);
                if (offset <= 0)
                {
                    // 겹침을 못 찾으면 프레임 전체를 이어붙임 (최악의 경우 중복 포함)
                    offset = cur.Height;
                }
                // cur의 아래쪽 offset 행이 새로운 내용
                int newStart = cur.Height - offset;
                for (int y = newStart; y < cur.Height; y++)
                    rows.Add(cur.GetRow(y));

                if (rows.Count > MaxTotalHeight) break;
            }

            int height = Math.Min(rows.Count, MaxTotalHeight);
            var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            int stride = width * 4;
            for (int y = 0; y < height; y++)
                wb.WritePixels(new System.Windows.Int32Rect(0, y, width, 1), rows[y], stride, 0);
            wb.Freeze();
            return wb;
        }

        /// <summary>
        /// prev 대비 cur가 얼마나 스크롤되었는지(픽셀)를 행 해시 정렬로 찾는다.
        /// prev의 아래쪽 기준 스트립이 cur에서 얼마나 위로 이동했는지 계산.
        /// </summary>
        private static int FindScrollOffset(PixelFrame prev, PixelFrame cur)
        {
            int h = prev.Height;
            // 기준 스트립: prev의 60% 지점부터 40행 (고정 헤더/푸터 영향 최소화)
            int stripStart = (int)(h * 0.6);
            int stripLen = Math.Min(40, h - stripStart - 1);
            if (stripLen < 8) return -1;

            var prevHashes = prev.RowHashes;
            var curHashes = cur.RowHashes;

            // cur에서 스트립과 일치하는 위치 탐색 (stripStart보다 위쪽으로만 = 아래로 스크롤)
            for (int pos = stripStart - 1; pos >= 0; pos--)
            {
                bool match = true;
                for (int k = 0; k < stripLen; k++)
                {
                    if (curHashes[pos + k] != prevHashes[stripStart + k]) { match = false; break; }
                }
                if (match)
                {
                    int offset = stripStart - pos; // 스크롤된 픽셀 수
                    if (offset > 0 && offset < h) return offset;
                }
            }
            return -1;
        }

        /// <summary>BGRA 픽셀 버퍼 + 행 해시 캐시. 스크롤바 열(우측 ~20px)은 해시에서 제외.</summary>
        private sealed class PixelFrame
        {
            public int Width, Height;
            private byte[] _pixels = Array.Empty<byte>();
            private ulong[]? _rowHashes;

            public ulong[] RowHashes => _rowHashes ??= ComputeRowHashes();

            public static PixelFrame From(BitmapSource src)
            {
                var converted = src.Format == PixelFormats.Bgra32
                    ? src
                    : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                var f = new PixelFrame { Width = converted.PixelWidth, Height = converted.PixelHeight };
                f._pixels = new byte[f.Width * f.Height * 4];
                converted.CopyPixels(f._pixels, f.Width * 4, 0);
                return f;
            }

            public byte[] GetRow(int y)
            {
                int stride = Width * 4;
                var row = new byte[stride];
                Buffer.BlockCopy(_pixels, y * stride, row, 0, stride);
                return row;
            }

            public bool IsIdenticalTo(PixelFrame other)
            {
                if (Width != other.Width || Height != other.Height) return false;
                return RowHashes.AsSpan().SequenceEqual(other.RowHashes);
            }

            private ulong[] ComputeRowHashes()
            {
                int stride = Width * 4;
                int usableBytes = Math.Max(4, (Width - 20) * 4); // 스크롤바 제외
                var hashes = new ulong[Height];
                for (int y = 0; y < Height; y++)
                {
                    ulong hash = 14695981039346656037UL; // FNV-1a
                    int baseIdx = y * stride;
                    for (int b = 0; b < usableBytes; b += 4)
                    {
                        // BGR만 사용(알파 제외)
                        hash = (hash ^ _pixels[baseIdx + b]) * 1099511628211UL;
                        hash = (hash ^ _pixels[baseIdx + b + 1]) * 1099511628211UL;
                        hash = (hash ^ _pixels[baseIdx + b + 2]) * 1099511628211UL;
                    }
                    hashes[y] = hash;
                }
                return hashes;
            }

            public BitmapSource ToBitmap()
            {
                var wb = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new System.Windows.Int32Rect(0, 0, Width, Height), _pixels, Width * 4, 0);
                wb.Freeze();
                return wb;
            }
        }
    }
}
