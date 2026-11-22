; © 2026 工控菜鸡刘 · ModbusTool · MIT License
; ModbusTool Inno Setup 6 安装脚本
; 由 scripts/publish.ps1 -Installer 调用，通过 /D 传入变体、版本与源目录。
; net8 安装包要求 Windows 10 1809+；net6 安装包支持 Windows 7 SP1+。

#ifndef AppVariant
  #define AppVariant "net8"
#endif

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\net8-framework-dependent"
#endif

#ifndef OutputDir
  #define OutputDir "..\publish"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "ModbusTool-setup"
#endif

#ifndef MinWindows
  #define MinWindows "10.0.17763"
#endif

#ifndef RuntimeMajor
  #define RuntimeMajor "8.0"
#endif

#ifndef RuntimeName
  #define RuntimeName ".NET 8 Desktop Runtime"
#endif

#define MyAppName "ModbusTool"
#define MyAppPublisher "工控菜鸡刘"
#define MyAppURL "https://github.com/dabaogg699/ModbusTool"

[Setup]
; net6 与 net8 使用不同 AppId，避免同一台机器上两个安装包互相覆盖。
#if AppVariant == "net6"
AppId={{2C9A5E71-6B4D-4A83-8F0E-5D7C1B3A9E42}
#else
AppId={{6D1B7F3E-3A5C-4C0E-9C2B-8F1D2A7E4B10}
#endif
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\ModbusTool
DefaultGroupName=ModbusTool
DisableProgramGroupPage=no
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion={#MinWindows}
UninstallDisplayIcon={app}\ModbusMaster.exe
UninstallDisplayName={#MyAppName} {#AppVersion}
SetupIconFile=..\src\ModbusMasterApp\Assets\app.ico
LicenseFile=..\LICENSE

[Languages]
; 若本机 Inno Setup 6 的 Languages 目录缺少 ChineseSimplified.isl，可删除本段改用默认语言。
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked
Name: "associate"; Description: "关联 .mbwork 工作区文件"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ModbusMaster"; Filename: "{app}\ModbusMaster.exe"; WorkingDir: "{app}"
Name: "{group}\ModbusSlave"; Filename: "{app}\ModbusSlave.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 ModbusTool"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ModbusMaster"; Filename: "{app}\ModbusMaster.exe"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autodesktop}\ModbusSlave"; Filename: "{app}\ModbusSlave.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.mbwork"; ValueType: string; ValueName: ""; ValueData: "ModbusTool.Workspace"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ModbusTool.Workspace"; ValueType: string; ValueName: ""; ValueData: "ModbusTool 工作区"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ModbusTool.Workspace\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\ModbusMaster.exe,0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ModbusTool.Workspace\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\ModbusMaster.exe"" ""%1"""; Tasks: associate

[Run]
Filename: "{app}\ModbusMaster.exe"; Description: "启动 ModbusMaster"; Flags: nowait postinstall skipifsilent
Filename: "{app}\ModbusSlave.exe"; Description: "启动 ModbusSlave"; Flags: nowait postinstall skipifsilent unchecked

[Code]
var
  DownloadPage: string;

function RuntimeInstalled(const Major: string): Boolean;
var
  RuntimeDir: string;
  TempFile: string;
  Lines: TArrayOfString;
  I: Integer;
  ResultCode: Integer;
begin
  Result := False;
  RuntimeDir := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(RuntimeDir) then
    Exit;

  TempFile := ExpandConstant('{tmp}\modbustool-dotnet-runtimes.txt');
  if not Exec(ExpandConstant('{sys}\cmd.exe'),
              '/c dir /b "' + RuntimeDir + '" > "' + TempFile + '" 2>nul',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Exit;

  if not LoadStringsFromFile(TempFile, Lines) then
    Exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Pos(Major + '.', Trim(Lines[I])) = 1 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if RuntimeInstalled('{#RuntimeMajor}') then
    Exit;

  if MsgBox('未检测到 {#RuntimeName}。' + #13#10 + #13#10 +
            '本安装包为框架依赖版本，需要先安装对应运行时。' + #13#10 +
            '是否现在打开官方下载页面？',
            mbConfirmation, MB_YESNO) = IDYES then
  begin
    if '{#AppVariant}' = 'net6' then
      DownloadPage := 'https://dotnet.microsoft.com/download/dotnet/6.0'
    else
      DownloadPage := 'https://dotnet.microsoft.com/download/dotnet/8.0';
    ShellExec('open', DownloadPage, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;

  Result := False;
end;
