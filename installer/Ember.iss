; Ember – PC temperature monitor
; Build with tools\build-installer.ps1 (publishes the app, then compiles this script).

#define AppName "Ember"
#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif
#define AppExe "Ember.exe"
#define PublishDir "..\publish"
#define TaskName "Ember Temperature Monitor"
#define PawnIOUrl "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe"

[Setup]
AppId={{3E143F93-7566-40BA-A9D1-4E310812FA78}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Conner Killian
AppPublisherURL=https://github.com/Conner-Killian/ember-monitor
AppSupportURL=https://github.com/Conner-Killian/ember-monitor/issues
AppUpdatesURL=https://github.com/Conner-Killian/ember-monitor/releases
AppCopyright=Copyright (c) 2026 Conner Killian. MIT License.
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Ember-Setup-{#AppVersion}
SetupIconFile=..\src\Ember\Assets\ember.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=110
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nEmber shows your CPU, GPU, RAM and drive temperatures in a small desktop overlay and a full dashboard.%n%nIt is recommended that you close all other applications before continuing.

[Tasks]
Name: "pawnio"; Description: "Install the PawnIO driver (needed for CPU, RAM and motherboard temperatures, ~1 MB download)"; GroupDescription: "Sensor driver:"; Check: not PawnIOInstalled
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Registers the scheduled tasks that let Ember start without a UAC prompt.
Filename: "{app}\{#AppExe}"; Parameters: "--register"; Flags: runhidden waituntilterminated; StatusMsg: "Setting up Ember…"
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
; In-app updates run this installer silently with /RELAUNCH=show or /RELAUNCH=tray: start the new version.
Filename: "{app}\{#AppExe}"; Parameters: "{code:RelaunchArgs}"; Flags: nowait; Check: RelaunchRequested

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--exit"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "ExitEmber"
Filename: "{app}\{#AppExe}"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "RemoveTasks"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#TaskName}"" /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  PawnIODownloaded: Boolean;

function PawnIOInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\PawnIO');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the PawnIO driver installer…', nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('pawnio') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#PawnIOUrl}', 'PawnIO_setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        PawnIODownloaded := True;
      except
        PawnIODownloaded := False;
        if not WizardSilent then
          SuppressibleMsgBox('The PawnIO driver could not be downloaded:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
            'Ember will still install. You can install the driver later from Ember''s dashboard or from https://pawnio.eu',
            mbInformation, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|}') <> '';
end;

function RelaunchArgs(Param: String): String;
begin
  if CompareText(ExpandConstant('{param:relaunch|}'), 'tray') = 0 then
    Result := '--tray --updated'
  else
    Result := '--updated';
end;

// Ask a running copy of Ember to quit before files are replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ExePath: String;
begin
  Result := '';
  ExePath := ExpandConstant('{app}\{#AppExe}');
  if FileExists(ExePath) then
  begin
    Exec(ExePath, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(1500);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and PawnIODownloaded then
  begin
    WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver…';
    if not Exec(ExpandConstant('{tmp}\PawnIO_setup.exe'), '-install -silent', '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
       or not ((ResultCode = 0) or (ResultCode = 3010)) then
    begin
      if not WizardSilent then
        SuppressibleMsgBox('The PawnIO driver installer did not finish (code ' + IntToStr(ResultCode) + ').' + #13#10 +
          'You can retry from Ember''s dashboard later.', mbInformation, MB_OK, IDOK);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Suppressible: a silent uninstall (/SILENT, /VERYSILENT) keeps the settings without asking.
    if SuppressibleMsgBox('Also remove your Ember settings?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(ExpandConstant('{userappdata}\Ember'), True, True, True);
  end;
end;
