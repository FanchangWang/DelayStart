; DelayStart.iss —— Inno Setup 6 安装脚本（D22）
;
; 编译：ISCC.exe installer\DelayStart.iss
; 前置：先发布三个产物（见 installer\README.md）：
;   1) 管理端（自包含）：dotnet publish src\DelayStart.App -c Release -r win-x64 --self-contained
;   2) 调度端（AOT）：  dotnet publish src\DelayStart.Scheduler -c Release
;   3) 代理（AOT）：    dotnet publish src\DelayStart.Agent -c Release
;
; D22 决策要点（详见 docs/requirements.md 决策表）：
;   · 固定安装路径 %LOCALAPPDATA%\Programs\DelayStart，不允许用户改（计划任务按固定路径注册，
;     用户自定义路径会让升级后的任务指向旧位置）。
;   · 不做 MSIX。首启的调度任务注册是幂等的，由程序自己完成，安装器只放文件。
;   · 卸载必须可逆：InitializeUninstall 里跑 --restore-all，退出码非 0 直接中止卸载
;     （🔴 不能写在 [UninstallRun] —— 那里读不到退出码，失败也照删文件）。
;   · 默认保留配置与日志：不加任何 [UninstallDelete]，卸载只动安装目录。

#define AppName "DelayStart"
#define AppNameZh "延时启动管理器"
#define AppVersion "0.1.0"
#define AppPublisher "DelayStart"
#define AppExe "DelayStart.exe"
#define SchedulerExe "DelayStart.Scheduler.exe"
#define AgentExe "DelayStart.Agent.exe"

[Setup]
AppId={{8F4C0B6A-2C1D-4E3B-9A5F-DELAYSTART001}
AppName={#AppNameZh}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
; 固定路径：禁止 "为所有用户安装"，也不允许改目录
PrivilegesRequired=lowest
OutputBaseFilename=DelayStart-Setup-{#AppVersion}
OutputDir=..\dist
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
; 关闭系统重启提示：本软件没有锁文件，重启无关紧要
RestartIfNeededByRun=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
; 管理端：自包含发布目录整个放入（~135 MB，239 文件）
Source: "..\src\DelayStart.App\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 调度端：AOT 单文件，放在安装根（调度任务按此路径注册）
Source: "..\src\DelayStart.Scheduler\bin\Release\net10.0-windows\win-x64\publish\{#SchedulerExe}"; DestDir: "{app}"; Flags: ignoreversion
; 普通用户代理：AOT 单文件，放在安装根（独立计划任务 \DelayStart\Agent 指向它，D38）
Source: "..\src\DelayStart.Agent\bin\Release\net10.0-windows\win-x64\publish\{#AgentExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppNameZh}"; Filename: "{app}\{#AppExe}"

[Run]
; 首启勾选项：装完直接打开管理端（调度任务由程序首启幂等注册，不归安装器管）
Filename: "{app}\{#AppExe}"; Description: "启动 {#AppNameZh}"; Flags: nowait postinstall skipifsilent

[Code]
// 卸载前的恢复动作（D22 / FR-2.7 / 9.3）。
// 🔴 必须在 InitializeUninstall 里同步等待并检查退出码：
//    --restore-all 失败说明还有条目没能还原成系统默认状态，此时删掉管理端
//    用户就永远失去"移出延时"的入口了 —— 所以中止卸载，让用户先手动处理。
function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if not FileExists(ExpandConstant('{app}\{#AppExe}')) then
    Exit; // 文件都不在了（手工删除过），无从恢复，照常卸载

  if Exec(
      ExpandConstant('{app}\{#AppExe}'),
      '--restore-all',
      ExpandConstant('{app}'),
      SW_SHOW,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    if ResultCode <> 0 then
    begin
      Result := False;
      if MsgBox(
          '恢复自启动项时出现问题（退出码 ' + IntToStr(ResultCode) + '）。' #13#10 +
          '为避免数据丢失，卸载已中止。请打开程序把「延时启动」页的条目逐一移出，' #13#10 +
          '或使用命令行 DelayStart.exe --restore-all 查看具体失败原因。' #13#10 #13#10 +
          '仍要强行卸载（跳过恢复，自启动项保持接管状态）吗？',
          mbConfirmation, MB_YESNO) = IDYES then
        Result := True;
    end;
  end
  else
  begin
    // Exec 本身失败（进程起不来）。给用户知情权，但默认放行卸载 ——
    // 程序已损坏时强行中止只会把用户锁死。
    if MsgBox(
        '无法运行恢复程序，卸载将继续。' #13#10 +
        '⚠ 已接管的条目不会被还原，如需还原请先修复程序再卸载。继续吗？',
        mbConfirmation, MB_YESNO) = IDNO then
      Result := False;
  end;
end;

// 默认保留配置与日志（%APPDATA%\DelayStart 与 %LOCALAPPDATA%\DelayStart）：
// 卸载重装后延时列表还在。用户要彻底清除时手动删这两个目录即可，
// 卸载完成页的提示负责把这件事说清楚。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    MsgBox(
      '配置与日志已保留：' #13#10 +
      '  %APPDATA%\DelayStart（延时列表与设置）' #13#10 +
      '  %LOCALAPPDATA%\DelayStart（日志与运行记录）' #13#10 #13#10 +
      '如需彻底清除，请手动删除上述目录。',
      mbInformation, MB_OK);
  end;
end;
