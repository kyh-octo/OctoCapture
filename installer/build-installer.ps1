# OctoCapture 설치 파일 빌드 스크립트
# 사용법: powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
# 결과물: installer\output\OctoCapture-Setup-<버전>.exe

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "OctoCapture.csproj"

# 1) csproj에서 버전 읽기
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }
Write-Host "== OctoCapture v$version 설치 파일 빌드 ==" -ForegroundColor Cyan

# 2) 게시 (단일 파일, 자체 포함 - 대상 PC에 .NET 설치 불필요)
Write-Host "[1/2] dotnet publish..." -ForegroundColor Yellow
dotnet publish $csproj "-p:PublishProfile=FolderProfile1" -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "게시 실패 (exit $LASTEXITCODE)" }

# 3) Inno Setup 컴파일
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6을 찾을 수 없습니다. https://jrsoftware.org/isdl.php 에서 설치하세요." }

Write-Host "[2/2] Inno Setup 컴파일..." -ForegroundColor Yellow
& $iscc "/DAppVersion=$version" (Join-Path $PSScriptRoot "OctoCapture.iss") | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "설치 파일 컴파일 실패 (exit $LASTEXITCODE)" }

$setup = Join-Path $PSScriptRoot "output\OctoCapture-Setup-$version.exe"
if (Test-Path $setup) {
    $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
    Write-Host "완료: $setup ($mb MB)" -ForegroundColor Green
} else {
    throw "설치 파일이 생성되지 않았습니다."
}
