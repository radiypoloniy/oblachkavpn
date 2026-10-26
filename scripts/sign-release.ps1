# Подписать релиз ключом разработчика и приложить <установщик>.sig. Пока подписи нет, Облачка 0.21+
# пишет «вышла, ждёт подписи» и не ставит её. Запуск после того, как сборка на GitHub создала релиз:
#   .\scripts\sign-release.ps1 -Tag v0.21.0              — ключ, сохранённый на этом компьютере (release-key.cs protect)
#   .\scripts\sign-release.ps1 -Tag v0.21.0 -Key E:\k    — файл ключа, пароль с клавиатуры
# Доступ к GitHub — токен, который git уже хранит для этого репозитория (git credential); GitHub CLI не нужен.
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Key = 'stored'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'radiypoloniy/oblachkavpn'
# Первая строка — пустышка: PowerShell с UTF-8 в консоли ставит перед текстом BOM, и git не узнаёт первое поле.
$credential = "oblachka=1`nprotocol=https`nhost=github.com`n" | git credential fill
$token = ($credential | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if (-not $token) { throw 'git не хранит токен GitHub для этого компьютера.' }
$headers = @{ Authorization = 'Bearer ' + $token; Accept = 'application/vnd.github+json'; 'User-Agent' = 'oblachka-release' }

$release = Invoke-RestMethod -Headers $headers -Uri ('https://api.github.com/repos/' + $repo + '/releases/tags/' + $Tag)
$asset = @($release.assets | Where-Object { $_.name -match '^Oblachka-setup-.+\.exe$' })
if ($asset.Count -ne 1) { throw 'В релизе должен быть ровно один установщик.' }
$asset = $asset[0]
if ($release.assets | Where-Object { $_.name -eq ($asset.name + '.sig') }) { Write-Host ('Релиз ' + $Tag + ' уже подписан.'); return }

$work = Join-Path ([IO.Path]::GetTempPath()) ('oblachka-sign-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    $file = Join-Path $work $asset.name
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UseBasicParsing
    $sha = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($asset.digest -ne ('sha256:' + $sha.ToLowerInvariant())) { throw ('SHA256 скачанного файла не совпал с digest GitHub: ' + $asset.digest) }

    & dotnet run (Join-Path $root 'scripts\release-key.cs') -- sign $Key $file
    if ($LASTEXITCODE -ne 0) { throw 'Подпись не сделана.' }

    # Проверка теми ключами, что зашиты в клиент: подпись чужим ключом клиент всё равно отверг бы.
    $source = Get-Content -LiteralPath (Join-Path $root 'src\App\ReleaseSignature.cs') -Raw
    $keys = [regex]::Matches(($source -split 'Keys =', 2)[1].Split(']')[0], '"([A-Za-z0-9+/=]+)"') | ForEach-Object { $_.Groups[1].Value }
    $verified = $false
    foreach ($pub in $keys) {
        $pubFile = Join-Path $work 'key.pub'
        [IO.File]::WriteAllText($pubFile, $pub)
        & dotnet run (Join-Path $root 'scripts\release-key.cs') -- verify $pubFile $file | Out-Null
        if ($LASTEXITCODE -eq 0) { $verified = $true; break }
    }
    if (-not $verified) { throw 'Подпись не проходит ни одним ключом из клиента: этот ключ не тот.' }

    $upload = 'https://uploads.github.com/repos/' + $repo + '/releases/' + $release.id + '/assets?name=' + [uri]::EscapeDataString($asset.name + '.sig')
    Invoke-RestMethod -Method Post -Headers $headers -Uri $upload -ContentType 'application/octet-stream' -InFile ($file + '.sig') | Out-Null
    Write-Host ('Релиз ' + $Tag + ' подписан: ' + $asset.name + '.sig')
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
