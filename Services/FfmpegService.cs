using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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

        /// <summary>ffmpeg 확보. 없으면 다운로드 여부를 묻고 내려받는다. 실패/거부/취소 시 null.</summary>
        public static async Task<string?> EnsureFfmpegAsync()
        {
            var found = FindFfmpeg();
            if (found != null) return found;

            var answer = MessageBox.Show(
                "GIF/WebP 변환과 영상 구간 편집에는 ffmpeg가 필요합니다.\n" +
                "지금 다운로드할까요? (약 100MB, 최초 1회만 · GitHub에서 내려받습니다)",
                "OctoCapture", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return null;

            try
            {
                return await Windows.BusyWindow.RunAsync("ffmpeg 다운로드 중",
                    (progress, ct) => DownloadAsync(progress, ct), allowCancel: true);
            }
            catch (OperationCanceledException)
            {
                return null; // 사용자가 취소
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ffmpeg 다운로드에 실패했습니다.\n{ex.Message}", "OctoCapture",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
        }

        /// <summary>
        /// 다운로드 서버 후보 (빠른 순). 국내에서 gyan.dev 직접 다운로드는 ~0.8MB/s로 매우 느리므로
        /// GitHub에 올라온 동일 빌드(gyan 공식 미러)를 우선 사용하고, 실패 시 BtbN 빌드 → gyan.dev 순으로 시도한다.
        /// </summary>
        private static async Task<List<(string Label, string Url)>> ResolveMirrorsAsync(HttpClient http, CancellationToken ct)
        {
            var list = new List<(string, string)>();
            try
            {
                using var res = await http.GetAsync("https://api.github.com/repos/GyanD/codexffmpeg/releases/latest", ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                    {
                        string name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith("-essentials_build.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            var url = asset.GetProperty("browser_download_url").GetString();
                            if (!string.IsNullOrEmpty(url)) list.Add(("GitHub (gyan 미러)", url));
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* API 실패 시 아래 고정 URL로 진행 */ }

            list.Add(("GitHub (BtbN)", "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip"));
            list.Add(("gyan.dev", DownloadUrl));
            return list;
        }

        private static async Task<string?> DownloadAsync(IProgress<Windows.BusyProgress> progress, CancellationToken ct)
        {
            Directory.CreateDirectory(LocalDir);
            string zipPath = Path.Combine(LocalDir, "ffmpeg.zip");
            try { File.Delete(zipPath); } catch { }

            using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
            {
                Timeout = Timeout.InfiniteTimeSpan, // 무응답 감지는 아래에서 청크 단위로 처리
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OctoCapture");

            progress.Report(new Windows.BusyProgress { Status = "다운로드 서버를 찾는 중…" });
            var mirrors = await ResolveMirrorsAsync(http, ct);

            Exception? lastError = null;
            bool downloaded = false;
            foreach (var (label, url) in mirrors)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    progress.Report(new Windows.BusyProgress { Status = $"{label}에 연결하는 중…" });
                    await DownloadFileAsync(http, url, zipPath, label, progress, ct);
                    downloaded = true;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { File.Delete(zipPath); } catch { }
                    progress.Report(new Windows.BusyProgress { Status = $"{label} 실패 ({ex.Message}) → 다음 서버 시도" });
                }
            }
            if (!downloaded)
                throw new InvalidOperationException("모든 다운로드 서버에서 실패했습니다.\n" + (lastError?.Message ?? ""));

            progress.Report(new Windows.BusyProgress { Fraction = null, Status = "압축 해제 중…" });
            await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidOperationException("압축 파일에서 ffmpeg.exe를 찾지 못했습니다.");
                entry.ExtractToFile(LocalExe, true);
            }, ct);
            try { File.Delete(zipPath); } catch { }
            return LocalExe;
        }

        /// <summary>진행률/속도를 보고하며 파일을 내려받는다. 30초 동안 데이터가 없으면 실패로 간주한다.</summary>
        private static async Task DownloadFileAsync(HttpClient http, string url, string destPath, string label,
            IProgress<Windows.BusyProgress> progress, CancellationToken ct)
        {
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? -1;

            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long done = 0;
            var sw = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            while (true)
            {
                var readTask = src.ReadAsync(buffer, 0, buffer.Length, ct);
                var finished = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(30), ct));
                if (finished != readTask)
                    throw new TimeoutException("30초 동안 응답이 없습니다");
                int n = await readTask;
                if (n == 0) break;

                await dst.WriteAsync(buffer, 0, n, ct);
                done += n;

                if (sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(200))
                {
                    lastReport = sw.Elapsed;
                    double speed = done / Math.Max(0.001, sw.Elapsed.TotalSeconds) / (1024 * 1024);
                    string sizeText = total > 0
                        ? $"{done / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"
                        : $"{done / 1048576.0:0.0} MB";
                    progress.Report(new Windows.BusyProgress
                    {
                        Fraction = total > 0 ? (double)done / total : null,
                        Status = $"{label}에서 내려받는 중 … {sizeText}  ({speed:0.0} MB/s)",
                    });
                }
            }
            if (total > 0 && done < total)
                throw new IOException("다운로드가 중간에 끊겼습니다");
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
            return await RunAsync(ffmpeg, args, output);
        }

        /// <summary>
        /// 동영상에서 [start, end) 구간을 제거하고 앞뒤를 이어붙인다 (MP4).
        /// 구간이 처음/끝에 닿으면 단순 트림으로 처리한다.
        /// </summary>
        public static async Task<bool> RemoveRangeAsync(string ffmpeg, string input, string output,
            TimeSpan start, TimeSpan end, TimeSpan total)
        {
            var eps = TimeSpan.FromMilliseconds(50);
            if (start <= eps)
                return await TrimConvertAsync(ffmpeg, input, output, "mp4", end, null, 15);          // 뒤쪽만 남김
            if (end >= total - eps)
                return await TrimConvertAsync(ffmpeg, input, output, "mp4", null, start, 15);        // 앞쪽만 남김

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string s = start.TotalSeconds.ToString("0.###", inv);
            string e = end.TotalSeconds.ToString("0.###", inv);
            string enc = "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart";

            // 1차: 영상+음성 (음성 트랙이 없으면 실패하므로 2차에서 영상만)
            string withAudio =
                $"-y -i \"{input}\" -filter_complex \"" +
                $"[0:v]trim=0:{s},setpts=PTS-STARTPTS[v0];[0:a]atrim=0:{s},asetpts=PTS-STARTPTS[a0];" +
                $"[0:v]trim={e},setpts=PTS-STARTPTS[v1];[0:a]atrim={e},asetpts=PTS-STARTPTS[a1];" +
                $"[v0][a0][v1][a1]concat=n=2:v=1:a=1[v][a]\" -map \"[v]\" -map \"[a]\" {enc} -c:a aac \"{output}\"";
            if (await RunAsync(ffmpeg, withAudio, output)) return true;

            string videoOnly =
                $"-y -i \"{input}\" -filter_complex \"" +
                $"[0:v]trim=0:{s},setpts=PTS-STARTPTS[v0];[0:v]trim={e},setpts=PTS-STARTPTS[v1];" +
                $"[v0][v1]concat=n=2:v=1:a=0[v]\" -map \"[v]\" {enc} -an \"{output}\"";
            return await RunAsync(ffmpeg, videoOnly, output);
        }

        private static async Task<bool> RunAsync(string ffmpeg, string args, string output)
        {
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
