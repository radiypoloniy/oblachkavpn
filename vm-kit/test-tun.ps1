param(
    [Parameter(Mandatory = $true)][ValidateRange(1, 65535)][int]$SocksPort,
    [Parameter(Mandatory = $true)][string[]]$PeerAddresses,
    [string]$ApplicationDirectory = '',
    [switch]$Run,
    [switch]$AcknowledgeDisposableVm
)
$ErrorActionPreference = 'Stop'
if ($ApplicationDirectory -eq '') {
    $projectApp = Join-Path $PSScriptRoot '..\app-v09'
    $packageApp = Join-Path $PSScriptRoot '..'
    if (Test-Path -LiteralPath (Join-Path $projectApp 'Oblachka.exe')) { $ApplicationDirectory = $projectApp }
    else { $ApplicationDirectory = $packageApp }
}
$ApplicationDirectory = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
$client = Join-Path $ApplicationDirectory 'Oblachka.exe'
$core = Join-Path $ApplicationDirectory 'tools\sing-box.exe'
if (-not (Test-Path -LiteralPath $client) -or -not (Test-Path -LiteralPath $core)) { throw 'Нет полного комплекта приложения.' }
if ((Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -ne '5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF') {
    throw 'sing-box не соответствует закреплённой версии.'
}
if ($Run) {
    if (-not $AcknowledgeDisposableVm) { throw 'Запуск требует -AcknowledgeDisposableVm и отдельной VM со снимком.' }
    $vmModel = (Get-CimInstance -ClassName Win32_ComputerSystem).Model
    if ($vmModel -notmatch 'Virtual Machine|VMware|VirtualBox|KVM|QEMU|Parallels|HVM') {
        throw 'Не обнаружена поддерживаемая виртуальная машина. На рабочем компьютере запуск запрещён.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    finally { $identity.Dispose() }
    if (-not $isAdministrator) { throw 'На VM открой PowerShell от администратора.' }
}
$labRoot = Join-Path $ApplicationDirectory ('vm-lab-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($labRoot) | Out-Null
$peersPath = Join-Path $labRoot 'peers.json'
$configPath = Join-Path $labRoot 'tun.json'
ConvertTo-Json -InputObject @($PeerAddresses) | Set-Content -LiteralPath $peersPath -Encoding utf8
$process = Start-Process -FilePath $client -ArgumentList @('--prepare-vm-tun', $SocksPort.ToString(), ('"' + $peersPath + '"'), ('"' + $configPath + '"')) -WorkingDirectory $ApplicationDirectory -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $configPath)) { throw 'Проверка/подготовка TUN не прошла. Нужны точные IP узла и порт SOCKS.' }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$interfaceName = $config.inbounds[0].interface_name
Write-Host ('Конфигурация принята sing-box check: ' + $configPath)
if (-not $Run) {
    Write-Host 'Сеть не изменена. Для запуска на одноразовой VM: -Run -AcknowledgeDisposableVm.'
    return
}
if (Get-NetAdapter -Name $interfaceName -ErrorAction SilentlyContinue) { throw 'Имя интерфейса уже занято.' }
$statusPath = Join-Path $labRoot 'run-status.json'
$status = [ordered]@{ Stage = 'Starting'; InterfaceName = $interfaceName; Started = (Get-Date).ToUniversalTime().ToString('O'); ExitCode = $null }
$status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
Write-Host 'Начинается экспериментальный TUN. Останови ядро Ctrl+C. Этот стенд не является готовым системным режимом приложения.'
try {
    $status.Stage = 'Running'
    $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    & $core run -c $configPath
    $status.ExitCode = $LASTEXITCODE
}
finally {
    # Audit only our named interface. Do not restore an old snapshot of the whole machine.
    $remaining = @(Get-NetAdapter -Name $interfaceName -ErrorAction SilentlyContinue)
    $status.Stage = if ($remaining.Count -eq 0) { 'StoppedInterfaceAbsent' } else { 'NeedsManualReview' }
    $status['Finished'] = (Get-Date).ToUniversalTime().ToString('O')
    $status | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    Write-Host ('Результат эксперимента: ' + $statusPath)
    if ($remaining.Count -gt 0) { Write-Warning 'Собственный интерфейс остался. Сеть автоматически не признана восстановленной; откати снимок VM.' }
}
