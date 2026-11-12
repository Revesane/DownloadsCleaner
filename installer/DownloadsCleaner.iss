; Установщик Downloads Cleaner для Windows (Inno Setup 6).
;
; Ставит программу для текущего пользователя, без прав администратора, в
; %LocalAppData%\Programs\DownloadsCleaner - в эту папку программа может писать сама,
; поэтому кнопка «Обновить» продолжает работать и после установки.
;
; Сборка (версия берётся из тега выпуска):
;   ISCC.exe /DAppVersion=1.2.3 installer\DownloadsCleaner.iss
; Ожидает готовый exe в publish\win-x64\DownloadsCleaner.exe (см. publish.ps1).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; Готовый exe; для проверочной сборки можно передать другой: /DSourceExe=путь
#ifndef SourceExe
  #define SourceExe "..\publish\win-x64\DownloadsCleaner.exe"
#endif

; Тот же идентификатор знает программа: после самообновления она поправляет
; версию в «Приложениях» Windows (GitHubUpdateService.InstallerAppId).
#define AppId "6E1B7D42-3F9A-4C1E-9B57-2D8A4C0F7E31"

[Setup]
AppId={{{#AppId}}
AppName=Downloads Cleaner
AppVersion={#AppVersion}
AppVerName=Downloads Cleaner {#AppVersion}
AppPublisher=Revesane
AppPublisherURL=https://github.com/Revesane/DownloadsCleaner
AppSupportURL=https://github.com/Revesane/DownloadsCleaner/issues
AppUpdatesURL=https://github.com/Revesane/DownloadsCleaner/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\DownloadsCleaner
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\publish
OutputBaseFilename=DownloadsCleanerSetup
SetupIconFile=..\src\DownloadsCleaner\Assets\app.ico
UninstallDisplayIcon={app}\DownloadsCleaner.exe
UninstallDisplayName=Downloads Cleaner
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Окно выбора языка - только если язык Windows не русский и не английский.
ShowLanguageDialog=auto
; Работающую программу (в том числе спрятанную в трей) установщик просит закрыть.
AppMutex=DownloadsCleaner.SingleInstance
CloseApplications=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AutoStart=Запускать Downloads Cleaner вместе с Windows (в трее)
english.AutoStart=Start Downloads Cleaner with Windows (in the tray)
russian.StartupGroup=Автозагрузка:
english.StartupGroup=Startup:

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:StartupGroup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Downloads Cleaner"; Filename: "{app}\DownloadsCleaner.exe"
Name: "{userdesktop}\Downloads Cleaner"; Filename: "{app}\DownloadsCleaner.exe"; Tasks: desktopicon

[Registry]
; Та же запись, что ставит переключатель «Запускать вместе с Windows» в настройках программы,
; поэтому галочка установщика и переключатель всегда согласованы.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DownloadsCleaner"; ValueData: """{app}\DownloadsCleaner.exe"" --minimized"; Tasks: autostart

[Run]
Filename: "{app}\DownloadsCleaner.exe"; Description: "{cm:LaunchProgram,Downloads Cleaner}"; \
    Flags: nowait postinstall skipifsilent

[Code]
// При удалении убираем и автозапуск - даже если его включили позже в настройках программы.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DownloadsCleaner');
end;

// Повторная установка поверх: снятая галочка автозапуска должна его выключить.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DownloadsCleaner');
end;
