;
; 由 publish.ps1 调用，版本号与产物目录通过 /D 命令行传入，
; 不在这里写死——写死必然和 AssemblyVersion 漂移。
;
; 刻意不要求管理员权限：程序本身以普通用户运行，需要提权的只有
; 一次性系统配置，那由程序内部单独拉起提权子进程完成。
; 安装器要了管理员，反而会让装出来的快捷方式带上提权痕迹。

#if VER < EncodeVer(6,3,0)
  #error Inno Setup 6.3 or newer is required to compile this script.
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ParaDesk-" + AppVersion
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
AppMutex=Global\ParaDeskSingleInstance,Local\ParaDeskSingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#ifdef WithChinese
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[CustomMessages]
english.CreateDesktopIcon=Create a &desktop shortcut
english.LaunchApp=Launch {#AppName}
english.RequiresPro=ParaDesk needs Windows 10 1903 or later, Pro edition or higher.%n%nHome edition does not include the child-session feature and cannot run the parallel desktop.%n%nContinue anyway?
english.AppStillRunning=ParaDesk is still running from the installation folder, possibly inside the parallel desktop: its helper keeps running there after you exit ParaDesk from the tray, and keeps ParaDesk.exe in use.%n%nSign out of the parallel desktop first: sign out inside it, or open ParaDesk and choose "Close desktop" (this ends every program running in the parallel desktop). Then exit ParaDesk from the tray menu and click Retry.
#ifdef WithChinese
chinese.CreateDesktopIcon=创建桌面快捷方式(&D)
chinese.LaunchApp=运行 {#AppName}
chinese.RequiresPro=ParaDesk 需要 Windows 10 1903 或更高版本的专业版及以上。%n%n家庭版不含子会话功能，无法运行分身桌面。%n%n仍要继续安装吗？
chinese.AppStillRunning=ParaDesk 仍在运行（可能在分身桌面里：从托盘退出 ParaDesk 后，分身桌面里的守护仍在运行，占用着 ParaDesk.exe）。%n%n请先注销分身桌面：在分身桌面里注销，或打开 ParaDesk 点「关闭桌面」（会结束分身桌面里正在运行的所有程序）。然后从托盘菜单退出 ParaDesk，再点「重试」。
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
#ifdef SourceDirArm64
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "LICENSE.txt"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not UseArm64Build
Source: "{#SourceDirArm64}\*"; DestDir: "{app}"; Excludes: "LICENSE.txt"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: UseArm64Build
#else
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "LICENSE.txt"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

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
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupApprovedRunKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';

{ Arm64 版需要 .NET Framework 4.8.1（Release >= 533320）；没有的话装 AnyCPU 版，由系统以 x64 模拟运行。 }
function UseArm64Build(): Boolean;
var
  Release: Cardinal;
begin
  Result := False;
  if not IsArm64 then Exit;
  if RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) then
    Result := Release >= 533320;
end;

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

{ 有没有进程在用安装目录里的 ParaDesk.exe，不分会话。
  AppMutex 只看得见主程序；分身桌面里的子会话守护在另一个会话里运行、没有窗口，
  Restart Manager 也关不掉它，结果是复制时报"文件被占用"（选"忽略"会留下旧 exe 配新的 dll），
  或卸载后留下删不掉的 ParaDesk.exe。
  按进程名查 Win32_Process，路径在这里比较：把路径拼进 WQL 要转义反斜杠和撇号（用户名里可能有），容易写错。
  读不到路径的进程（别的账户的、提权的）跳过：ExecutablePath 为 Null，不先判断会抛类型转换异常。
  WMI 出错时放行、只记日志：检测是为了给出看得懂的提示，不能因为它自己出错把安装卡死。 }
function IsAppExeRunning(): Boolean;
var
  Locator, Service, Procs, Proc: Variant;
  AppExePath, ExePath: String;
  I, Count: Integer;
begin
  Result := False;
  AppExePath := ExpandConstant('{app}\{#AppExe}');
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Procs := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''{#AppExe}''');
    Count := Procs.Count;
    I := 0;
    while (not Result) and (I < Count) do
    begin
      Proc := Procs.ItemIndex(I);
      if not VarIsNull(Proc.ExecutablePath) then
      begin
        ExePath := Proc.ExecutablePath;
        if CompareText(ExePath, AppExePath) = 0 then
        begin
          Log('ParaDesk is still running from the installation folder: ' + ExePath);
          Result := True;
        end;
      end;
      I := I + 1;
    end;
  except
    Log('Could not check for running ParaDesk processes: ' + GetExceptionMessage);
  end;
end;

{ 有进程在用安装目录里的程序时反复提示，直到用户处理完点"重试"，或点"取消"放弃。
  不替用户注销分身桌面、也不结束守护：里面可能正有 agent 在干活，结束与否只能由用户决定。
  也不去执行 ParaDesk.exe --close：主程序已经被 AppMutex 请退，--close 找不到主程序，什么也不做。
  提示里要写"然后从托盘退出"：用户重新打开 ParaDesk 去关桌面后，主程序又在运行，同样会被检测到。
  用 SuppressibleMsgBox：静默安装加 /SUPPRESSMSGBOXES 时按"取消"处理，不会无限重试。 }
function WaitUntilAppClosed(): Boolean;
begin
  Result := True;
  while IsAppExeRunning() do
  begin
    if SuppressibleMsgBox(ExpandConstant('{cm:AppStillRunning}'), mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

{ 安装：放在 PrepareToInstall。此时安装目录已经确定（InitializeSetup 里还不能展开安装目录常量），
  文件还没开始复制，取消不会留下装了一半的程序。全新安装时目录里还没有程序，不必检测。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Exit;
  if not WaitUntilAppClosed() then
    Result := ExpandConstant('{cm:AppStillRunning}');
end;

{ 卸载：同样的检测，用户取消就不卸载。卸载程序自己从临时目录运行，不会把自己当成残留进程。 }
function InitializeUninstall(): Boolean;
begin
  Result := WaitUntilAppClosed();
end;

{ 程序运行时会在 HKCU\...\Run 下写两个启动项：ParaDesk（开机自启）与 ParaDeskChildAgent
  （分身桌面登录时拉起的子会话守护）；用户在任务管理器里禁用过的，还会在
  Explorer\StartupApproved\Run 下留一个同名的禁用标记。
  它们不属于用户数据（设置、方案、日志在 %LOCALAPPDATA%\ParaDesk，上面刻意不删），
  留着的话每次登录 Windows 都会尝试拉起一个已经删掉的程序，任务管理器"启动"页也会多出幽灵条目。

  只删指向本次卸载目录的启动项：用户可能另有一份绿色版在用，它的启动项不归这个卸载器管。
  Run 值已经不存在时，同名的禁用标记已经没有意义，一并清掉。
  管理员安装模式下卸载程序以提权身份运行，HKCU 是提权账户的；与安装者是同一账户（最常见的情况）时
  就是用户自己的注册表，不是同一账户时这里找不到这两个值，什么也不会删。 }
procedure RemoveStartupEntry(const ValueName: String);
var
  RunCommand: String;
  AppExePath: String;
begin
  AppExePath := Lowercase(ExpandConstant('{app}\{#AppExe}'));
  if RegQueryStringValue(HKCU, RunKey, ValueName, RunCommand) then
  begin
    if Pos(AppExePath, Lowercase(RunCommand)) = 0 then
    begin
      Log('Keep Run value ' + ValueName + ' (points elsewhere): ' + RunCommand);
      Exit;
    end;
    if RegDeleteValue(HKCU, RunKey, ValueName) then
      Log('Removed Run value ' + ValueName);
  end;
  if RegDeleteValue(HKCU, StartupApprovedRunKey, ValueName) then
    Log('Removed StartupApproved value ' + ValueName);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveStartupEntry('ParaDesk');
    RemoveStartupEntry('ParaDeskChildAgent');
  end;
end;
