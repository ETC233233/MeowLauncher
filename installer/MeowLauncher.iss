; MeowLauncher 安装脚本（Inno Setup 6.7）
; 安装包不含运行库字节：缺 .NET 8 / Windows App Runtime 1.8 / WebView2 时，
; 在“准备安装”前用 Inno 自带下载页显示真实下载进度，随后静默安装。

#define AppName "MeowLauncher"
#define AppVer "0.5.0"
#define AppExe "MeowLauncher.WinUI.exe"
#define AppPublisher "MeowLauncher"
#define SourceDir "..\publish-slim"

[Setup]
AppId={{4C1F2D5A-9B7E-4A3C-8E21-6F0B7C9D2A11}
AppName={#AppName}
AppVersion={#AppVer}
AppVerName={#AppName} {#AppVer}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVer}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir=..\_export\releases
OutputBaseFilename={#AppName}-Setup-{#AppVer}
SetupIconFile=..\MeowLauncher.WinUI\Assets\Icons\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no
SetupLogging=yes

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[CustomMessages]
AppRun=启动 {#AppName}
TasksDesktop=创建桌面快捷方式
TasksGroup=附加快捷方式：
DlDotNet=正在下载 .NET 8 桌面运行时
DlAppRt=正在下载 Windows App Runtime 1.8
DlWebVw=正在下载 WebView2 运行时

[Tasks]
Name: "desktopicon"; Description: "{cm:TasksDesktop}"; GroupDescription: "{cm:TasksGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:AppRun}"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  AppRtUrl  = 'https://aka.ms/windowsappsdk/1.8/1.8.260921001/windowsappruntimeinstall-x64.exe';
  WebVwUrl  = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  DownloadPage: TDownloadWizardPage;
  NeedDotNet, NeedAppRt, NeedWebVw: Boolean;

function DotNet8Present: Boolean;
var
  F: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), F) then
  begin
    Result := True; FindClose(F); Exit;
  end;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.NETCore.App\8.*'), F) then
  begin
    Result := True; FindClose(F);
  end;
end;

{ 非打包应用要的是 1.8 框架 >= 8000.994.2142.0 且配套 DDLM；只查“框架包存在”会把
  系统自带的老 1.8 / 缺 DDLM 当已安装，启动器随后报
  'Required components of the Windows App Runtime are missing'。 }
function VerCmp(const A, B: String): Integer;
var
  I, P, Va, Vb: Integer;
  Sa, Sb: String;
begin
  Sa := A;
  Sb := B;
  Result := 0;
  for I := 1 to 4 do
  begin
    P := Pos('.', Sa);
    if P > 0 then begin Va := StrToIntDef(Copy(Sa, 1, P - 1), 0); Sa := Copy(Sa, P + 1, Length(Sa)); end
    else begin Va := StrToIntDef(Sa, 0); Sa := ''; end;
    P := Pos('.', Sb);
    if P > 0 then begin Vb := StrToIntDef(Copy(Sb, 1, P - 1), 0); Sb := Copy(Sb, P + 1, Length(Sb)); end
    else begin Vb := StrToIntDef(Sb, 0); Sb := ''; end;
    if Va <> Vb then
    begin
      if Va < Vb then Result := -1 else Result := 1;
      Exit;
    end;
  end;
end;

{ 从 AppModel 仓库里找前缀匹配的最高版本号；Stop 是版本号后面的分隔符 }
function PkgMaxVersion(const Prefix, Stop: String): String;
var
  Names: TArrayOfString;
  I, J, Roots, R: Integer;
  S: String;
begin
  Result := '';
  for R := 0 to 1 do
  begin
    if R = 0 then Roots := HKCU else Roots := HKLM;
    if RegGetSubkeyNames(Roots, 'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages', Names) then
      for I := 0 to GetArrayLength(Names) - 1 do
      begin
        S := Lowercase(Names[I]);
        if Pos(Prefix, S) = 1 then
        begin
          S := Copy(S, Length(Prefix) + 1, Length(S));
          J := Pos(Stop, S);
          if J > 0 then S := Copy(S, 1, J - 1);
          if (Result = '') or (VerCmp(S, Result) > 0) then Result := S;
        end;
      end;
  end;
end;

{ 非打包应用要 1.8 框架 >= 8000.994.2142.0 且配套 DDLM；只看包名会把系统自带的老 1.8 当已装 }
function AppRuntimeVersion: String;
begin
  Result := PkgMaxVersion('microsoft.windowsappruntime.1.8_', '_');
end;

function AppRuntimePresent: Boolean;
var
  Fw, Ddlm: String;
begin
  Fw := AppRuntimeVersion;
  Ddlm := PkgMaxVersion('microsoft.winappruntime.ddlm.', '-');
  Result := (Fw <> '') and (VerCmp(Fw, '8000.994.2142.0') >= 0) and
            (Ddlm <> '') and (VerCmp(Ddlm, '8000.994.2142.0') >= 0);
end;

function WebView2Present: Boolean;
var
  V: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then
    Exit;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', V) then
    Exit;
  Result := False;
end;

procedure RefreshNeeds;
begin
  NeedDotNet := not DotNet8Present;
  NeedAppRt  := not AppRuntimePresent;
  NeedWebVw  := not WebView2Present;
  Log('MeowRuntime: dotnet8=' + IntToStr(Ord(not NeedDotNet)) +
      ' appruntime18=' + IntToStr(Ord(not NeedAppRt)) +
      ' webview2=' + IntToStr(Ord(not NeedWebVw)) +
      ' user=' + GetUserNameString);
end;

procedure ShowStep(const Text: String);
begin
  WizardForm.StatusLabel.Font.Size := 11;
  WizardForm.StatusLabel.Font.Style := [fsBold];
  WizardForm.StatusLabel.Caption := Text;
  WizardForm.StatusLabel.Visible := True;
  WizardForm.ProgressGauge.Style := npbstMarquee;
  WizardForm.ProgressGauge.Visible := True;
  WizardForm.Refresh;
  WizardForm.Update;
end;

function RunSilent(const FileName, Params: String): String;
var
  Code: Integer;
begin
  Result := '';
  Log('RunSilent: ' + FileName + ' ' + Params + ' exists=' + IntToStr(Ord(FileExists(FileName))));
  if not FileExists(FileName) then
  begin
    Result := '找不到下载好的安装文件，请重新运行安装程序。';
    Exit;
  end;
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Result := '无法启动 ' + ExtractFileName(FileName) + '。';
    Exit;
  end;
  Log('RunSilent exit=' + IntToStr(Code));
  if (Code = 5) or (Code = -1073740791) then
  begin
    Result := '安装运行库需要管理员权限：请关闭安装程序，右键选择「以管理员身份运行」后重试。';
    Exit;
  end;
  if (Code <> 0) and (Code <> 3010) and (Code <> 1638) then
    Result := Format('%s 安装失败，返回错误码 %d。', [ExtractFileName(FileName), Code]);
end;

function RuntimeMemo(const Space, NewLine: String): String;
var
  Inst, Miss: String;
begin
  Inst := '';
  Miss := '';
  if DotNet8Present then
    Inst := Inst + NewLine + Space + Space + '.NET 8 桌面运行时 (x64)'
  else
    Miss := Miss + NewLine + Space + Space + '.NET 8 桌面运行时 (x64)';
  if AppRuntimePresent then
    Inst := Inst + NewLine + Space + Space + 'Windows App Runtime 1.8 (x64) ' + AppRuntimeVersion
  else
    Miss := Miss + NewLine + Space + Space + 'Windows App Runtime 1.8 (x64)（需要 8000.994.2142.0 及以上）';
  if WebView2Present then
    Inst := Inst + NewLine + Space + Space + 'WebView2 运行时 (x64)'
  else
    Miss := Miss + NewLine + Space + Space + 'WebView2 运行时 (x64)';
  Result := NewLine + NewLine + '运行库：';
  if Miss <> '' then
    Result := Result + NewLine + Space + '待安装：' + Miss;
  if (Miss <> '') and (Inst <> '') then
    Result := Result + NewLine;
  if Inst <> '' then
    Result := Result + NewLine + Space + '已安装：' + Inst;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  Result := Result + RuntimeMemo(Space, NewLine);
end;

procedure InitializeWizard;
begin
  RefreshNeeds;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;

end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;
  RefreshNeeds;
  if not (NeedDotNet or NeedAppRt or NeedWebVw) then Exit;

  DownloadPage.Clear;
  if NeedDotNet then DownloadPage.Add(DotNetUrl, 'windowsdesktop-runtime-8.0-x64.exe', '');
  if NeedAppRt then DownloadPage.Add(AppRtUrl, 'WindowsAppRuntimeInstall-x64.exe', '');
  if NeedWebVw then DownloadPage.Add(WebVwUrl, 'MicrosoftEdgeWebView2Setup.exe', '');

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      MsgBox('运行库下载失败：' + GetExceptionMessage + #13#10 + '请检查网络连接后重试。', mbError, MB_OK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: String;
begin
  Result := '';
  RefreshNeeds;

  if NeedDotNet then
  begin
    ShowStep('正在安装 .NET 8 桌面运行时，请稍候…');
    R := RunSilent(ExpandConstant('{tmp}\windowsdesktop-runtime-8.0-x64.exe'), '/install /quiet /norestart');
    if R <> '' then begin Result := R; Exit; end;
  end;
  if NeedAppRt then
  begin
    ShowStep('正在安装 Windows App Runtime 1.8，请稍候…');
    R := RunSilent(ExpandConstant('{tmp}\WindowsAppRuntimeInstall-x64.exe'), '--quiet');
    if R <> '' then begin Result := R; Exit; end;
  end;
  if NeedWebVw then
  begin
    ShowStep('正在安装 WebView2 运行时，请稍候…');
    R := RunSilent(ExpandConstant('{tmp}\MicrosoftEdgeWebView2Setup.exe'), '/silent /install');
    if R <> '' then begin Result := R; Exit; end;
  end;

  RefreshNeeds;
  if NeedAppRt then
    Result := 'Windows App Runtime 1.8 仍不满足要求（需 8000.994.2142.0 及以上；当前框架 ' +
      AppRuntimeVersion + '，DDLM ' + PkgMaxVersion('microsoft.winappruntime.ddlm.', '-') +
      '）。请重启电脑后再运行安装程序。'
  else if NeedDotNet then
    Result := '.NET 8 桌面运行时安装后仍没有检测到，请重启电脑后再运行安装程序。'
  else if NeedWebVw then
    Result := 'WebView2 运行时安装后仍没有检测到，请重启电脑后再运行安装程序。'
  else
    Result := '';
end;
