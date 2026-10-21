; Установщик Облачков. Собирается .\build-installer.ps1, вручную ISCC не запускать:
; скрипт готовит самодостаточную сборку и передаёт Version, Revision, SourceDir, OutputDir.
;
; Главное, что делает установщик сверх копирования файлов, — сетевая служба:
;  - ставит приложение только в Program Files (выбор папки отключён): служба работает от
;    SYSTEM и регистрируется лишь оттуда, куда без прав администратора не пишут;
;  - перед обновлением удаляет прежнюю службу: её остановка снимает сессию и возвращает сеть;
;  - при удалении сначала удаляет службу (сеть возвращается), потом закрывает окно.
; Данные пользователя (подписки, настройки в LocalAppData) при удалении остаются.

#ifndef Version
  #error Запускайте через build-installer.ps1
#endif

[Setup]
AppId={{D5DAEC8C-8C26-4A03-AFBE-038B2F89FFC0}
AppName=Облачка
AppVersion={#Version}
AppVerName=Облачка {#Version}
AppPublisher=Облачка
; Числовая часть: у предварительных версий (0.17.0-rc.1) суффикс в ресурсах файла не допускается.
VersionInfoVersion={#VersionNumber}
VersionInfoDescription=Установщик Облачков
DefaultDirName={autopf}\Oblachka
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Oblachka-setup-{#Version}-{#Revision}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\Oblachka.exe
SetupIconFile=..\src\Assets\app.ico
UninstallDisplayName=Облачка
SetupLogging=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"

[InstallDelete]
; Обновление ставится на чистую папку: старые библиотеки прежней версии не должны остаться.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Облачка"; Filename: "{app}\Oblachka.exe"
Name: "{autodesktop}\Облачка"; Filename: "{app}\Oblachka.exe"; Tasks: desktopicon

[Run]
; Окно запускается от имени пользователя, а не администратора, который ставил программу.
Filename: "{app}\Oblachka.exe"; Description: "Запустить Облачка"; Flags: postinstall nowait skipifsilent runasoriginaluser
; Обновление из приложения идёт тихо (/SILENT /RELAUNCH=1): после него окно открывается само.
Filename: "{app}\Oblachka.exe"; Flags: nowait runasoriginaluser; Check: RelaunchAfterUpdate

[Registry]
; Ссылки «Добавить в Облачка» (oblachka://) с сайтов и из ботов продавцов подписки.
Root: HKLM; Subkey: "Software\Classes\oblachka"; ValueType: string; ValueName: ""; ValueData: "URL:Oblachka"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\oblachka"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\oblachka\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Oblachka.exe"",0"
Root: HKLM; Subkey: "Software\Classes\oblachka\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Oblachka.exe"" ""%1"""
; Автозапуск включает сам пользователь в настройках; при удалении запись убирается.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Oblachka"; ValueType: none; Flags: uninsdeletevalue dontcreatekey

[UninstallRun]
; Сначала служба: её остановка снимает сессию и возвращает обычную сеть. Убить её процесс
; нельзя — тогда фильтры защиты остались бы до перезагрузки.
Filename: "{app}\Oblachka.exe"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"
; Затем окна пользователей: служба уже остановлена, сеть открыта, закрывать их безопасно.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Oblachka.exe"; Flags: runhidden waituntilterminated; RunOnceId: "CloseWindows"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\Oblachka"

[Code]
const
  ServiceOk = 0;

function RelaunchAfterUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;

function ServiceError(Code: Integer): String;
begin
  case Code of
    2: Result := 'нет прав администратора';
    3: Result := 'папка установки не в Program Files';
    4: Result := 'диспетчер служб Windows отказал';
  else
    Result := 'код ' + IntToStr(Code);
  end;
end;

// Прежняя служба удаляется до замены файлов: она держит Oblachka.exe и ядро туннеля,
// а её остановка возвращает сеть, если подключение было активно.
// Службы нет (sc query отвечает 1060): снимать нечего, даже если прежняя версия не запускается.
function ServiceMissing(): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query OblachkaNetwork', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 1060);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Old: String;
begin
  Result := '';
  Old := ExpandConstant('{app}\Oblachka.exe');
  if FileExists(Old) then
  begin
    Exec(Old, '--uninstall-service', '', SW_HIDE, ewWaitUntilTerminated, Code);
    // Повреждённая прежняя установка (файлы не запускаются) без службы не должна навсегда
    // запрещать новую: иначе выйти из этого состояния можно только удалением вручную.
    if (Code <> ServiceOk) and not ServiceMissing() then
      Result := 'Не удалось остановить сетевую службу прежней версии (' + ServiceError(Code) + '). Перезагрузите компьютер и запустите установку снова.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not Exec(ExpandConstant('{app}\Oblachka.exe'), '--install-service', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Code := -1;
    if Code <> ServiceOk then
      SuppressibleMsgBox('Сетевая служба не установлена (' + ServiceError(Code) + ').' + #13#10 +
        'Облачка будет работать, но для системного режима попросит права администратора при каждом подключении, ' +
        'а при аварийном завершении окна защита снимется. Попробуйте переустановить.', mbError, MB_OK, IDOK);
  end;
end;
