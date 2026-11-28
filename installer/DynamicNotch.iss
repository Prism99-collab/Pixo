; Pixo — Windows installer (Inno Setup 6)
; Built by scripts\build.ps1, which passes the version and the folder holding the published exes.
; Per-user install: no administrator prompt, installs to %LOCALAPPDATA%\Programs\Pixo.

#ifndef AppVersion
  #define AppVersion "6.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
; "x64" (Intel / AMD, also runs on ARM64 PCs through emulation) or "arm64" (native Snapdragon build)
#ifndef Arch
  #define Arch "x64"
#endif

#define AppName "Pixo"
#define AppExe "Pixo.exe"
#define AppPublisher "Prism"
#define AppUrl "https://github.com/Prism99-collab/Pixo"

[Setup]
; Never change AppId: it is how Windows recognises upgrades of the same app.
AppId={{CD4578A0-5D80-422D-9C75-1054B3DA5884}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; per-user, no admin rights needed
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
; it moves to its new folder (Pixo) on upgrade; the old one is cleared below
UsePreviousAppDir=no
AppCopyright=Copyright (c) 2026 Prism
DisableDirPage=auto
DisableReadyPage=no

; one installer per CPU type: x64 (Intel / AMD, also runs on ARM64 via emulation) and native ARM64 (Snapdragon)
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041

OutputDir={#OutputDir}
OutputBaseFilename=Pixo-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\src\DynamicNotch\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=100
; follow the PC's language (French or English) instead of asking
ShowLanguageDialog=auto
; the logo in the wizard's corner, in sizes for 100 / 125 / 150 / 200 % display scaling
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-69.bmp,wizard-small-83.bmp,wizard-small-110.bmp
Compression=lzma2/max
SolidCompression=yes

; close a running copy before replacing it (upgrades), and don't ask to restart Windows
CloseApplications=force
CloseApplicationsFilter={#AppExe},DynamicNotch.exe
RestartApplications=no
RestartIfNeededByRun=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; the app with its own .NET runtime: works on any Windows 10 / 11 PC, nothing else to install
Source: "{#PublishDir}\win-{#Arch}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; DestName: "THIRD-PARTY-NOTICES.txt"; Flags: ignoreversion
Source: "..\TRADEMARKS.md"; DestDir: "{app}"; DestName: "TRADEMARKS.txt"; Flags: ignoreversion
Source: "..\PRIVACY.md"; DestDir: "{app}"; DestName: "PRIVACY.txt"; Flags: ignoreversion

[InstallDelete]
; the name before Pixo: its folder, shortcuts and sign-in entries go
Type: filesandordirs; Name: "{autopf}\Dynamic Notch"
Type: files; Name: "{autoprograms}\Dynamic Notch.lnk"
Type: files; Name: "{autodesktop}\Dynamic Notch.lnk"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Open {#AppName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Open {#AppName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; closes the notch and removes its sign-in task (the app knows exactly what it registered)
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "DynamicNotchCleanup"

; your notes, chats, settings and keys stay unless you say so (asked at the end of the uninstall, "No" by default)

[Code]
function OpenEvent(Access: Cardinal; Inherit: LongBool; Name: String): THandle; external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): LongBool; external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): LongBool; external 'CloseHandle@kernel32.dll stdcall';

procedure StopRunningCopy();
var
  ResultCode: Integer;
  Quit: THandle;
begin
  { first a polite request, straight to the running notch (a version that listens for it): it saves everything and
    closes. Nothing is started for this, so an older version that doesn't listen is simply stopped below. }
  Quit := OpenEvent($0002 { EVENT_MODIFY_STATE }, False, 'Pixo.Quit');
  if Quit <> 0 then
  begin
    SetEvent(Quit);
    CloseHandle(Quit);
    Sleep(3000);
  end;
  { then make sure no copy of the notch keeps the exe locked while files are replaced }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM DynamicNotch.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure SetStartupTask(Enable: Boolean);
var
  ResultCode: Integer;
  Switch: String;
begin
  { the app's own sign-in / keep-alive task (it may not exist yet: then this does nothing) }
  if Enable then Switch := '/ENABLE' else Switch := '/DISABLE';
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Change /TN "Pixo" ' + Switch, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if not Enable then Exec(ExpandConstant('{sys}\schtasks.exe'), '/Change /TN "Dynamic Notch" /DISABLE', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { the keep-alive task relaunches the notch every 5 minutes: pause it so it can't lock the exe mid-upgrade }
  SetStartupTask(False);
  StopRunningCopy();
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then SetStartupTask(True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { your data is only deleted if you say so (a silent uninstall never deletes it) }
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
    if MsgBox('Also delete your Pixo data (notes, chats, clipboard, settings and keys)?' + #13#10 + #13#10 +
              'Choose No to keep it for a later reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\Pixo'), True, True, True);
      DelTree(ExpandConstant('{userappdata}\DynamicNotch'), True, True, True);
    end;
end;
