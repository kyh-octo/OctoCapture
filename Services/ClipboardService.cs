using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace OctoCapture.Services
{
    /// <summary>클립보드 복사 유틸. 이미지(DIB/Bitmap/PNG 동시 등록), 파일 복사 지원.</summary>
    public static class ClipboardService
    {
        /// <summary>이미지를 클립보드에 복사. PNG 포맷도 함께 실어 투명도 지원 앱과 호환.</summary>
        public static bool CopyImage(BitmapSource image)
        {
            try
            {
                var data = new DataObject();
                data.SetImage(image);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                var pngStream = new MemoryStream();
                encoder.Save(pngStream);
                pngStream.Position = 0;
                data.SetData("PNG", pngStream, false);

                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch { return false; }
        }

        public static bool CopyImage(byte[] pngData)
        {
            try
            {
                using var ms = new MemoryStream(pngData);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                return CopyImage(img);
            }
            catch { return false; }
        }

        /// <summary>파일을 클립보드에 복사(탐색기 붙여넣기 가능). 동영상 복사에 사용.</summary>
        public static bool CopyFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var files = new StringCollection { path };
                Clipboard.SetFileDropList(files);
                return true;
            }
            catch { return false; }
        }
    }
}
