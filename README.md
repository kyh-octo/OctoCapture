# OctoCapture

Windows용 무료 화면 캡쳐 / 녹화 도구입니다. 알캡쳐 스타일의 다양한 캡쳐 모드와 이미지 편집, 화면 녹화(MP4/GIF/WebP)를 지원합니다.

> A free screen capture & recording tool for Windows — multiple capture modes, built-in image editor, and screen recording to MP4/GIF/WebP.

## 다운로드

[Releases](../../releases) 페이지에서 최신 설치 파일(`OctoCapture-Setup-x.x.x.exe`)을 받아 실행하세요.
.NET 런타임 설치가 필요 없는 자체 포함(self-contained) 배포입니다.

## 주요 기능

### 캡쳐
- **직접 캡쳐** — 드래그로 영역 지정
- **창 캡쳐** — 마우스로 창을 골라 클릭
- **단위별 캡쳐** — 창 안의 컨트롤(버튼·패널 등) 단위 캡쳐
- **화면 캡쳐** — 모니터 단위 캡쳐 (멀티 모니터 지원)
- **전체 캡쳐** — 모든 모니터를 한 장으로
- **스크롤 캡쳐** — 긴 페이지를 자동 스크롤하며 이어붙이기
- 캡쳐 시작 시 화면이 정지(프리즈 프레임)되고, 상단의 **모드 바**로 캡쳐 방식을 즉시 전환하거나 취소할 수 있습니다 (드래그로 이동 가능)
- 캡쳐와 동시에 클립보드 복사, 캡쳐 목록에서 언제든 다시 저장/복사

### 이미지 편집
- 펜, 직선, 화살표, 사각형, 원, 텍스트
- **모자이크**, **자르기**
- 색상 팔레트, 굵기 조절, 실행취소(Ctrl+Z), 확대/축소(Ctrl+휠)

### 화면 녹화
- 영역을 지정하고 녹화 시작/종료 (H.264 MP4)
- **시스템 소리 / 마이크** 포함 여부 선택
- **MP4 / GIF / WebP**로 저장, 파일로 클립보드 복사
- 내장 플레이어로 재생 + **앞뒤 자르기(트림)** 편집
- GIF/WebP 변환용 ffmpeg는 최초 1회 자동 다운로드

### 편의 기능
- 전역 단축키 (모두 설정에서 변경 가능)
- 트레이 최소화, 시작프로그램 등록
- 한국어 UI

## 기본 단축키

| 기능 | 단축키 |
|---|---|
| 직접 캡쳐 | `Ctrl+Shift+A` |
| 창 캡쳐 | `Ctrl+Shift+W` |
| 단위별 캡쳐 | `Ctrl+Shift+E` |
| 화면 캡쳐 | `Ctrl+Shift+M` |
| 전체 캡쳐 | `Ctrl+Shift+F` |
| 스크롤 캡쳐 | `Ctrl+Shift+L` |
| 녹화 시작/종료 | `Ctrl+Shift+R` |
| 메인 창 열기 | `Ctrl+Shift+O` |

## 소스에서 빌드

요구 사항: [.NET 10 SDK](https://dotnet.microsoft.com/download), Windows 10 2004 이상

```powershell
dotnet build OctoCapture.csproj          # 개발 빌드 (x64)
dotnet publish -p:PublishProfile=FolderProfile1   # 단일 파일 게시
```

설치 파일 생성 ([Inno Setup 6](https://jrsoftware.org/isdl.php) 필요):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

## 기술 스택

- C# / WPF (.NET 10, x64)
- [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) — 화면 녹화 (Windows Graphics Capture + Media Foundation)
- [FFmpeg](https://ffmpeg.org/) — GIF/WebP 변환 (런타임 자동 다운로드)
- [Inno Setup](https://jrsoftware.org/isinfo.php) — 설치 파일

## 라이선스

[MIT License](LICENSE) © 2026 OctoBrain Softworks
