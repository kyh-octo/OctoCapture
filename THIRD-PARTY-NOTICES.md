# Third-Party Notices

## OctoCapture

OctoCapture is licensed under the MIT License. See [LICENSE](LICENSE).

## ScreenRecorderLib 6.3.0

ScreenRecorderLib is licensed under the MIT License. The license text from the package is included at [licenses/ScreenRecorderLib-6.3.0-LICENSE.txt](licenses/ScreenRecorderLib-6.3.0-LICENSE.txt).

Upstream project: https://github.com/sskodje/ScreenRecorderLib

## Microsoft .NET runtimes 10.0.11

This application bundles the .NET 10.0.11 Windows x64 runtime and Windows Desktop runtime. Their official package license and third-party notice files are included under `licenses/`:

- `Microsoft.NETCore.App.Runtime.win-x64-10.0.11-LICENSE.txt`
- `Microsoft.NETCore.App.Runtime.win-x64-10.0.11-THIRD-PARTY-NOTICES.txt`
- `Microsoft.WindowsDesktop.App.Runtime.win-x64-10.0.11-LICENSE.txt`

These files are copied from the corresponding Microsoft NuGet runtime packages without modification.

## FFmpeg (optional external download)

FFmpeg is not bundled with OctoCapture. When a user chooses GIF or WebP conversion, the application can offer an optional external FFmpeg download. Supported builds may be provided by [Gyan](https://www.gyan.dev/ffmpeg/builds/) or [BtbN](https://github.com/BtbN/FFmpeg-Builds); these are GPL builds. FFmpeg's terms apply to the separately downloaded binary. See the FFmpeg project licensing information at https://ffmpeg.org/legal.html.

OctoCapture does not bundle LibVLC.