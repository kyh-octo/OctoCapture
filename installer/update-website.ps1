# octo-brain.com 배포 섹션 갱신 스크립트
# 1) 웹사이트 저장소(kyh-octo/octobrain-website)에 릴리스 octocapture-v<버전> 생성/갱신 + 설치 파일 업로드
# 2) store.html의 OctoCapture 카드(버전/용량/다운로드/릴리스 노트 링크) 갱신 후 커밋·푸시 → GitHub Pages 자동 배포
# 사용법: powershell -ExecutionPolicy Bypass -File installer\update-website.ps1 -Version 1.5.0 -InstallerPath <exe>
# (build-installer.ps1이 설치 파일 빌드 후 자동으로 호출한다)

param(
    [Parameter(Mandatory = $true)] [string]$Version,
    [Parameter(Mandatory = $true)] [string]$InstallerPath,
    [string]$NotesFile = "",
    [string]$SiteRepoDir = (Join-Path $env:LOCALAPPDATA "OctoBrain\octobrain-website"),
    [string]$CoAuthor = ""
)

$ErrorActionPreference = "Stop"
$SiteRepo = "kyh-octo/octobrain-website"
$SiteRepoUrl = "https://github.com/$SiteRepo.git"
$Tag = "octocapture-v$Version"

if (-not (Test-Path $InstallerPath)) { throw "설치 파일을 찾을 수 없습니다: $InstallerPath" }
$installerName = Split-Path $InstallerPath -Leaf
$sizeMB = [math]::Round((Get-Item $InstallerPath).Length / 1MB)

$gh = @("$env:ProgramFiles\GitHub CLI\gh.exe", "${env:ProgramFiles(x86)}\GitHub CLI\gh.exe", "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $gh) { $cmd = Get-Command gh -ErrorAction SilentlyContinue; if ($cmd) { $gh = $cmd.Source } }
if (-not $gh) { throw "GitHub CLI(gh)를 찾을 수 없습니다." }

Write-Host "== octo-brain.com 배포 갱신: OctoCapture v$Version ==" -ForegroundColor Cyan

# ---------- 1) 웹사이트 저장소 릴리스 생성/갱신 ----------
if (-not $NotesFile) {
    $NotesFile = Join-Path $env:TEMP "octocapture-site-notes-$Version.md"
    @"
OctoCapture v$Version 설치 파일입니다. ``$installerName`` 을 내려받아 실행하세요 (.NET 설치 불필요, Windows 10 2004 이상 x64).

자세한 변경 내역: https://github.com/kyh-octo/OctoCapture/releases/tag/v$Version
"@ | Set-Content -Path $NotesFile -Encoding UTF8
}

# 주의: PowerShell 5.1에서는 네이티브 명령의 stderr를 리다이렉션하면 오류로 승격되므로
#       (gh release view의 "release not found") 리다이렉션 없이 목록으로 존재 여부를 확인한다.
$existingTags = @(& $gh release list -R $SiteRepo --limit 200 --json tagName --jq '.[].tagName')
if ($LASTEXITCODE -ne 0) { throw "웹사이트 저장소 릴리스 목록 조회 실패" }
if ($existingTags -contains $Tag) {
    Write-Host "[1/3] 릴리스 $Tag 존재 → 설치 파일 교체 업로드" -ForegroundColor Yellow
    & $gh release upload $Tag $InstallerPath -R $SiteRepo --clobber
    if ($LASTEXITCODE -ne 0) { throw "릴리스 자산 업로드 실패" }
    & $gh release edit $Tag -R $SiteRepo --notes-file $NotesFile --latest | Out-Null
} else {
    Write-Host "[1/3] 릴리스 $Tag 생성 + 설치 파일 업로드" -ForegroundColor Yellow
    & $gh release create $Tag $InstallerPath -R $SiteRepo --title "OctoCapture $Version" --notes-file $NotesFile --latest
    if ($LASTEXITCODE -ne 0) { throw "릴리스 생성 실패" }
}

# ---------- 2) 자동화 전용 클론을 origin/main에 동기화 ----------
Write-Host "[2/3] 웹사이트 저장소 동기화 ($SiteRepoDir)" -ForegroundColor Yellow
if (-not (Test-Path (Join-Path $SiteRepoDir ".git"))) {
    New-Item -ItemType Directory -Force (Split-Path $SiteRepoDir -Parent) | Out-Null
    git clone --quiet $SiteRepoUrl $SiteRepoDir
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 저장소 클론 실패" }
} else {
    git -C $SiteRepoDir fetch --quiet origin
    git -C $SiteRepoDir reset --quiet --hard origin/main
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 저장소 동기화 실패" }
}

# ---------- 3) store.html의 OctoCapture 카드 갱신 ----------
$storePath = Join-Path $SiteRepoDir "store.html"
if (-not (Test-Path $storePath)) { throw "store.html을 찾을 수 없습니다: $storePath" }

$bytes = [System.IO.File]::ReadAllBytes($storePath)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$html = [System.Text.Encoding]::UTF8.GetString($bytes)
if ($hasBom) { $html = $html.TrimStart([char]0xFEFF) }

$titleIdx = $html.IndexOf('<h3 class="dl-title">OctoCapture</h3>')
if ($titleIdx -lt 0) { throw "store.html에서 OctoCapture 카드를 찾을 수 없습니다." }
$start = $html.LastIndexOf('<article', $titleIdx)
$end = $html.IndexOf('</article>', $titleIdx)
if ($start -lt 0 -or $end -lt 0) { throw "OctoCapture 카드의 <article> 범위를 찾을 수 없습니다." }
$end += '</article>'.Length

$card = $html.Substring($start, $end - $start)
$new = $card
$new = [regex]::Replace($new, '(<span class="dl-version">)v[^<]+(</span>)', "`${1}v$Version`${2}")
$new = [regex]::Replace($new, '(<p class="dl-meta">Windows 10/11 · 64bit · )\d+MB(</p>)', "`${1}${sizeMB}MB`${2}")
$new = [regex]::Replace($new, 'releases/download/octocapture-v[^/"]+/[^"]+\.exe', "releases/download/$Tag/$installerName")
$new = [regex]::Replace($new, 'releases/tag/octocapture-v[^"]+', "releases/tag/$Tag")

if ($new -eq $card) {
    Write-Host "store.html 변경 없음 (이미 v$Version)" -ForegroundColor DarkGray
} else {
    $html = $html.Substring(0, $start) + $new + $html.Substring($end)
    $enc = New-Object System.Text.UTF8Encoding($hasBom)
    [System.IO.File]::WriteAllText($storePath, $html, $enc)

    $msgFile = Join-Path $env:TEMP "octocapture-site-commit-$Version.txt"
    $msg = "OctoCapture v$Version 배포 갱신"
    if ($CoAuthor) { $msg += "`n`nCo-Authored-By: $CoAuthor" }
    [System.IO.File]::WriteAllText($msgFile, $msg, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "[3/3] store.html 갱신 → 커밋/푸시 (GitHub Pages 자동 배포)" -ForegroundColor Yellow
    git -C $SiteRepoDir add store.html
    git -C $SiteRepoDir commit --quiet -F $msgFile
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 커밋 실패" }
    git -C $SiteRepoDir push --quiet origin main
    if ($LASTEXITCODE -ne 0) { throw "웹사이트 푸시 실패" }
}

Write-Host "완료: https://www.octo-brain.com/store.html  (다운로드: https://github.com/$SiteRepo/releases/download/$Tag/$installerName)" -ForegroundColor Green
