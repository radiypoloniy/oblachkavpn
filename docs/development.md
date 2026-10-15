# Разработка

## Что нужно

- Windows 10 1809+ или Windows 11, x64
- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0): `winget install Microsoft.DotNet.SDK.10`
- PowerShell 5.1 (встроен в Windows) или PowerShell 7
- Git
- Для установщика — [Inno Setup 6](https://jrsoftware.org/isinfo.php): `winget install JRSoftware.InnoSetup --scope user`

## Сборка и запуск

```powershell
.\build.ps1            # скачивает и проверяет ядра, собирает out\app
.\out\app\Oblachka.exe
```

При первом запуске `build.ps1` скачивает Xray и sing-box с GitHub (около 40 МБ) в `vendor\downloads` и сверяет SHA256. Потом берёт их из кеша.

Сборка в `out\app` требует установленного .NET 10 Desktop Runtime. Самодостаточная сборка с .NET внутри получается только через установщик.

## Проверки

```powershell
.\test.ps1
```

Скрипт собирает отдельную тестовую версию в `out\test` и запускает:

1. проверку размеров файлов и обращения на «вы»;
2. самопроверки `Oblachka.Tests.exe --self-test`: разбор подписок и ссылок, маршрутизация, канал службы, журнал сети, WFP-правила, QR и прочее, всего около 200;
3. проверки интерфейса `Oblachka.exe --ui-self-test`, около 40 сценариев на настоящем окне;
4. проверку, что в `out\app` нет тестового кода.

Проверки не трогают сеть компьютера и сохранения пользователя. Сетевые сценарии с настоящим TUN выполняются только на виртуальной машине.

Части окна для проверок (`src/Window/Fixtures`) компилируются только с `-p:IncludeTestCode=true`, и это делает только `test.ps1`.

## Превью экранов

Тестовая сборка умеет сохранить снимок экрана без запуска подключения:

```powershell
.\out\test\Oblachka.exe --preview preview.png servers        # или -dark: servers-dark
```

Страницы: `welcome`, `welcome-import`, `servers`, `servers-groups`, `groups`, `settings`, `rules`, `rules-lists`, `apps`, `doctor`, `power-on`, `tray`, `dialog`, `confirm` и другие (см. `Window/Fixtures/MainWindow.DesignFixtures.cs`).

## Установщик

```powershell
.\build.ps1
.\build-installer.ps1    # dist\Oblachka-setup-<версия>-<ревизия>.exe
```

Установщик собирается только из закоммиченных `src` и `installer`: так файл всегда соответствует ревизии. Подробности — [installer.md](installer.md).

## Версия

Версия хранится в `src/Oblachka.csproj` (`<Version>`). Ревизия Git и признак «с изменениями» видны в настройках приложения и в `build-info.json` рядом с exe.

## Обновление ядер

1. Поменять версии, адреса и SHA256 в `scripts/fetch-cores.ps1`.
2. Обновить лицензии в `vendor/` и [THIRD-PARTY.md](../THIRD-PARTY.md).
3. `.\build.ps1` и `.\test.ps1`.
