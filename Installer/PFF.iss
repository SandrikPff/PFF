#define AppVersion "5.4.0"

[Setup]
AppId={{BA54E22C-7D43-4A30-AC35-00C80D5537AE}
AppName=PFF
AppVerName=PFF
UninstallDisplayName=PFF
VersionInfoProductName=PFF
VersionInfoDescription=PFF Setup
AppVersion={#AppVersion}
DefaultDirName={autopf}\PFF
DefaultGroupName=PFF
UsePreviousGroup=no
OutputDir=output
OutputBaseFilename=PFF_Setup
SetupIconFile=..\CrimsonPanel\Assets\pff.ico
UninstallDisplayIcon={app}\PFF.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.22000
PrivilegesRequired=admin
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "..\install-mic-driver.ps1"; Flags: dontcopy noencryption
Source: "..\publish\PFF.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY.txt"; DestDir: "{app}"
Source: "..\README.md"; DestDir: "{app}"
Source: "..\install-mic-driver.ps1"; DestDir: "{app}"

[InstallDelete]
Type: files; Name: "{app}\Pff_crimson.exe"
Type: files; Name: "{autoprograms}\Pff_crimson.lnk"
Type: files; Name: "{autodesktop}\Pff_crimson.lnk"

[Icons]
Name: "{autoprograms}\PFF"; Filename: "{app}\PFF.exe"
Name: "{autodesktop}\PFF"; Filename: "{app}\PFF.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; Flags: unchecked
Name: "virtualmic"; Description: "Установить VB-CABLE для эквалайзера микрофона"

[Run]
Filename: "{app}\PFF.exe"; Description: "Запустить PFF"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{app}\PFF.exe"; Parameters: "--detach-apo"; Flags: waituntilterminated; RunOnceId: "PffCrimsonDetachAudio"

[Code]
const
  ApoKey = 'SOFTWARE\EqualizerAPO';
  ApoFile = 'EqualizerAPO-XT-Setup.exe';
  ApoUrl = 'https://github.com/115dkk/EqualizerAPO-XT/releases/download/v2.63.2/EqualizerAPO-XT-Setup.exe';
  ApoHash = '3a547db56b607bf1845c74f13dd39736841082a550019de038d97aea9e944337';
  ReaFile = 'reaplugs236_x64-install.exe';
  ReaUrl = 'https://www.reaper.fm/reaplugs/reaplugs236_x64-install.exe';
  ReaHash = '1654f1f78d746fc87a6f6d078a8f928ea61c7b49997b1f2c07dfc870f8fa6c10';

var
  DownloadPage: TDownloadWizardPage;
  RestartAfterAudio: Boolean;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Аудиокомпоненты', 'Загрузка и проверка файлов', nil);
end;

function ApoXtPresent: Boolean;
var
  InstallDir, ConfigDir: String;
begin
  if not RegQueryStringValue(HKLM64, ApoKey, 'InstallPath', InstallDir) or (InstallDir = '') then
  begin
    Result := False;
    Exit;
  end;
  if not RegQueryStringValue(HKLM64, ApoKey, 'ConfigPath', ConfigDir) or (ConfigDir = '') then
    ConfigDir := AddBackslash(InstallDir) + 'config';
  Result := FileExists(AddBackslash(InstallDir) + 'EqualizerAPO.dll') and
    FileExists(AddBackslash(InstallDir) + 'EqualizerAPOHost.exe') and
    FileExists(AddBackslash(InstallDir) + 'DeviceSelector.exe') and
    FileExists(AddBackslash(ConfigDir) + 'config.txt');
end;

function ReaPresent: Boolean;
begin
  Result := FileExists(ExpandConstant('{pf64}\Pff_crimson Audio\reacomp-standalone.dll')) or
    FileExists(ExpandConstant('{pf64}\VSTPlugins\ReaPlugs\reacomp-standalone.dll')) or
    FileExists(ExpandConstant('{pf64}\VSTPlugins\reacomp-standalone.dll'));
end;

function InstallDependency(const FileName, Args: String): Integer;
begin
  if not Exec(ExpandConstant('{tmp}\' + FileName), Args, ExpandConstant('{tmp}'),
              SW_SHOWNORMAL, ewWaitUntilTerminated, Result) then
    RaiseException('Не удалось запустить ' + FileName + '. Код Windows ' + IntToStr(Result));
  if (Result <> 0) and (Result <> 3010) then
    RaiseException(FileName + ' завершился с кодом ' + IntToStr(Result) + '.');
end;

function RunCableHelper(const ExtraArgs: String): Integer;
var
  LogFile: String;
  Lines: TArrayOfString;
  Index: Integer;
  Details: String;
begin
  LogFile := ExpandConstant('{tmp}\pff-vb-cable-error.txt');
  DeleteFile(LogFile);
  if not Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\install-mic-driver.ps1') + '" -ErrorLog "' + LogFile + '"' + ExtraArgs,
    ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Result) then
    RaiseException('Не удалось запустить установку VB-CABLE. Код Windows ' + IntToStr(Result));

  Log('VB-CABLE helper exit code: ' + IntToStr(Result));
  if (Result <> 0) and (Result <> 10) and (Result <> 3010) then
  begin
    Details := '';
    if LoadStringsFromFile(LogFile, Lines) then
      for Index := 0 to GetArrayLength(Lines) - 1 do
      begin
        Log(Lines[Index]);
        if (Details = '') and (Trim(Lines[Index]) <> '') then
          Details := Lines[Index];
      end;
    if Details = '' then
      Details := 'Код завершения ' + IntToStr(Result) + '.';
    RaiseException('Не удалось установить VB-CABLE. ' + Details);
  end;
end;

procedure PrepareVirtualMicrophone;
var
  Status: Integer;
begin
  if not WizardIsTaskSelected('virtualmic') then
    Exit;
  ExtractTemporaryFile('install-mic-driver.ps1');
  Status := RunCableHelper(' -Check');
  if Status = 10 then
  begin
    if WizardSilent then
      RaiseException('Для установки VB-CABLE нужен интерактивный запуск PFF_Setup.exe. Для установки только PFF снимите пункт VB-CABLE.');
    MsgBox('В мастере VB-CABLE нажмите Install Driver. По окончании вернитесь в установщик PFF.', mbInformation, MB_OK);
    Status := RunCableHelper('');
    if Status = 10 then
      RaiseException('После установки VB-CABLE не обнаружен.');
  end;
  if Status = 3010 then
    RestartAfterAudio := True;
end;

function NeedRestart: Boolean;
begin
  Result := RestartAfterAudio;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  NeedApo, NeedRea: Boolean;
begin
  Result := '';
  try
    NeedApo := not ApoXtPresent;
    NeedRea := not ReaPresent;
    if NeedApo or NeedRea then
    begin
      DownloadPage.Clear;
      if NeedApo then
        DownloadPage.Add(ApoUrl, ApoFile, ApoHash);
      if NeedRea then
        DownloadPage.Add(ReaUrl, ReaFile, ReaHash);
      DownloadPage.Show;
      try
        DownloadPage.Download;
      finally
        DownloadPage.Hide;
      end;
    end;
    if NeedApo then
    begin
      if InstallDependency(ApoFile, '--silent') = 3010 then
        RestartAfterAudio := True;
      if not ApoXtPresent then
        RaiseException('Установка Equalizer APO-XT не завершена.');
    end;
    if NeedRea then
    begin
      if InstallDependency(ReaFile, '/S /D=' + ExpandConstant('{pf64}\Pff_crimson Audio')) = 3010 then
        RestartAfterAudio := True;
      if not ReaPresent then
        RaiseException('После установки не найден ReaComp x64.');
    end;
    PrepareVirtualMicrophone;
  except
    Result := 'Не удалось установить аудиокомпоненты. ' + GetExceptionMessage;
  end;
end;
