# Загружает ядра Xray и sing-box с официальных релизов GitHub и сверяет SHA256.
# Версии закреплены: обновление ядра — это правка этого файла и THIRD-PARTY.md.
# Архивы кешируются в vendor\downloads, проверенные файлы кладутся в vendor\cores.
# Повторный запуск ничего не скачивает, если файлы на месте и хеши совпадают.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $root 'vendor\downloads'
$cores = Join-Path $root 'vendor\cores'

$pins = @(
    @{
        Name = 'Xray 26.10.21'
        Url = 'https://github.com/XTLS/Xray-core/releases/download/v26.10.21/Xray-windows-64.zip'
        Archive = 'Xray-windows-64-26.10.21.zip'
        ArchiveSha256 = 'FC58C39EE2F2717B4C54D28D374362389E5319B20AE44AEA76EA45A5243EC827'
        Files = @{ 'xray.exe' = 'C77A00EB84FB93AE044C2D771A1CABCC033B81E487253FBC35B12DF6879C1E15' }
        Folder = ''
    },
    @{
        Name = 'sing-box 1.14.4'
        Url = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip'
        Archive = 'sing-box-1.14.4-windows-amd64.zip'
        ArchiveSha256 = '99185D75D314A07DBBA4A130C8AD986CBEB629A07927975636B341CA61F991DF'
        Files = @{
            'sing-box.exe' = '8F30682EE198855440D2A7FE070D5D3653793B2D01BB8C85EEA88F93BBE7C8B9'
            'libcronet.dll' = '3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928'
        }
        Folder = 'sing-box-1.14.4-windows-amd64'
    }
)

function Test-Sha256([string]$Path, [string]$Expected) {
    (Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected
}

New-Item -ItemType Directory -Force $downloads, $cores | Out-Null
foreach ($pin in $pins) {
    $ready = $true
    foreach ($file in $pin.Files.GetEnumerator()) { if (-not (Test-Sha256 (Join-Path $cores $file.Key) $file.Value)) { $ready = $false } }
    if ($ready) { continue }

    $archive = Join-Path $downloads $pin.Archive
    if (-not (Test-Sha256 $archive $pin.ArchiveSha256)) {
        Write-Host ('Загружаем ' + $pin.Name + '…')
        Invoke-WebRequest -Uri $pin.Url -OutFile $archive -UseBasicParsing
        if (-not (Test-Sha256 $archive $pin.ArchiveSha256)) {
            Remove-Item -LiteralPath $archive -Force
            throw ('Архив ' + $pin.Name + ' не прошёл проверку SHA256.')
        }
    }
    $unpacked = Join-Path $downloads ([IO.Path]::GetFileNameWithoutExtension($pin.Archive))
    if (Test-Path -LiteralPath $unpacked) { Remove-Item -LiteralPath $unpacked -Recurse -Force }
    Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
    foreach ($file in $pin.Files.GetEnumerator()) {
        $source = Join-Path (Join-Path $unpacked $pin.Folder) $file.Key
        if (-not (Test-Sha256 $source $file.Value)) { throw ('Файл ' + $file.Key + ' из ' + $pin.Name + ' не совпал с закреплённым SHA256.') }
        Copy-Item -LiteralPath $source -Destination (Join-Path $cores $file.Key) -Force
    }
    Remove-Item -LiteralPath $unpacked -Recurse -Force
    Write-Host ($pin.Name + ': проверено.')
}
