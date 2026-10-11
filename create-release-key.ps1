param([Parameter(Mandatory = $true)][string]$Directory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Directory)
if (Test-Path -LiteralPath $root) { throw 'Choose a new directory; existing files are preserved.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$rsa = [Security.Cryptography.RSACng]::new(3072)
try {
    $parameters = $rsa.ExportParameters($true)
    $private = [ordered]@{}; $public = [ordered]@{}
    foreach ($name in @('Modulus', 'Exponent', 'D', 'P', 'Q', 'DP', 'DQ', 'InverseQ')) {
        $private[$name] = [Convert]::ToBase64String($parameters.$name)
        if ($name -in @('Modulus', 'Exponent')) { $public[$name] = $private[$name] }
    }
    [IO.File]::WriteAllText((Join-Path $root 'private.json'), ($private | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $root 'public.json'), ($public | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
} finally { $rsa.Dispose() }
Write-Host 'Keep private.json offline. Only public.json may be distributed. These are release signatures, not Windows Authenticode.'
