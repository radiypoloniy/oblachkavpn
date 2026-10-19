# Собирает ядро системного туннеля oblachka-core.exe из core\ (sing-box 1.14.3 и выход
# Xray для XHTTP) и сверяет SHA256 с закреплённым в TunCore.cs. Сборка воспроизводимая
# (-trimpath, без buildid, без cgo): тот же Go и те же модули дают тот же файл.
# Готовый файл с верным хешем не пересобирается. Изменили core\ — обновите хеш в TunCore.cs.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'vendor\cores\oblachka-core.exe'
$pin = Select-String -LiteralPath (Join-Path $root 'src\Network\TunCore.cs') -Pattern 'Sha256 = "([0-9A-F]{64})"' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
if (-not $pin) { throw 'В TunCore.cs нет закреплённого SHA256 ядра.' }
if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $pin) { return }

$go = Get-Command go -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $go -and (Test-Path -LiteralPath "$env:USERPROFILE\tools\go\bin\go.exe")) { $go = "$env:USERPROFILE\tools\go\bin\go.exe" }
if (-not $go) { throw 'Для ядра oblachka-core нужен Go (версия — в core\go.mod): https://go.dev/dl/' }
New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
$env:CGO_ENABLED = '0'; $env:GOOS = 'windows'; $env:GOARCH = 'amd64'; $env:GOAMD64 = 'v1'; $env:GOFLAGS = '-mod=readonly'
$tags = 'with_gvisor,with_quic,with_utls,with_clash_api,badlinkname,tfogo_checklinkname0'
$ldflags = "-X 'github.com/sagernet/sing-box/constant.Version=1.14.3' -X runtime.godebugDefault=multipathtcp=0,tlssha1=1 -checklinkname=0 -s -w -buildid="
Push-Location (Join-Path $root 'core')
try {
    & $go build -trimpath -buildvcs=false -tags $tags -ldflags $ldflags -o $target .
    if ($LASTEXITCODE -ne 0) { throw 'Ядро oblachka-core не собралось.' }
}
finally { Pop-Location }
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
if ($hash -ne $pin) {
    Remove-Item -LiteralPath $target -Force
    throw ('Ядро oblachka-core собралось с SHA256 ' + $hash + ', а в TunCore.cs закреплён ' + $pin + '. Другой Go или изменён core\.')
}
Write-Host 'oblachka-core: собрано и проверено.'
