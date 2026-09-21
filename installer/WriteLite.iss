; ============================================================================
;  WriteLite — Windows installer
;
;  Not compiled by hand. build\build-release.ps1 publishes the application,
;  builds the bundled Java runtime, stages the payload and then calls:
;
;      ISCC.exe /DAppVersion=... /DSourceDir=... /DOutDir=... /DAssetsDir=... WriteLite.iss
;
;  The version comes from the published WriteLite.exe, so the installer and the
;  assembly cannot disagree. Raise it in Directory.Build.props.
; ----------------------------------------------------------------------------
;  Decisions worth recording:
;
;  1. PrivilegesRequired=lowest — a per-user install into
;     %LocalAppData%\Programs\WriteLite. Not laziness about UAC: the application
;     writes its compatibility log next to itself, which a Program Files install
;     would deny. Per-user keeps the app working and asks for no elevation.
;
;  2. User data lives in %LocalAppData%\WriteLite (settings, user dictionaries,
;     dictionary packs, ignore list) — a different tree from the install dir, so
;     uninstalling removes the program and never the user's own words. Removing
;     it is offered at uninstall time, off by default.
;
;  3. Autostart is written to the same HKCU Run value that
;     WriteLite.Services.Settings.WriteLiteAutostartService uses, under the same
;     value name, so the installer checkbox and the Settings page toggle are the
;     same switch rather than two that fight.
; ============================================================================

#ifndef AppVersion
  #error AppVersion must be passed with /DAppVersion=...
#endif
#ifndef SourceDir
  #error SourceDir must be passed with /DSourceDir=...
#endif
#ifndef OutDir
  #error OutDir must be passed with /DOutDir=...
#endif
#ifndef AssetsDir
  #define AssetsDir AddBackslash(SourcePath) + "assets"
#endif

#define AppName        "WriteLite"
; From NOTICE: «Copyright 2026 MisterHarryX». The copyright holder the project
; already declares — not an invented legal entity.
#define AppPublisher   "MisterHarryX"
#define AppUrl         "https://writelite-web.vercel.app"
#define AppExe         "WriteLite.exe"

; Must match WriteLiteAutostartService.RunValueName / RunKeyPath.
#define RunKey         "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValueName   "WriteLite"

; Must match WriteLiteSetupHandoff.KeyPath / AutostartValueName.
#define SetupKey       "Software\WriteLite\Setup"
#define AutostartValue "AutostartRequested"

[Setup]
; Never change AppId — it is what lets a new build upgrade an old one in place,
; and what «Установленные приложения» keys the entry on.
AppId={{7B3C1A64-9E2F-4D58-A0C7-3F5E8B21D9A4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} {#AppVersion} Setup
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
AppCopyright=Copyright (C) 2026 {#AppPublisher}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
; Inno 6 hides the welcome page unless asked. WriteLite wants it: it is where the
; product says what it is, and where an unsigned build warns about SmartScreen
; before Windows does.
DisableWelcomePage=no
; The Start Menu group is a [Tasks] checkbox instead of a wizard page, so the
; three optional extras all live in one place the user actually reads.
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

; Per-user only, and deliberately not offered as a choice.
; CompatibilityLogger writes to AppContext.BaseDirectory\logs — next to the
; executable. An all-users install lands in Program Files, where that write is
; denied, so «установить для всех пользователей» would offer a configuration
; that cannot log. PrivilegesRequiredOverridesAllowed is therefore left unset,
; which also removes the mode-choice dialog in front of the welcome page.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

OutputDir={#OutDir}
OutputBaseFilename=WriteLite-Setup-{#AppVersion}
SetupIconFile={#SourceDir}\assets\WriteLite.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}

; The payload is ~1.2 GB and most of it (GGUF weights, ONNX graphs, the language
; engine's jars, the jlink runtime) is already compressed, so lzma2/normal buys
; nearly all of the achievable ratio for a fraction of what max costs.
Compression=lzma2/normal
SolidCompression=no
LZMANumBlockThreads=4
DiskSpanning=no

; Refuse to install over a running copy and let Restart Manager offer to close it.
; The mutex name is the one WriteLite.App/Services/SingleInstanceService.cs creates;
; if that constant ever changes, change it here too.
AppMutex=Global\WriteLite.Application.SingleInstance
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

WizardStyle=modern
WizardImageFile={#AssetsDir}\wizard-164x314.bmp,{#AssetsDir}\wizard-192x386.bmp,{#AssetsDir}\wizard-256x459.bmp,{#AssetsDir}\wizard-384x689.bmp,{#AssetsDir}\wizard-497x892.bmp
WizardSmallImageFile={#AssetsDir}\wizard-small-55x58.bmp,{#AssetsDir}\wizard-small-92x97.bmp,{#AssetsDir}\wizard-small-110x116.bmp,{#AssetsDir}\wizard-small-138x140.bmp
WizardImageStretch=yes
ShowLanguageDialog=auto

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.DesktopIcon=Создать ярлык на рабочем столе
english.DesktopIcon=Create a desktop shortcut
russian.StartMenuIcon=Добавить WriteLite в меню «Пуск»
english.StartMenuIcon=Add WriteLite to the Start menu
russian.AutostartTask=Запускать WriteLite вместе с Windows
english.AutostartTask=Start WriteLite together with Windows

russian.LaunchApp=Запустить WriteLite
english.LaunchApp=Launch WriteLite

russian.DiskSpaceWarn=Для установки WriteLite нужно около 1,5 ГБ свободного места: в комплект входят локальная языковая модель, словари и среда выполнения.
english.DiskSpaceWarn=WriteLite needs about 1.5 GB of free space: the local language model, the dictionaries and the runtime are bundled.

russian.RemoveUserData=Удалить также пользовательские настройки и данные WriteLite
english.RemoveUserData=Also remove WriteLite user settings and data
russian.RemoveUserDataPrompt=Удалить также пользовательские настройки и данные WriteLite?%n%nСюда входят ваши словари, добавленные слова, список исключений и настройки из папки:%n%s%n%nЕсли вы планируете установить WriteLite снова, выберите «Нет».
english.RemoveUserDataPrompt=Also remove WriteLite user settings and data?%n%nThis includes your dictionaries, added words, ignore list and settings from:%n%s%n%nIf you plan to install WriteLite again, choose No.

[Messages]
; [Messages] values are plain strings: Inno does not expand {cm:...} here, which
; is why the welcome subtitle once shipped reading literally "{cm:WelcomeSub}".
; The text therefore lives inline, per language.
russian.WelcomeLabel1=Добро пожаловать в программу установки [name]
english.WelcomeLabel1=Welcome to the [name] Setup
russian.WelcomeLabel2=WriteLite — интеллектуальный помощник для работы с текстом.%n%nПроверяет орфографию, пунктуацию, грамматику и стиль прямо на вашем компьютере — без облака и без регистрации.%n%nСборка не подписана цифровым сертификатом, поэтому Windows SmartScreen может показать предупреждение.
english.WelcomeLabel2=WriteLite is an intelligent writing assistant.%n%nIt checks spelling, punctuation, grammar and style on your own computer — no cloud, no account.%n%nThis build is not code-signed, so Windows SmartScreen may show a warning.
russian.FinishedHeadingLabel=WriteLite установлен
english.FinishedHeadingLabel=WriteLite is installed

; The default wording is «закройте все экземпляры приложения», which is not
; actionable here: closing the WriteLite window minimises it to the notification
; area by default, so the user does exactly as asked and the installer keeps
; refusing. Say where it actually is.
russian.SetupAppRunningError=WriteLite сейчас запущен.%n%nЗакрытие окна сворачивает WriteLite в область уведомлений, поэтому он может работать, даже если окна не видно. Нажмите правой кнопкой на значок WriteLite рядом с часами и выберите «Завершить WriteLite» — или откройте окно и нажмите «Завершить WriteLite» слева внизу.%n%nЗатем нажмите «OK», чтобы продолжить, или «Отмена», чтобы выйти.
russian.UninstallAppRunningError=WriteLite сейчас запущен.%n%nЗакрытие окна сворачивает WriteLite в область уведомлений, поэтому он может работать, даже если окна не видно. Нажмите правой кнопкой на значок WriteLite рядом с часами и выберите «Завершить WriteLite».%n%nЗатем нажмите «OK», чтобы продолжить, или «Отмена», чтобы выйти.
english.SetupAppRunningError=WriteLite is currently running.%n%nClosing its window minimises WriteLite to the notification area, so it can still be running with no window in sight. Right-click the WriteLite icon next to the clock and choose «Завершить WriteLite» — or open the window and use «Завершить WriteLite» at the bottom left.%n%nThen click OK to continue, or Cancel to exit.
english.UninstallAppRunningError=WriteLite is currently running.%n%nClosing its window minimises WriteLite to the notification area, so it can still be running with no window in sight. Right-click the WriteLite icon next to the clock and choose «Завершить WriteLite».%n%nThen click OK to continue, or Cancel to exit.

[Tasks]
; Order matches the order the user was promised: desktop off, Start menu on,
; autostart off.
Name: "desktopicon";  Description: "{cm:DesktopIcon}";   GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startmenuicon"; Description: "{cm:StartMenuIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart";    Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The staging tree is already stripped of .pdb symbols and logs by
; build-release.ps1; the excludes are a second line of defence so a hand-run
; compile against a raw publish directory still cannot ship them.
Source: "{#SourceDir}\*"; DestDir: "{app}"; \
  Excludes: "*.pdb,*.log,*.pid,logs\*"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\assets\WriteLite.ico"; Tasks: startmenuicon
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\assets\WriteLite.ico"; Tasks: desktopicon

[Registry]
; One Run value, written when the task is ticked and deleted when it is not, so
; upgrading never leaves a second entry and never leaves a stale path behind.
; uninsdeletevalue removes it on uninstall.
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValueName}"; \
  ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "{#RunKey}"; ValueType: none; ValueName: "{#RunValueName}"; \
  Flags: deletevalue uninsdeletevalue; Tasks: not autostart

; The same choice, left where the application can read it once on its next launch.
; Without this the Run value is ambiguous on an upgrade: a user whose settings.json
; says autostart is off would have the box they just ticked undone by their own older
; answer. WriteLiteSetupHandoff reads this, folds it into settings and deletes it.
Root: HKCU; Subkey: "Software\WriteLite"; Flags: uninsdeletekeyifempty
Root: HKCU; Subkey: "{#SetupKey}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "{#SetupKey}"; ValueType: dword; ValueName: "{#AutostartValue}"; \
  ValueData: "1"; Tasks: autostart
Root: HKCU; Subkey: "{#SetupKey}"; ValueType: dword; ValueName: "{#AutostartValue}"; \
  ValueData: "0"; Tasks: not autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Written by the app next to itself at runtime, so Inno does not track them.
Type: filesandordirs; Name: "{app}\logs"
Type: files; Name: "{app}\*.log"
Type: dirifempty; Name: "{app}"

[Code]

{ ---------------------------------------------------------------- install side }

{ Refuse early and clearly if the machine cannot hold the payload, rather than
  failing halfway through extracting more than a gigabyte. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  FreeMB, TotalMB: Cardinal;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    if GetSpaceOnDisk(ExtractFileDrive(WizardDirValue), True, FreeMB, TotalMB) then
    begin
      if FreeMB < 1600 then
      begin
        MsgBox(ExpandConstant('{cm:DiskSpaceWarn}'), mbError, MB_OK);
        Result := False;
      end;
    end;
  end;
end;

{ -------------------------------------------------------------- uninstall side }

function UserDataDir(): String;
begin
  { Matches WriteLiteSettingsStore.GetDefaultPath and WriteLiteDataPaths:
    %LocalAppData%\WriteLite. }
  Result := ExpandConstant('{localappdata}\WriteLite');
end;

{ The offer to delete personal data is a question, asked once, defaulting to No.
  It is deliberately not a silent default-on step: dictionaries and added words
  are the user's work, and a reinstall must find them where they left them. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Dir := UserDataDir();
    if DirExists(Dir) then
    begin
      { Default is No: a reinstall must find the user's dictionaries where they
        left them. UninstallSilent() keeps an unattended removal non-interactive
        and therefore non-destructive. }
      if not UninstallSilent() then
      begin
        if MsgBox(Format(ExpandConstant('{cm:RemoveUserDataPrompt}'), [Dir]),
                  mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        begin
          DelTree(Dir, True, True, True);
        end;
      end;
    end;
  end;
end;
