# Сторонние компоненты

Облачка запускает ядра Xray и sing-box отдельными процессами. Их код не изменяется и в исходники клиента не входит. Бинарники скачиваются с официальных релизов скриптом `scripts/fetch-cores.ps1`, который сверяет SHA256 архива и каждого файла. В установщике они лежат в `tools\` рядом с лицензиями.

## Xray-core 26.10.21

- Релиз: https://github.com/XTLS/Xray-core/releases/tag/v26.10.21
- Исходники: https://github.com/XTLS/Xray-core/tree/v26.10.21
- Лицензия: MPL-2.0 (`vendor/xray-26.10.21/LICENSE-Xray`, в установке — `tools/LICENSE-Xray`)
- SHA256 архива `Xray-windows-64.zip`: `FC58C39EE2F2717B4C54D28D374362389E5319B20AE44AEA76EA45A5243EC827`. Совпадает с файлом `.dgst` релиза.
- SHA256 `xray.exe`: `C77A00EB84FB93AE044C2D771A1CABCC033B81E487253FBC35B12DF6879C1E15`

## sing-box 1.14.4

- Релиз: https://github.com/SagerNet/sing-box/releases/tag/v1.14.4
- Исходники: https://github.com/SagerNet/sing-box/tree/v1.14.4
- Лицензия: GPL-3.0-or-later с условием об имени проекта (`vendor/sing-box-1.14.4/LICENSE-sing-box` и `COPYING-GPL-3.0`)
- SHA256 архива `sing-box-1.14.4-windows-amd64.zip`: `99185D75D314A07DBBA4A130C8AD986CBEB629A07927975636B341CA61F991DF`. Совпадает с `digest` GitHub Release API.
- SHA256 `sing-box.exe`: `8F30682EE198855440D2A7FE070D5D3653793B2D01BB8C85EEA88F93BBE7C8B9`
- SHA256 `libcronet.dll`: `3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928`. Файл из того же официального комплекта, без изменений.

Запись происхождения: `vendor/sing-box-1.14.4/provenance.json`.

## oblachka-core (ядро системного туннеля)

Собирается из исходников в `core/` (`scripts/build-core.ps1`): sing-box 1.14.4 и Xray-core 26.10.21 как библиотеки Go (версии закреплены в `core/go.mod` и `core/go.sum`) плюс выход `xray`, который передаёт соединения встроенному Xray внутри процесса. Код sing-box и Xray не изменяется. Сборка воспроизводимая (`-trimpath`, без `buildid` и cgo), SHA256 файла закреплён в `src/Network/TunCore.cs`. Лицензия — GPL-3.0-or-later, как у sing-box и всего клиента. Название своё: условие sing-box запрещает производным работам называться sing-box.

В то же ядро входит [amneziawg-go](https://github.com/amnezia-vpn/amneziawg-go) (выход AmneziaWG, версия закреплена в `core/go.mod`) — MIT, © WireGuard LLC и Amnezia; код не изменяется.

## zapret и набор Flowseal

Не входят в установщик: сетевая служба скачивает их по включению «YouTube и Discord без VPN» с официальных релизов и сверяет SHA256 по манифесту `zapret/manifest.json` (см. [docs/zapret.md](docs/zapret.md)).

- [zapret](https://github.com/bol-van/zapret) (`winws.exe`) — MIT, © bol-van.
- [WinDivert](https://github.com/basil00/WinDivert) (`WinDivert.dll`, `WinDivert64.sys`) — LGPL-3.0 / GPL-2.0, © basil00.
- `cygwin1.dll` — библиотека Cygwin, LGPL-3.0.
- Стратегии, списки доменов и подменные пакеты — набор [zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube), © Flowseal.

## .NET

Установщик содержит самодостаточную сборку .NET 10 (WPF и Windows Forms) от Microsoft, лицензия MIT: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## Списки для маршрутизации

Базы `geosite.dat` и `geoip.dat` не входят в установщик. Их загружают по нажатию в настройках правил. По умолчанию используется https://github.com/Loyalsoldier/v2ray-rules-dat (GPL-3.0); подписка сервиса может указать свои адреса.

## Флаги и названия стран

Справочники названий стран (ru/en) получены с FlagCDN/Flagpedia (https://flagpedia.net/download/api). PNG-флаги встроены в сборку; недостающие загружаются с https://flagcdn.com по коду страны, и в запросе нет ничего, кроме этого кода. Изображения флагов государств — общественное достояние.

## Шрифты Onest и Unbounded

Onest (https://github.com/googlefonts/onest) и Unbounded (https://github.com/googlefonts/unbounded) — SIL Open Font License 1.1, тексты лицензий в `src/Assets/Fonts/OFL-*.txt`. Встроены статические начертания (Onest 400–700, Unbounded 700–800), нарезанные из вариативных файлов google/fonts и урезанные до латиницы, кириллицы и знаков (fontTools); названия шрифтов не изменены.
