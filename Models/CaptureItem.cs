using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OctoCapture.Models
{
    public enum CaptureItemKind { Image, Video }

    /// <summary>
    /// 캡쳐 히스토리 항목.
    /// 메모리 최적화: 원본 BitmapSource를 들고 있지 않고 PNG 인코딩된 byte[]와
    /// 작은 썸네일(Frozen)만 유지한다. 원본은 필요할 때 디코딩한다.
    /// 동영상은 임시 파일 경로만 유지한다(앱 종료 시 삭제).
    /// 편집으로 내용이 바뀌면 PropertyChanged로 목록 UI가 갱신된다.
    /// </summary>
    public class CaptureItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public CaptureItemKind Kind { get; }
        public DateTime CreatedAt { get; } = DateTime.Now;

        /// <summary>항목 고유 ID (임시 파일명 충돌 방지용)</summary>
        public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
        public string Title { get; set; } = "";

        /// <summary>이미지 항목: PNG 인코딩 데이터</summary>
        public byte[]? PngData { get; private set; }

        /// <summary>동영상 항목: 임시 mp4 파일 경로</summary>
        public string? VideoPath { get; private set; }

        public TimeSpan? Duration { get; set; }
        public int PixelWidth { get; private set; }
        public int PixelHeight { get; private set; }

        /// <summary>목록 표시용 썸네일 (Frozen, 최대 200px)</summary>
        public BitmapSource? Thumbnail { get; private set; }

        /// <summary>동영상 형식 변환 캐시 (형식 → 임시 파일 경로). 같은 형식으로 다시 복사할 때 재변환을 피한다.</summary>
        public Dictionary<string, string> ConvertedCache { get; } = new();

        public string Info => Kind == CaptureItemKind.Image
            ? $"{PixelWidth}×{PixelHeight}  ·  {FormatSize(PngData?.LongLength ?? 0)}"
            : $"{PixelWidth}×{PixelHeight}  ·  {(Duration.HasValue ? Duration.Value.ToString(@"mm\:ss") : "")}  ·  {FormatSize(FileSize())}";

        public string TimeText => CreatedAt.ToString("HH:mm:ss");

        /// <summary>목록 썸네일 위 재생 아이콘 표시 여부 (동영상만)</summary>
        public System.Windows.Visibility PlayIconVisibility =>
            Kind == CaptureItemKind.Video ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        private CaptureItem(CaptureItemKind kind, string? videoPath)
        {
            Kind = kind;
            VideoPath = videoPath;
        }

        public static CaptureItem FromImage(BitmapSource source, string title)
        {
            var item = new CaptureItem(CaptureItemKind.Image, null) { Title = title };
            item.SetImage(source);
            return item;
        }

        public static CaptureItem FromVideo(string videoPath, string title, int width, int height, TimeSpan? duration, BitmapSource? thumbnail)
        {
            var item = new CaptureItem(CaptureItemKind.Video, videoPath)
            {
                Title = title,
                PixelWidth = width,
                PixelHeight = height,
                Duration = duration,
            };
            if (thumbnail != null)
                item.Thumbnail = MakeThumbnail(thumbnail);
            return item;
        }

        /// <summary>이미지 교체 (편집 [저장]=덮어쓰기 포함). 목록 UI가 자동 갱신된다.</summary>
        public void SetImage(BitmapSource source)
        {
            if (Kind != CaptureItemKind.Image) throw new InvalidOperationException();
            PixelWidth = source.PixelWidth;
            PixelHeight = source.PixelHeight;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            PngData = ms.ToArray();

            Thumbnail = MakeThumbnail(source);
            NotifyVisuals();
        }

        /// <summary>동영상 교체 (트림 [저장]=덮어쓰기). 이전 임시 파일과 변환 캐시를 정리한다.</summary>
        public void ReplaceVideo(string newPath, TimeSpan? duration)
        {
            if (Kind != CaptureItemKind.Video) throw new InvalidOperationException();
            string? old = VideoPath;
            VideoPath = newPath;
            Duration = duration;

            // 클립보드가 참조 중인 파일은 남기고, 잠긴 파일은 지연 재시도로 정리
            foreach (var cached in ConvertedCache.Values)
                Services.TempFileCleaner.DeleteLater(cached);
            ConvertedCache.Clear();

            if (old != null && !string.Equals(old, newPath, StringComparison.OrdinalIgnoreCase))
                Services.TempFileCleaner.DeleteLater(old);

            NotifyVisuals();
        }

        /// <summary>항목 삭제 시 관련 임시 파일 정리 (클립보드 참조 파일은 보호)</summary>
        public void DeleteTempFiles()
        {
            if (Kind != CaptureItemKind.Video) return;
            Services.TempFileCleaner.DeleteLater(VideoPath);
            foreach (var cached in ConvertedCache.Values)
                Services.TempFileCleaner.DeleteLater(cached);
            ConvertedCache.Clear();
        }

        private void NotifyVisuals()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Info)));
        }

        /// <summary>원본 이미지를 디코딩해 반환. 호출자가 캐시하지 말 것(메모리 절약).</summary>
        public BitmapSource? LoadFullImage()
        {
            if (Kind != CaptureItemKind.Image || PngData == null) return null;
            using var ms = new MemoryStream(PngData);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }

        private static BitmapSource MakeThumbnail(BitmapSource source)
        {
            const double maxSide = 200;
            double scale = Math.Min(1.0, maxSide / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource thumb = scale < 1.0
                ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
                : source;
            // TransformedBitmap이 원본을 참조하지 않도록 픽셀을 복사해 독립 비트맵 생성
            var cached = new WriteableBitmap(thumb);
            cached.Freeze();
            return cached;
        }

        private long FileSize()
        {
            try { return VideoPath != null && File.Exists(VideoPath) ? new FileInfo(VideoPath).Length : 0; }
            catch { return 0; }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:0.0} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:0.0} KB";
            return $"{bytes} B";
        }
    }
}
