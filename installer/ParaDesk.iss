; ParaDesk 安装包（Inno Setup 6）
;
; 由 publish.ps1 调用，版本号与产物目录通过 /D 命令行传入，
; 不在这里写死——写死必然和 AssemblyVersion 漂移。
;
; 刻意不要求管理员权限：程序本身以普通用户运行，需要提权的只有
; 一次性系统配置，那由程序内部单独拉起提权子进程完成。
; 安装器要了管理员，反而会让装出来的快捷方式带上提权痕迹。

#ifndef AppVersion
  #define AppVersion "1.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ParaDesk-1.0.0.0"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "ParaDesk"
#define AppPublisher "sinpoce"
#define AppExe "ParaDesk.exe"
#define AppUrl "https://github.com/sinpoce/ParaDesk"

[Setup]
AppId={{8F3A2C51-6E4D-4B7A-9C15-2D8E7F0A5B34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=ParaDesk-{#AppVersion}-Setup
SetupIconFile=..\src\ParaDesk.App\paradesk.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 普通用户装到自己的 AppData，管理员装到 Program Files——由用户在向导里选
PrivilegesRequiredOverridesAllowed=dialog
PrivilegesRequired=lowest
; 子会话依赖终端服务，只有 x64 的现代 Windows 才谈得上
ArchitecturesAllowed=x64compatible
MinVersion=10.0.18362

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.CreateDesktopIcon=Create a &desktop shortcut
english.LaunchApp=Launch {#AppName}
english.RequiresPro=ParaDesk needs Windows 10 1903 or later, Pro edition or higher.%n%nHome edition does not include the child-session feature and cannot run the parallel desktop.%n%nContinue anyway?
chinese.CreateDesktopIcon=创建桌面快捷方式(&D)
chinese.LaunchApp=运行 {#AppName}
chinese.RequiresPro=ParaDesk 需要 Windows 10 1903 或更高版本的专业版及以上。%n%n家庭版不含子会话功能，无法运行分身桌面。%n%n仍要继续安装吗？

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\ParaDesk.exe";        DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\ParaDesk.exe.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\Wpf.Ui.dll";          DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE";                       DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时不动 %LOCALAPPDATA%\ParaDesk：那里是用户的设置、方案和日志，
; 重装后还要用。真要清干净由用户自己删。

[Code]
{ 家庭版装了也跑不起来，装之前如实告知，但不强行拦——
  用户可能只是想先装上、之后升级系统版本。 }
function IsHomeEdition(): Boolean;
var
  Edition: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion',
                         'EditionID', Edition) then
    Result := Pos('Core', Edition) = 1;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsHomeEdition() then
    Result := MsgBox(ExpandConstant('{cm:RequiresPro}'), mbConfirmation, MB_YESNO) = IDYES;
end;
