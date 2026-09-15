#define AppName "Cozy Roomswitch"
#define AppExeName "CozyRoomswitch.exe"
#define AppPublisher "sol669"
#define AppURL "https://github.com/sol669/Cozy-Roomswitch"

#ifndef AppVersion
  #define AppVersion "1.0.5"
#endif

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

[Setup]
AppId={{EDACA359-B3D4-4F05-B077-521D8BB4FDE1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppPublisher}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\release
OutputBaseFilename=Cozy-Roomswitch-Setup-v{#AppVersion}
SetupIconFile=..\rebuild\src\RoomSwitcherTray.Core\Assets\AppIcon\RoomSwitcher.ico
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
AppMutex=sol669.CozyRoomswitch.Singleton
MinVersion=10.0.17763
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.30/windowsdesktop-runtime-8.0.30-win-x64.exe"; DestDir: "{tmp}"; DestName: "windowsdesktop-runtime-8.0.30-win-x64.exe"; ExternalSize: 58510672; Flags: external download ignoreversion; Check: not IsDotNetDesktop8Installed
Source: "https://download.microsoft.com/download/cbb0f858-7923-48f0-bc2e-ba8ae187b65c/WindowsAppRuntimeInstall-x64.exe"; DestDir: "{tmp}"; DestName: "WindowsAppRuntimeInstall-x64-2.3.1.exe"; ExternalSize: 113192760; Flags: external download ignoreversion; Check: not IsWindowsAppRuntimeInstalled

[Dirs]
Name: "{app}\Data"; Permissions: users-modify

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Cozy Roomswitch"; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\AppIcon\RoomSwitcher.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\Assets\AppIcon\RoomSwitcher.ico"; Tasks: desktopicon

[UninstallDelete]
Type: filesandordirs; Name: "{app}\Data"

[Run]
Filename: "{tmp}\windowsdesktop-runtime-8.0.30-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft .NET Desktop Runtime..."; Flags: waituntilterminated; Check: not IsDotNetDesktop8Installed
Filename: "{tmp}\WindowsAppRuntimeInstall-x64-2.3.1.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App Runtime..."; Flags: waituntilterminated; Check: not IsWindowsAppRuntimeInstalled
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function IsDotNetDesktop8Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{autopf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec);
  if Result then FindClose(FindRec);
end;

function IsWindowsAppRuntimeInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$runtime = Get-AppxPackage -Name ''Microsoft.WindowsAppRuntime.2'' -ErrorAction SilentlyContinue | Where-Object { [version]$_.Version -ge [version]''2.3.0.0'' } | Select-Object -First 1; if ($runtime) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;
