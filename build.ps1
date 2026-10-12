$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$outputDirectory = Join-Path $projectRoot 'app-v09'
& (Join-Path $projectRoot 'check-source-size.ps1')
& dotnet build (Join-Path $projectRoot 'src\Oblachka.csproj') -c Release -o $outputDirectory --configfile (Join-Path $projectRoot 'src\NuGet.Config') -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Сборка не прошла.' }
[System.IO.Directory]::CreateDirectory((Join-Path $outputDirectory 'tools')) | Out-Null
foreach ($coreFile in @('xray.exe', 'LICENSE-Xray')) {
    $targetFile = Join-Path $outputDirectory ('tools\' + $coreFile)
    if (-not (Test-Path -LiteralPath $targetFile)) { Copy-Item -LiteralPath (Join-Path $projectRoot ('app\tools\' + $coreFile)) -Destination $targetFile }
}
$vendorRoot = Join-Path $projectRoot 'vendor\sing-box-1.14.3'
$expectedHash = 'D8B86EF58936DBA9230EC6A57FFA4243933F3C14EF4809828B6C553440ED1518'
if ((Get-FileHash -LiteralPath (Join-Path $vendorRoot 'sing-box-1.14.3-windows-amd64.zip') -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Архив sing-box не прошёл проверку SHA256.' }
$vendorFiles = @{
    'sing-box.exe' = 'unpacked\sing-box-1.14.3-windows-amd64\sing-box.exe'
    'libcronet.dll' = 'unpacked\sing-box-1.14.3-windows-amd64\libcronet.dll'
    'LICENSE-sing-box' = 'LICENSE-sing-box'
    'COPYING-GPL-3.0' = 'COPYING-GPL-3.0'
}
foreach ($binary in @{
    'sing-box.exe' = '5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF'
    'libcronet.dll' = '3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928'
}.GetEnumerator()) {
    $unpackedFile = Join-Path $vendorRoot ('unpacked\sing-box-1.14.3-windows-amd64\' + $binary.Key)
    if ((Get-FileHash -LiteralPath $unpackedFile -Algorithm SHA256).Hash -ne $binary.Value) { throw ('Изменён файл ядра: ' + $binary.Key) }
}
foreach ($item in $vendorFiles.GetEnumerator()) {
    Copy-Item -LiteralPath (Join-Path $vendorRoot $item.Value) -Destination (Join-Path $outputDirectory ('tools\' + $item.Key))
}
Write-Host 'Готово: app-v09\Oblachka.exe. Предыдущие сборки сохранены.'


