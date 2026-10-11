function Read-ReleaseRsa([string]$Path, [switch]$Private) {
    if ((Get-Item -LiteralPath $Path).Length -gt 16384) { throw 'Key file too large.' }
    $key = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $parameters = [Security.Cryptography.RSAParameters]::new()
    foreach ($name in @('Modulus', 'Exponent')) { $parameters.$name = [Convert]::FromBase64String($key.$name) }
    if ($Private) {
        foreach ($name in @('D', 'P', 'Q', 'DP', 'DQ', 'InverseQ')) { $parameters.$name = [Convert]::FromBase64String($key.$name) }
    }
    if ($parameters.Modulus.Length -lt 384) { throw 'RSA key must be at least 3072 bits.' }
    $rsa = [Security.Cryptography.RSA]::Create()
    try { $rsa.ImportParameters($parameters); return $rsa } catch { $rsa.Dispose(); throw }
}

function Get-Sha256Bytes([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') } finally { $algorithm.Dispose() }
}
