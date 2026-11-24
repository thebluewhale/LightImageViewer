; Inno Setup 7.1.1. Build-Installer.ps1 packages the existing portable executable.
; Overrides are used by Test-Installer.ps1 to isolate its install and settings data.
#ifndef RepoRoot
  #define RepoRoot SourcePath + ".."
#endif
#ifndef ApplicationId
  #define ApplicationId "LightImageViewer"
#endif
#ifndef ApplicationName
  #define ApplicationName "LightImageViewer"
#endif
#ifndef InstallFolder
  #define InstallFolder "{commonpf32}\LightImageViewer"
#endif
#ifndef SettingsFolder
  #define SettingsFolder "{localappdata}\LightImageViewer"
#endif
#ifndef StartMenuFolder
  #define StartMenuFolder "LightImageViewer"
#endif
#ifndef RequiredPrivileges
  #define RequiredPrivileges "admin"
#endif
#ifndef ApplicationRegistrationName
  #define ApplicationRegistrationName "LightImageViewer.exe"
#endif
#ifndef AssociationExtensionPrefix
  #define AssociationExtensionPrefix ""
#endif
#define ImageProgId ApplicationId + ".Image"
#define CapabilitiesKey "Software\" + ApplicationId + "\Capabilities"
#define AppExePath RepoRoot + "\dist\LightImageViewer.exe"
#define AppVersion GetVersionNumbersString(AppExePath)
#define AppDisplayVersion GetStringFileInfo(AppExePath, "ProductVersion")
#ifndef OutputFilename
  #define OutputFilename "LightImageViewer-Setup-" + GetStringFileInfo(AppExePath, "ProductVersion")
#endif

[Setup]
AppId={#ApplicationId}
AppName={#ApplicationName}
AppVersion={#AppDisplayVersion}
AppPublisher=LightImageViewer
AppVerName={#ApplicationName} {#AppDisplayVersion}
DefaultDirName={#InstallFolder}
DefaultGroupName={#StartMenuFolder}
PrivilegesRequired={#RequiredPrivileges}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
AllowNoIcons=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
Uninstallable=yes
UninstallDisplayName={#ApplicationName}
UninstallDisplayIcon={app}\LightImageViewer.exe
UninstallFilesDir={app}
CreateUninstallRegKey=yes
ChangesAssociations=yes
CloseApplications=yes
CloseApplicationsFilter=LightImageViewer.exe
RestartApplications=no
OutputDir={#RepoRoot}\dist
OutputBaseFilename={#OutputFilename}
SetupIconFile={#RepoRoot}\src\ImageViewer\Assets\app.ico
WizardStyle=modern
Compression=lzma2/normal
SolidCompression=yes
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription=LightImageViewer installer

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#AppExePath}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#StartMenuFolder}\LightImageViewer"; Filename: "{app}\LightImageViewer.exe"; WorkingDir: "{app}"; AppUserModelID: "{#ApplicationId}"

[Registry]
; Declare candidates only. Never write extension defaults or Windows UserChoice.
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#ApplicationId}"; ValueData: "{#CapabilitiesKey}"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\{#ApplicationId}"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "{#CapabilitiesKey}"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#ApplicationId}"; Flags: uninsdeletekey
Root: HKA; Subkey: "{#CapabilitiesKey}"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "LightImageViewer image viewer"
Root: HKA; Subkey: "{#CapabilitiesKey}"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\LightImageViewer.exe,0"
Root: HKA; Subkey: "Software\Classes\{#ImageProgId}"; ValueType: string; ValueName: ""; ValueData: "LightImageViewer Image"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ImageProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\LightImageViewer.exe,0"
Root: HKA; Subkey: "Software\Classes\{#ImageProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LightImageViewer.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\{#ImageProgId}\Application"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#ApplicationName}"
Root: HKA; Subkey: "Software\Classes\{#ImageProgId}\Application"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\LightImageViewer.exe,0"
Root: HKA; Subkey: "Software\Classes\Applications\{#ApplicationRegistrationName}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#ApplicationName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#ApplicationRegistrationName}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\LightImageViewer.exe"" ""%1"""
; Generated from ImageCatalog.Extensions by both build and test scripts.
#include RepoRoot + "\artifacts\installer\FileAssociations.iss"

[Run]
Filename: "{app}\LightImageViewer.exe"; Description: "{cm:LaunchProgram,LightImageViewer}"; Flags: nowait postinstall skipifsilent unchecked

[CustomMessages]
korean.OpenDefaultApps=기본 앱 설정 열기(&D)
english.OpenDefaultApps=Open &default app settings
korean.DefaultAppsHint=Windows 설정에서 LightImageViewer로 열 이미지 확장자를 선택하세요.
english.DefaultAppsHint=Choose which image extensions to open with LightImageViewer in Windows Settings.
korean.DefaultAppsOpenFailed=Windows 설정을 열지 못했습니다. 설정 → 앱 → 기본 앱에서 LightImageViewer를 찾아주세요.
english.DefaultAppsOpenFailed=Could not open Windows Settings. Go to Settings > Apps > Default apps and find LightImageViewer.
korean.SettingsQuestion=현재 제거 프로세스 계정의 창 크기·위치·보기 모드 설정과 오류 로그도 삭제하시겠습니까?%n%n포터블 EXE와 설치 버전은 같은 설정을 사용합니다.%n'예'를 선택하면 포터블 버전의 설정도 초기화됩니다.%n%n예: 설정과 오류 로그 삭제%n아니요: 설정 유지 (권장)%n%n이미지 파일은 삭제하지 않습니다.
english.SettingsQuestion=Also delete saved window/view mode settings and the error log for the account running this uninstaller?%n%nThe portable EXE and installed app share these settings.%nChoosing Yes also resets settings for the portable app.%n%nYes: Delete settings and the error log%nNo: Keep settings (recommended)%n%nYour image files will not be deleted.
korean.SettingsDeleteFailed=프로그램 제거는 완료되었지만 일부 설정 파일을 삭제하지 못했습니다. 실행 중인 LightImageViewer를 종료한 뒤 다음 폴더를 확인하세요:
english.SettingsDeleteFailed=The program was removed, but some settings files could not be deleted. Close any running LightImageViewer and check this folder:

[Code]
var
  DeleteUserSettings: Boolean;
  DefaultAppsButton: TNewButton;

function DefaultAppsUri: String;
var
  Version: TWindowsVersion;
begin
  Result := 'ms-settings:defaultapps';
  GetWindowsVersionEx(Version);
  if (Version.Major >= 10) and (Version.Build >= 22000) then
  begin
    { Production and isolated test IDs contain URI-safe ASCII characters only. }
    if IsAdminInstallMode then
      Result := Result + '?registeredAppMachine={#ApplicationId}'
    else
      Result := Result + '?registeredAppUser={#ApplicationId}';
  end;
end;

function TryOpenDefaultApps(const Uri: String): Boolean;
var
  ErrorCode: Integer;
begin
  Result := False;
  Log('Opening default apps settings: ' + Uri);
  try
    { Settings must run as the initiating user, not the elevated installer. }
    Result := ShellExecAsOriginalUser('open', Uri, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    if not Result then
      Log('Default apps settings launch failed: ' + IntToStr(ErrorCode));
  except
    Log('Default apps settings launch exception: ' + GetExceptionMessage);
  end;
end;

procedure OpenDefaultAppsClick(Sender: TObject);
var
  Uri: String;
  Opened: Boolean;
begin
  if WizardSilent or (WizardForm.CurPageID <> wpFinished) then Exit;
  DefaultAppsButton.Enabled := False;
  try
    Uri := DefaultAppsUri;
    Opened := TryOpenDefaultApps(Uri);
    if (not Opened) and (Uri <> 'ms-settings:defaultapps') then
      Opened := TryOpenDefaultApps('ms-settings:defaultapps');
    if not Opened then
      MsgBox(CustomMessage('DefaultAppsOpenFailed'), mbInformation, MB_OK);
  finally
    DefaultAppsButton.Enabled := True;
  end;
end;

procedure InitializeWizard;
begin
  DefaultAppsButton := TNewButton.Create(WizardForm);
  DefaultAppsButton.Parent := WizardForm;
  DefaultAppsButton.Caption := CustomMessage('OpenDefaultApps');
  DefaultAppsButton.Hint := CustomMessage('DefaultAppsHint');
  DefaultAppsButton.ShowHint := True;
  DefaultAppsButton.Left := ScaleX(12);
  DefaultAppsButton.Top := WizardForm.NextButton.Top;
  DefaultAppsButton.Width := ScaleX(210);
  DefaultAppsButton.Height := WizardForm.NextButton.Height;
  DefaultAppsButton.Anchors := [akLeft, akBottom];
  DefaultAppsButton.Visible := False;
  DefaultAppsButton.OnClick := @OpenDefaultAppsClick;
  Log('Default apps settings target: ' + DefaultAppsUri);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  DefaultAppsButton.Visible := (CurPageID = wpFinished) and (not WizardSilent);
  if DefaultAppsButton.Visible then
  begin
    WizardForm.BackButton.Visible := False;
    DefaultAppsButton.Top := WizardForm.NextButton.Top;
    DefaultAppsButton.Height := WizardForm.NextButton.Height;
    if DefaultAppsButton.Left + DefaultAppsButton.Width > WizardForm.NextButton.Left - ScaleX(12) then
      DefaultAppsButton.Width := WizardForm.NextButton.Left - ScaleX(12) - DefaultAppsButton.Left;
    DefaultAppsButton.BringToFront;
  end;
end;

function DeleteSettingsFile(const FileName: String): Boolean;
begin
  Result := True;
  if FileExists(FileName) then
    Result := DeleteFile(FileName);
  if not Result then
    Log('Unable to remove settings file: ' + FileName);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  SettingsPath: String;
  SettingsDeleted, LogDeleted: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Silent uninstalls retain settings unless explicitly told otherwise. }
    if UninstallSilent then
      DeleteUserSettings := ExpandConstant('{param:DELETESETTINGS|0}') = '1'
    else
      DeleteUserSettings :=
        MsgBox(CustomMessage('SettingsQuestion') + #13#10#13#10 +
          ExpandConstant('{#SettingsFolder}'), mbConfirmation,
          MB_YESNO or MB_DEFBUTTON2) = IDYES;
    if DeleteUserSettings then
      Log('Settings removal explicitly selected')
    else
      Log('Preserving user settings');
  end;

  if (CurUninstallStep = usPostUninstall) and DeleteUserSettings then
  begin
    SettingsPath := ExpandConstant('{#SettingsFolder}');
    { Delete only known app-owned files; never recursively remove a directory. }
    SettingsDeleted := DeleteSettingsFile(AddBackslash(SettingsPath) + 'window.json');
    LogDeleted := DeleteSettingsFile(AddBackslash(SettingsPath) + 'error.log');
    if SettingsDeleted and LogDeleted then
      RemoveDir(SettingsPath)  { Succeeds only when the directory is empty. }
    else if not UninstallSilent then
      MsgBox(CustomMessage('SettingsDeleteFailed') + #13#10 + SettingsPath,
        mbInformation, MB_OK);
  end;
end;
