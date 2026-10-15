$ErrorActionPreference = 'Stop'

function Get-ClientSourceFingerprint {
    $sourceRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
    $entries = Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object {
        $_.FullName -notmatch '\\(obj|bin)\\'
    } | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($sourceRoot.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($entries -join "`n")))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-ClientRevision {
    $revision = & git -C (Split-Path -Parent $PSScriptRoot) rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось определить ревизию Git.' }
    $changes = & git -C (Split-Path -Parent $PSScriptRoot) status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось проверить состояние Git.' }
    if ($changes) { return ($revision + '-dirty') }
    return $revision
}

function Write-ClientBuildInfo([string]$Directory, [string]$Revision) {
    $metadata = [ordered]@{
        Version = 1
        Revision = $Revision
        BuiltUtc = [DateTime]::UtcNow.ToString('o')
        SourceFingerprint = Get-ClientSourceFingerprint
        AssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $Directory 'Oblachka.dll') -Algorithm SHA256).Hash
    }
    [IO.File]::WriteAllText((Join-Path $Directory 'build-info.json'), ($metadata | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}

function Assert-ClientBuildInfo([string]$Directory) {
    $path = Join-Path $Directory 'build-info.json'
    if (-not (Test-Path -LiteralPath $path)) { throw 'Нет сведений о сборке. Запустите .\build.ps1 перед упаковкой.' }
    $metadata = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($metadata.SourceFingerprint -ne (Get-ClientSourceFingerprint) -or $metadata.Revision -ne (Get-ClientRevision) -or
        $metadata.AssemblySha256 -ne (Get-FileHash -LiteralPath (Join-Path $Directory 'Oblachka.dll') -Algorithm SHA256).Hash) {
        throw 'Сборка не соответствует текущим исходникам. Запустите .\build.ps1 перед упаковкой.'
    }
}
