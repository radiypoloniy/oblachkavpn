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
        Name = 'Xray 26.10.17'
        Url = 'https://github.com/XTLS/Xray-core/releases/download/v26.10.17/Xray-windows-64.zip'
        Archive = 'Xray-windows-64-26.10.17.zip'
        ArchiveSha256 = 'C10A532A9271964F2AA2F4B4C0757605FF529222CB1D5D7AB88F8ECC52E67FB0'
        Files = @{ 'xray.exe' = 'A743B8C2CE49CB33D6C1CF764C780BF1A475D746AD7485E7C3FCFE6458511C92' }
        Folder = ''
    },
    @{
        Name = 'sing-box 1.14.3'
        Url = 'https://github.com/SagerNet/sing-box/releases/download/v1.14.3/sing-box-1.14.3-windows-amd64.zip'
        Archive = 'sing-box-1.14.3-windows-amd64.zip'
        ArchiveSha256 = 'D8B86EF58936DBA9230EC6A57FFA4243933F3C14EF4809828B6C553440ED1518'
        Files = @{
            'sing-box.exe' = '5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF'
            'libcronet.dll' = '3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928'
        }
        Folder = 'sing-box-1.14.3-windows-amd64'
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
