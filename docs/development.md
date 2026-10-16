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

## Оформление

Цвета, шрифты, размеры текста и скругления живут только в `theme/tokens.json`: у каждого цвета значение для светлой и тёмной темы и подпись, где он используется. После правки:

```powershell
.\scripts	okens.ps1
```

Скрипт пересобирает `src/Themes/Light.xaml`, `Dark.xaml` (цвета тем), `Tokens.xaml` (шрифты, размеры, скругления) и `theme/tokens.css` (те же переменные для HTML-макетов). Эти файлы руками не правятся.

В разметке цвет берётся как `{DynamicResource Ac}`, размер — `{StaticResource FsRow}`, скругление — `{StaticResource RadiusLg}`. В коде — `Tokens.Brush("Ac")`, `Tokens.Size("FsRow")`, `Tokens.Radius("RadiusLg")`. `scripts/check-tokens.ps1` (часть `test.ps1`) не пропустит «#RRGGBB», `White`, числа в `FontSize` и `CornerRadius`, а также темы, собранные из устаревшего `tokens.json`. Исключение — скругления «таблеток» (половина высоты элемента): это геометрия, они перечислены в скрипте явно.

Нужного токена нет — добавьте его в `tokens.json` с подписью, а не пишите значение на месте.

## Версия

Версия хранится в двух местах: `src/Oblachka.csproj` (`<Version>`) и `ServiceBrand.Version`. Ревизия Git и признак «с изменениями» видны в настройках приложения и в `build-info.json` рядом с exe.

## Выпуск релиза

1. Поднять версию в `src/Oblachka.csproj` и `ServiceBrand.Version`.
2. Добавить раздел `## <версия>` в [CHANGELOG.md](../CHANGELOG.md): он станет описанием релиза.
3. Закоммитить и отправить тег:

   ```powershell
   git tag v0.12.1
   git push origin main v0.12.1
   ```

Дальше всё делает GitHub Actions (`.github/workflows/release.yml`):
1. Сверяет версию тега с кодом.
2. Собирает программу и прогоняет `test.ps1`.
3. Собирает установщик и публикует релиз с SHA256.

Установленные копии найдут новую версию сами в течение 12 часов.

### Пробная версия

Изменения, которые касаются туннеля, защиты WFP, службы или zapret, сначала выходят пробной версией:

```powershell
git tag v0.13.3-rc.1
git push origin main v0.13.3-rc.1
```

Сборка та же, но релиз помечается как pre-release («Облачка 0.13.3 — проба 1»), а в описании есть предупреждение. Обычная обновлялка его не видит: она берёт `releases/latest`, а GitHub не отдаёт туда pre-release. С переключателем «Помощь → Получать пробные версии» она читает список релизов (`releases?per_page=10`) и берёт верхний подходящий, пробный тоже. У пробы и выпуска одинаковый номер версии, поэтому при равном номере обновлялка сравнивает ревизию из имени установщика со своей: стоящая rc.1 видит rc.2 и итоговый выпуск, а выпуск на том же коммите — нет. Когда она проверена, тот же коммит получает тег `v0.13.3` и выходит обычным релизом.

Перед пробной версией такие изменения проверяются на VM-стенде. Тестовый сервер на стенде нужен на порту 443, как у настоящих узлов: на другом порту перехват 80/443 (zapret) туннель не задевает, и поломка не видна.

Ручной запуск сборки (Actions → «Сборка и релиз» → Run workflow) делает всё то же без релиза, а установщик остаётся артефактом сборки.

## Обновление ядер

1. Поменять версии, адреса и SHA256 в `scripts/fetch-cores.ps1`.
2. Обновить лицензии в `vendor/` и [THIRD-PARTY.md](../THIRD-PARTY.md).
3. `.\build.ps1` и `.\test.ps1`.
