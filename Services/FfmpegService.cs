using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using OctoCapture.Models;

namespace OctoCapture.Services
{
    /// <summary>
    /// ffmpeg 탐색/다운로드 및 MP4 → GIF/WebP 변환.
    /// ffmpeg가 없으면 사용자 동의 후 %AppData%\OctoCapture\ffmpeg 에 내려받는다.
    /// </summary>
    public static class FfmpegService
    {
        private const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        private static string LocalDir => Path.Combine(AppSettings.SettingsDir, "ffmpeg");
        private static string LocalExe => Path.Combine(LocalDir, "ffmpeg.exe");

        public static string? FindFfmpeg()
        {
            if (File.Exists(LocalExe)) return LocalExe;

            // 실행 파일 옆
            string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(beside)) return beside;

            // PATH
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    string p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (!string.IsNullOrWhiteSpace(dir) && File.Exists(p)) return p;
                }
                catch { }
            }
            return null;
        }

        /// <summary>ffmpeg 확보. 없으면 다운로드 여부를 묻고 내려받는다. 실패/거부 시 null.</summary>
        public static async Task<string?> EnsureFfmpegAsync()
        {
            var found = FindFfmpeg();
            if (found != null) return found;

            var answer = MessageBox.Show(
                "GIF/WebP 변환에는 ffmpeg가 필요합니다.\n지금 다운로드할까요? (약 30~90MB, 1회만)",
                "OctoCapture", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return null;

            try
            {
                return await Windows.BusyWindow.RunAsync("ffmpeg 다운로드 중… 잠시만 기다려주세요.", DownloadAsync);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ffmpeg 다운로드에 실패했습니다.\n{ex.Message}", "OctoCapture",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        private static async Task<string?> DownloadAsync()
        {
            Directory.CreateDirectory(LocalDir);
            string zipPath = Path.Combine(LocalDir, "ffmpeg.zip");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            await using (var stream = await http.GetStreamAsync(DownloadUrl))
            await using (var file = File.Create(zipPath))
            {
                await stream.CopyToAsync(file);
            }

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidOperationException("압축 파일에서 ffmpeg.exe를 찾지 못했습니다.");
                entry.ExtractToFile(LocalExe, true);
            }
            File.Delete(zipPath);
            return LocalExe;
        }

        /// <summary>MP4를 GIF 또는 WebP로 변환. format: "gif" | "webp"</summary>
        public static Task<bool> ConvertAsync(string ffmpeg, string input, string output, string format, int gifFps)
            => TrimConvertAsync(ffmpeg, input, output, format, null, null, gifFps);

        /// <summary>
        /// 동영상 트림(구간 잘라내기) + 포맷 변환.
        /// start/duration이 null이면 전체 구간. format: "mp4" | "gif" | "webp"
        /// </summary>
        public static async Task<bool> TrimConvertAsync(string ffmpeg, string input, string output,
            string format, TimeSpan? start, TimeSpan? duration, int gifFps)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            // 입력 시킹(-ss를 -i 앞에)으로 빠르게 이동한 뒤 -t(지속시간)로 자른다
            string seek = start.HasValue ? $"-ss {start.Value.TotalSeconds.ToString("0.###", inv)} " : "";
            string len = duration.HasValue ? $"-t {duration.Value.TotalSeconds.ToString("0.###", inv)} " : "";

            string args = format switch
            {
                "mp4" => $"-y {seek}-i \"{input}\" {len}-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -c:a aac -movflags +faststart \"{output}\"",
                "gif" => $"-y {seek}-i \"{input}\" {len}-vf \"fps={gifFps},split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4\" -loop 0 \"{output}\"",
                "webp" => $"-y {seek}-i \"{input}\" {len}-c:v libwebp -quality 80 -compression_level 4 -loop 0 -an \"{output}\"",
                _ => throw new ArgumentException(format),
            };

            var psi = new ProcessStartInfo(ffmpeg, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            // 에러 스트림을 소비해야 버퍼가 차서 멈추지 않음
            _ = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 && File.Exists(output);
        }
    }
}
