# Разбор внешнего захвата трафика гостя: что уходило наружу мимо туннеля.
#
# Захват снимается снаружи гостя, самим гипервизором, а не внутри системы:
#   VBoxManage modifyvm "<VM>" --nictrace1 on --nictracefile1 <путь>.pcap
# Так результат не зависит от того, что гость о себе сообщает.
#
# Два урока, стоившие времени, закреплены здесь в коде:
#   1. Длину кадра нужно приводить к int. Прочитанная как UInt32, она утягивает
#      арифметику смещений в беззнаковый тип, и разбор молча уезжает.
#   2. Первый кадр у nictrace вырожденный: incl=4 при orig=60. Его надо пропускать,
#      это не признак чужого формата.
# Признак верного разбора один: поток кончается ровно на последнем байте файла.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Peer,
    [string[]]$Needle = @(),
    [double]$WindowStart = -1,
    [double]$WindowEnd = -1,
    [double]$Bucket = 10
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Path)) { throw "Файл захвата не найден: $Path" }
$bytes = [IO.File]::ReadAllBytes($Path)
if ($bytes.Length -lt 24) { throw 'Файл короче заголовка pcap.' }
$magic = '{0:x2}{1:x2}{2:x2}{3:x2}' -f $bytes[3], $bytes[2], $bytes[1], $bytes[0]
if ($magic -ne 'a1b2c3d4') { throw "Ожидался pcap с обычным порядком байтов, получено 0x$magic." }

function AddrBytes([string]$ip) { , [byte[]]($ip.Split('.') | ForEach-Object { [byte][int]$_ }) }

# Поиск идёт по сырым байтам файла: так он не зависит от правильности разбора кадров.
function CountRaw([byte[]]$hay, [byte[]]$pin) {
    $n = 0
    $last = $hay.Length - $pin.Length
    for ($i = 0; $i -le $last; $i++) {
        if ($hay[$i] -ne $pin[0]) { continue }
        $ok = $true
        for ($j = 1; $j -lt $pin.Length; $j++) { if ($hay[$i + $j] -ne $pin[$j]) { $ok = $false; break } }
        if ($ok) { $n++ }
    }
    $n
}

function IsHousekeeping([string]$a) {
    if ($a -eq '255.255.255.255' -or $a -eq '0.0.0.0') { return $true }
    $first = [int]($a.Split('.')[0])
    if ($first -ge 224 -and $first -le 239) { return $true }
    if ($first -eq 10 -or $a.StartsWith('192.168.') -or $a.StartsWith('169.254.')) { return $true }
    if ($first -eq 172) {
        $second = [int]($a.Split('.')[1])
        if ($second -ge 16 -and $second -le 31) { return $true }
    }
    return $false
}

$pkts = New-Object System.Collections.ArrayList
$v4 = 0; $v6 = 0; $arp = 0; $stub = 0; $pos = 24
while ($pos + 16 -le $bytes.Length) {
    $sec = [int][BitConverter]::ToUInt32($bytes, $pos)
    $usec = [int][BitConverter]::ToUInt32($bytes, $pos + 4)
    $incl = [int][BitConverter]::ToUInt32($bytes, $pos + 8)   # именно int, см. урок 1
    $pos += 16
    if ($incl -le 0 -or ($pos + $incl) -gt $bytes.Length) { break }
    $f = [int]$pos
    $pos += $incl
    if ($incl -lt 34) { $stub++; continue }                   # вырожденный кадр, см. урок 2
    $t = $sec + $usec / 1000000.0
    $ether = ((([int]$bytes[$f + 12]) -shl 8) -bor [int]$bytes[$f + 13])
    if ($ether -eq 0x0806) { $arp++; continue }
    if ($ether -eq 0x86dd) {
        $v6++
        $src6 = (0..7 | ForEach-Object { '{0:x2}{1:x2}' -f $bytes[$f + 22 + $_ * 2], $bytes[$f + 23 + $_ * 2] }) -join ':'
        $dst6 = (0..7 | ForEach-Object { '{0:x2}{1:x2}' -f $bytes[$f + 38 + $_ * 2], $bytes[$f + 39 + $_ * 2] }) -join ':'
        [void]$pkts.Add([pscustomobject]@{ T = $t; V = 6; Proto = [int]$bytes[$f + 20]; Src = $src6; Dst = $dst6; DPort = 0; Syn = $false })
        continue
    }
    if ($ether -ne 0x0800) { continue }
    $v4++
    $ip = $f + 14
    $ihl = ([int]$bytes[$ip] -band 0x0f) * 4
    $proto = [int]$bytes[$ip + 9]
    $src = ($ip + 12)..($ip + 15) | ForEach-Object { [int]$bytes[$_] }
    $dst = ($ip + 16)..($ip + 19) | ForEach-Object { [int]$bytes[$_] }
    $tr = $ip + $ihl
    $dport = 0; $syn = $false
    if (($proto -eq 6 -or $proto -eq 17) -and ($tr + 14) -le ($f + $incl)) {
        $dport = ((([int]$bytes[$tr + 2]) -shl 8) -bor [int]$bytes[$tr + 3])
        if ($proto -eq 6) {
            $flags = [int]$bytes[$tr + 13]
            $syn = (($flags -band 0x02) -ne 0) -and (($flags -band 0x10) -eq 0)
        }
    }
    [void]$pkts.Add([pscustomobject]@{ T = $t; V = 4; Proto = $proto; Src = ($src -join '.'); Dst = ($dst -join '.'); DPort = $dport; Syn = $syn })
}

"=== разбор ==="
"кадров: $($pkts.Count) (IPv4 $v4, IPv6 $v6, ARP $arp, вырожденных $stub)"
if ($pos -ne $bytes.Length) { throw "Разбор остановился на $pos из $($bytes.Length) — доверять выводам нельзя." }
"поток кончился ровно на последнем байте файла"

""
"=== поиск по сырым байтам ==="
"{0,8}  {1}  (контроль: адрес входа узла)" -f (CountRaw $bytes (AddrBytes $Peer)), $Peer
foreach ($n in $Needle) { "{0,8}  {1}  (не должно встречаться)" -f (CountRaw $bytes (AddrBytes $n)), $n }

$peerPkts = @($pkts | Where-Object { $_.Src -eq $Peer -or $_.Dst -eq $Peer })
if ($peerPkts.Count -eq 0) { throw "В захвате нет кадров с адресом узла $Peer — туннель в это окно не работал." }
$t0 = ($peerPkts | Measure-Object T -Minimum).Minimum
$t1 = ($peerPkts | Measure-Object T -Maximum).Maximum
""
"=== окно ==="
"захват длится 0.00 .. {0:F2} с" -f ($pkts | Measure-Object T -Maximum).Maximum
"обмен с узлом: {0:F2} .. {1:F2} с, кадров {2}" -f $t0, $t1, $peerPkts.Count

# Край окна по кадрам к узлу определяется с запасом: пока туннель снимается, обмен с
# узлом ещё идёт, а трафик уже уходит напрямую. Точную границу видно по таблице ниже,
# и её можно задать вручную через -WindowStart/-WindowEnd.
$ws = if ($WindowStart -ge 0) { $WindowStart } else { $t0 }
$we = if ($WindowEnd -ge 0) { $WindowEnd } else { $t1 }
$how = if ($WindowStart -ge 0 -or $WindowEnd -ge 0) { 'задано вручную' } else { 'по обмену с узлом' }
"окно разбора: {0:F2} .. {1:F2} с ({2})" -f $ws, $we, $how

# Вызывать только как @(Bypass ...): пустой результат иначе приходит как $null.
function Bypass($set) {
    @($set | Where-Object { $_.V -eq 4 -and $_.Dst -ne $Peer -and $_.Src -ne $Peer -and -not (IsHousekeeping $_.Dst) })
}
function Outside6($set) {
    @($set | Where-Object { $_.V -eq 6 -and -not ($_.Dst.StartsWith('fe80') -or $_.Dst.StartsWith('ff02')) })
}

""
"=== темп по интервалам в {0:F0} с ===" -f $Bucket
"       интервал   к узлу   мимо узла   DNS   IPv6-наружу"
for ($a = [Math]::Floor(($t0 - $Bucket) / $Bucket) * $Bucket; $a -lt $t1 + $Bucket * 2; $a += $Bucket) {
    $slice = @($pkts | Where-Object { $_.T -ge $a -and $_.T -lt $a + $Bucket })
    if ($slice.Count -eq 0) { continue }
    $toPeer = @($slice | Where-Object { $_.Src -eq $Peer -or $_.Dst -eq $Peer }).Count
    $dns = @($slice | Where-Object { $_.V -eq 4 -and $_.Proto -eq 17 -and $_.DPort -eq 53 }).Count
    "{0,7:F0}-{1,-7:F0} {2,7} {3,11} {4,5} {5,13}" -f $a, ($a + $Bucket), $toPeer, @(Bypass $slice).Count, $dns, @(Outside6 $slice).Count
}

$win = @($pkts | Where-Object { $_.T -ge $ws -and $_.T -le $we })
$leak = @(Bypass $win)
""
"=== наружу мимо узла внутри окна: $($leak.Count) кадров ==="
if ($leak.Count -eq 0) { "  нет" }
else {
    $leak | Group-Object { "proto$($_.Proto) $($_.Src) -> $($_.Dst):$($_.DPort)" } | Sort-Object Count -Descending |
        ForEach-Object { "{0,6}  {1}  первый на {2:F2} с" -f $_.Count, $_.Name, ($_.Group | Measure-Object T -Minimum).Minimum }
}

# Решающий признак: SYN без ACK — это новое соединение, открытое мимо туннеля.
# Прочие кадры могут быть продолжением сокетов, открытых до его поднятия.
$syns = @($leak | Where-Object { $_.Syn })
""
"=== новые соединения мимо туннеля (SYN без ACK): $($syns.Count) ==="
if ($syns.Count -eq 0) { "  нет — остальное является продолжением соединений, открытых до туннеля" }
else { $syns | Sort-Object T | ForEach-Object { "{0,8:F2} с  -> {1}:{2}" -f $_.T, $_.Dst, $_.DPort } }

$dnsWin = @($win | Where-Object { $_.V -eq 4 -and $_.Proto -eq 17 -and $_.DPort -eq 53 })
""
"=== DNS внутри окна: $($dnsWin.Count) кадров ==="
if ($dnsWin.Count -eq 0) { "  нет — перехват работает" }
else { $dnsWin | Group-Object Dst | ForEach-Object { "{0,6}  -> {1}" -f $_.Count, $_.Name } }

$v6Win = @(Outside6 $win)
""
"=== IPv6 наружу внутри окна: $($v6Win.Count) кадров ==="
if ($v6Win.Count -eq 0) { "  нет, осталось только служебное fe80::/ff02::" }
else {
    $v6Win | Group-Object { "$($_.Src) -> $($_.Dst)" } | Sort-Object Count -Descending | Select-Object -First 10 |
        ForEach-Object { "{0,6}  {1}" -f $_.Count, $_.Name }
}
