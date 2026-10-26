# Подписать релиз офлайн-ключом: скачать установщик из релиза, сверить его с digest GitHub,
# подписать (scripts\release-key.cs), проверить подпись открытыми ключами из клиента и приложить
# <установщик>.sig к релизу. Пока подписи нет, Облачка этот релиз не предлагает.
# Запуск после того, как сборка на GitHub создала релиз: .\scripts\sign-release.ps1 -Tag v0.21.0 -Key E:\oblachka-release.key
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Key
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repo = 'radiypoloniy/oblachkavpn'
if (-not (Test-Path -LiteralPath $Key)) { throw ('Нет файла ключа: ' + $Key) }

$release = gh api ('repos/' + $repo + '/releases/tags/' + $Tag) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw ('Релиз ' + $Tag + ' не найден.') }
$asset = @($release.assets | Where-Object { $_.name -match '^Oblachka-setup-.+\.exe$' })
if ($asset.Count -ne 1) { throw 'В релизе должен быть ровно один установщик.' }
$asset = $asset[0]
if ($release.assets | Where-Object { $_.name -eq ($asset.name + '.sig') }) { throw 'Релиз уже подписан.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('oblachka-sign-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    gh release download $Tag --repo $repo --pattern $asset.name --dir $work
    if ($LASTEXITCODE -ne 0) { throw 'Установщик не скачался.' }
    $file = Join-Path $work $asset.name
    $sha = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    if ($asset.digest -ne ('sha256:' + $sha.ToLowerInvariant())) { throw ('SHA256 скачанного файла не совпал с digest GitHub: ' + $asset.digest) }

    & dotnet run (Join-Path $root 'scripts\release-key.cs') -- sign $Key $file
    if ($LASTEXITCODE -ne 0) { throw 'Подпись не сделана.' }

    # Проверка теми ключами, что зашиты в клиент: подпись чужим ключом клиент всё равно отверг бы.
    $source = Get-Content -LiteralPath (Join-Path $root 'src\App\ReleaseSignature.cs') -Raw
    $keys = [regex]::Matches(($source -split 'Keys =', 2)[1].Split(']')[0], '"([A-Za-z0-9+/=]+)"') | ForEach-Object { $_.Groups[1].Value }
    if (-not $keys) { throw 'В ReleaseSignature.Keys нет ни одного открытого ключа.' }
    $verified = $false
    foreach ($pub in $keys) {
        $pubFile = Join-Path $work 'key.pub'
        [IO.File]::WriteAllText($pubFile, $pub)
        & dotnet run (Join-Path $root 'scripts\release-key.cs') -- verify $pubFile $file | Out-Null
        if ($LASTEXITCODE -eq 0) { $verified = $true; break }
    }
    if (-not $verified) { throw 'Подпись не проходит ни одним ключом из клиента: этот ключ не тот.' }

    gh release upload $Tag ($file + '.sig') --repo $repo
    if ($LASTEXITCODE -ne 0) { throw 'Подпись не загрузилась в релиз.' }
    Write-Host ('Релиз ' + $Tag + ' подписан: ' + $asset.name + '.sig')
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
