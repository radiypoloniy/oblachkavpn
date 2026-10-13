# Аудит соединений WFP в госте: какая программа куда вышла и каким фильтром это решено.
# Время — по часам гостя, теми же, что в шагах помощника отчёта --system-tunnel-check,
# поэтому края окна сопоставляются без захвата снаружи.
#   .\wfp-audit.ps1 -Enable            до прогона (права администратора)
#   .\wfp-audit.ps1 -Since <время>     после прогона: вывод исходящих соединений
# Фильтр «исходящее по умолчанию разрешено» и наш запрет различаются по номеру filter=:
# пока блокировка стоит, запреты идут номером запрещающего фильтра.
param([switch]$Enable, [switch]$Disable, [datetime]$Since)
$audit = '{0CCE9226-69AE-11D9-BED3-505054503030}'
if ($Enable) { auditpol /set /subcategory:$audit /success:enable /failure:enable | Out-Null; return }
if ($Disable) { auditpol /set /subcategory:$audit /success:disable /failure:disable | Out-Null; return }
$auditStart = $Since
$rows = Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 5156, 5157; StartTime = $auditStart } -ErrorAction SilentlyContinue | Sort-Object TimeCreated | ForEach-Object {
  $x = [xml]$_.ToXml(); $d = @{}; foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
  if ($d.Direction -ne '%%14593') { return }
  $a = $d.DestAddress
  if ($a -match '^(127\.|10\.|::1|fe80|ff02|224\.|239\.|255\.|0\.0|172\.31\.255|fdfe)') { return }
  $_.TimeCreated.ToUniversalTime().ToString('HH:mm:ss.fff') + ' ' + $(if ($_.Id -eq 5156) { 'ALLOW' } else { 'BLOCK' }) + ' ' + $a + ':' + $d.DestPort + ' p' + $d.Protocol + ' ' + (Split-Path $d.Application -Leaf) + ' filter=' + $d.FilterRTID
}
$rows
