# Сторонние компоненты

Облачка запускает ядра Xray и sing-box отдельными процессами. Их код не изменяется и в исходники клиента не входит. Бинарники скачиваются с официальных релизов скриптом `scripts/fetch-cores.ps1`, который сверяет SHA256 архива и каждого файла. В установщике они лежат в `tools\` рядом с лицензиями.

## Xray-core 26.9.9

- Релиз: https://github.com/XTLS/Xray-core/releases/tag/v26.9.9
- Исходники: https://github.com/XTLS/Xray-core/tree/v26.9.9
- Лицензия: MPL-2.0 (`vendor/xray-26.9.9/LICENSE-Xray`, в установке — `tools/LICENSE-Xray`)
- SHA256 архива `Xray-windows-64.zip`: `244DEABA2098C2964E49BBA90DF3707777E5F5F428A82D2F29604015F24BEEC2`. Совпадает с файлом `.dgst` релиза.
- SHA256 `xray.exe`: `0D0FC0EA2B05641ACB78C01FC36AD694E7B029861B2D5EB93DA0E3E9FDA9A98F`

## sing-box 1.14.3

- Релиз: https://github.com/SagerNet/sing-box/releases/tag/v1.14.3
- Исходники: https://github.com/SagerNet/sing-box/tree/v1.14.3
- Лицензия: GPL-3.0-or-later с условием об имени проекта (`vendor/sing-box-1.14.3/LICENSE-sing-box` и `COPYING-GPL-3.0`)
- SHA256 архива `sing-box-1.14.3-windows-amd64.zip`: `D8B86EF58936DBA9230EC6A57FFA4243933F3C14EF4809828B6C553440ED1518`. Совпадает с `digest` GitHub Release API.
- SHA256 `sing-box.exe`: `5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF`
- SHA256 `libcronet.dll`: `3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928`. Файл из того же официального комплекта, без изменений.

Запись происхождения: `vendor/sing-box-1.14.3/provenance.json`.

## .NET

Установщик содержит самодостаточную сборку .NET 10 (WPF и Windows Forms) от Microsoft, лицензия MIT: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT

## Списки для маршрутизации

Базы `geosite.dat` и `geoip.dat` не входят в установщик. Их загружают по нажатию в настройках правил. По умолчанию используется https://github.com/Loyalsoldier/v2ray-rules-dat (GPL-3.0); подписка сервиса может указать свои адреса.

## Флаги и названия стран

Справочники названий стран (ru/en) получены с FlagCDN/Flagpedia (https://flagpedia.net/download/api). PNG-флаги встроены в сборку; недостающие загружаются с https://flagcdn.com по коду страны, и в запросе нет ничего, кроме этого кода. Изображения флагов государств — общественное достояние.
