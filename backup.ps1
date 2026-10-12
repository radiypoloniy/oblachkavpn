# Резервная копия репозитория. Единственная рабочая копия лежит на рабочем столе,
# поэтому копия на втором диске обязательна, а копия вне машины желательна.
#
#   .\backup.ps1                              — зеркало на D:
#   .\backup.ps1 -BundlePath "C:\...\oblachka.bundle"  — ещё и один файл-архив
#
# Файл-архив удобен для облачной папки: это один файл, а не дерево мелких объектов,
# которые синхронизация может повредить. Восстановление: git clone <файл> <папка>.
param(
    [string] $Mirror = 'D:\Backups\oblachka-client.git',
    [string] $BundlePath
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

# git пишет в stderr и при успехе («Everything up-to-date»), а Windows PowerShell 5.1
# при ErrorActionPreference = Stop считает это ошибкой. Поэтому вызываем через обёртку
# и смотрим на код возврата, а не на поток ошибок.
function Invoke-Git {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]] $Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & git @Arguments } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw ('git ' + ($Arguments -join ' ') + ': код возврата ' + $LASTEXITCODE) }
}

if (-not (Test-Path -LiteralPath $Mirror)) {
    Write-Host ('Зеркала нет, создаю: ' + $Mirror)
    Invoke-Git clone --bare . $Mirror
}
$remotes = Invoke-Git remote
if ($remotes -notcontains 'backup') { Invoke-Git remote add backup $Mirror }
else { Invoke-Git remote set-url backup $Mirror }

Invoke-Git push backup --all
Invoke-Git push backup --tags

$head = (Invoke-Git rev-parse --short HEAD)
Write-Host ('Зеркало обновлено: ' + $Mirror + ' (HEAD ' + $head + ')')

if ($BundlePath) {
    $directory = Split-Path -Parent $BundlePath
    if ($directory -and -not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    # Пишем во временный файл и подменяем: прерванная запись не испортит прежний архив.
    $temporary = $BundlePath + '.new'
    Invoke-Git bundle create $temporary --all
    Move-Item -LiteralPath $temporary -Destination $BundlePath -Force
    $size = [math]::Round((Get-Item -LiteralPath $BundlePath).Length / 1MB, 1)
    Write-Host ('Файл-архив обновлён: ' + $BundlePath + ' (' + $size + ' МБ)')
}

$uncommitted = Invoke-Git status --porcelain
if ($uncommitted) { Write-Warning ('В рабочей копии есть незакоммиченные изменения (' + ($uncommitted | Measure-Object).Count + ' файлов): в копию они не попали.') }
