; OctoCapture 설치 스크립트 (Inno Setup 6)
; 빌드 방법: installer\build-installer.ps1 실행 (게시 → 설치파일 생성까지 자동)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "OctoCapture"
#define AppPublisher "OctoBrain Softworks"
#define AppExeName "OctoCapture.exe"
#define PublishDir "..\bin\Release\Publish"

[Setup]
; AppId는 업그레이드 인식용 고유 값 - 절대 변경하지 말 것
AppId={{D0CAD3E1-B621-46D8-9272-B026C9553234}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir=output
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
SetupIconFile=..\OctoCapture.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 관리자 권한 없이 사용자 단위 설치 (프로그램 파일 대신 LocalAppData)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; 실행 중인 OctoCapture를 감지해 종료 안내 (앱의 단일 인스턴스 뮤텍스)
AppMutex=OctoCapture_SingleInstance
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; Excludes: "*.pdb,*.xml"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; 앱 내 자동 업데이트(/SILENT /AUTOUPDATE=1)로 설치된 경우, 설치가 끝나면 앱을 다시 실행한다
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: IsAutoUpdate

[UninstallRun]
; 제거 전에 실행 중인 앱 종료
Filename: "{cmd}"; Parameters: "/C taskkill /F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "KillApp"
; 시작프로그램 등록이 남아있으면 정리
Filename: "{cmd}"; Parameters: "/C reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v OctoCapture /f"; Flags: runhidden; RunOnceId: "RemoveStartup"

[UninstallDelete]
; 앱이 만든 설정/로그 정리 (시작프로그램 등록은 앱 설정에서 해제)
Type: filesandordirs; Name: "{userappdata}\OctoCapture"

[Code]
{ 앱 내 자동 업데이트(UpdateService)가 /AUTOUPDATE=1 매개변수로 실행했는지 }
function IsAutoUpdate: Boolean;
begin
  Result := ExpandConstant('{param:AUTOUPDATE|0}') = '1';
end;
