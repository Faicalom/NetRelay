#define MyAppName "Net Relay"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Net Relay"
#define MyAppExeName "NetRelay.exe"
#ifndef FinalWindowsDir
  #define FinalWindowsDir "..\FINAL_RELEASE\Windows"
#endif

#ifndef PrerequisiteProbe
  #define PrerequisiteProbe "prerequisites\WebView2Probe.exe"
#endif
#ifndef PublishRoot
  #define PublishRoot "..\FINAL_RELEASE\Windows\App"
#endif
#ifndef WebView2Installer
  #define WebView2Installer "prerequisites\MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
#endif

[Setup]
AppId={{6F761982-E6E9-410F-977B-408F82F37CD3}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={commonpf32}\Net Relay
DefaultGroupName=Net Relay
DisableProgramGroupPage=auto
OutputDir={#FinalWindowsDir}
OutputBaseFilename=NetRelay-Windows-1.0.0-Setup
SetupIconFile=..\windows\assets\NetRelay.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName=Net Relay
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Put prerequisite-only files first for efficient solid-block extraction.
Source: "{#WebView2Installer}"; Flags: dontcopy
Source: "{#PrerequisiteProbe}"; Flags: dontcopy
Source: "{#PublishRoot}\WebView2Loader.dll"; Flags: dontcopy
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Net Relay"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\Net Relay Uninstall"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Net Relay"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Net Relay"; WorkingDir: "{app}"; Flags: shellexec nowait postinstall skipifsilent; Check: CanLaunchNetRelay

[UninstallDelete]
Type: filesandordirs; Name: "{app}\NetRelay.exe.WebView2"

[Code]
const
  WebView2ClientGuid = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  PrerequisitesReady: Boolean;
  ProbeExtracted: Boolean;

function HasWebView2Version(const RootKey: Integer): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(
      RootKey,
      'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WebView2ClientGuid,
      'pv',
      Version) and
    (Version <> '') and
    (Version <> '0.0.0.0');
end;

function WebView2RuntimeReady: Boolean;
var
  ExitCode: Integer;
begin
  Result := (
    HasWebView2Version(HKLM32) or
    HasWebView2Version(HKLM64) or
    HasWebView2Version(HKCU));
  Log('NETRELAY_PREREQ WebView2 registered=' + IntToStr(Ord(Result)));
  if not Result then Exit;
  if not ProbeExtracted then
  begin
    ExtractTemporaryFile('WebView2Probe.exe');
    ExtractTemporaryFile('WebView2Loader.dll');
    ProbeExtracted := True;
  end;
  Result := Exec(ExpandConstant('{tmp}\WebView2Probe.exe'), '',
    ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode);
  Log('NETRELAY_PREREQ WebView2 loader probe exit=' + IntToStr(ExitCode));
  Result := Result and (ExitCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode, Attempt: Integer;
begin
  Result := '';
  PrerequisitesReady := False;
  try
    if WebView2RuntimeReady then
    begin
      PrerequisitesReady := True;
      Log('NETRELAY_PREREQ WebView2 ALREADY_PRESENT and loader verified');
      Exit;
    end;
    WizardForm.PreparingLabel.Caption := 'Installing Microsoft WebView2 Runtime...';
    ExtractTemporaryFile('MicrosoftEdgeWebView2RuntimeInstallerX64.exe');
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'),
      '/silent /install', ExpandConstant('{tmp}'), SW_HIDE,
      ewWaitUntilTerminated, ExitCode) then
    begin
      Log('NETRELAY_PREREQ installer could not execute; error=' + IntToStr(ExitCode));
      Result := 'Microsoft WebView2 Runtime could not be installed.' + #13#10 +
        'Net Relay requires this component for its interface. Please run Net Relay Setup again.';
      Exit;
    end;
    Log('NETRELAY_PREREQ offline installer exit=' + IntToStr(ExitCode));
    if (ExitCode = 3010) or (ExitCode = 1641) then
    begin
      NeedsRestart := True;
      Result := 'Windows must be restarted to finish installing Microsoft WebView2 Runtime.' + #13#10 +
        'Restart when convenient, then run Net Relay Setup again.';
      Exit;
    end;
    // Do not trust exit 0 alone; delayed registration is bounded to 30 seconds.
    // A nonzero exit may mean an existing/newer runtime; the API remains authoritative.
    for Attempt := 0 to 30 do
    begin
      if WebView2RuntimeReady then
      begin
        PrerequisitesReady := True;
        Log('NETRELAY_PREREQ WebView2 POST_INSTALL_VERIFIED');
        Exit;
      end;
      if Attempt < 30 then Sleep(1000);
    end;
    Result := 'Microsoft WebView2 Runtime could not be installed.' + #13#10 +
      'Net Relay requires this component for its interface. Please run Net Relay Setup again.';
  except
    Log('NETRELAY_PREREQ exception: ' + GetExceptionMessage);
    Result := 'Microsoft WebView2 Runtime could not be installed.' + #13#10 +
      'Net Relay requires this component for its interface. Please run Net Relay Setup again.';
  end;
end;

function CanLaunchNetRelay: Boolean;
begin
  Result := False;
  if not PrerequisitesReady then Exit;
  try
    Result := WebView2RuntimeReady;
  except
    Log('NETRELAY_PREREQ launch verification failed: ' + GetExceptionMessage);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LocalAppData: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'NetRelay');
    LocalAppData := GetEnv('LOCALAPPDATA');
    if LocalAppData <> '' then
      DelTree(
        AddBackslash(LocalAppData) + 'Net Relay\WebView2',
        True,
        True,
        True);
  end;
end;
