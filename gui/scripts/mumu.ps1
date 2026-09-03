# MuMu instance lifecycle wrapper around MuMuManager.exe (host-side CLI).
# One action per call; prints machine-readable [mumu] lines; exit 0 ok / 1 error.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action create   -StudentId 20230001
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action ensure   -StudentId 20230001
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action shutdown -StudentId 20230001
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action info
#
# create   -> [mumu] CREATED index=<i> name=<student_id>
#             (clones the 模板 template and renames the copy to student_id;
#              does NOT launch; errors if the name is already taken)
# ensure   -> [mumu] READY index=<i> port=<adb_port> name=<student_id>
#             (finds the instance; if missing clones the 模板 template and
#              renames the copy; launches; polls until Android booted)
# shutdown -> [mumu] STOPPED index=<i>
# info     -> one [mumu] index=... line per instance
param(
  [Parameter(Mandatory = $true)][ValidateSet('create', 'info', 'ensure', 'shutdown')][string]$Action,
  [string]$StudentId = '',
  [int]$Index = -1,
  [string]$Template = '模板',
  [int]$BootTimeoutSec = 300,
  [int]$StopTimeoutSec = 120,
  [string]$ManagerPath = 'C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe'
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not (Test-Path $ManagerPath)) { Write-Output '[mumu] ERROR manager_not_found'; exit 1 }

function Get-All {
  try { return ((& $ManagerPath 'info' '-v' 'all') -join "`n" | ConvertFrom-Json) } catch { return $null }
}
function Get-One([string]$i) {
  try { return ((& $ManagerPath 'info' '-v' $i) -join "`n" | ConvertFrom-Json) } catch { return $null }
}

# Clone the $Template instance and rename the copy to $StudentId (no launch).
# Returns the new index as the only function output; exits 1 on error.
# Callers print "[mumu] CLONED index=<i>" themselves (function stdout becomes
# the return value in PowerShell).
function Invoke-CloneTemplate {
  if (-not $StudentId) { Write-Output '[mumu] ERROR student_id_required'; exit 1 }
  $all = Get-All
  if (-not $all) { Write-Output '[mumu] ERROR info_failed'; exit 1 }
  if (@($all.PSObject.Properties | Where-Object { $_.Value.name -eq $StudentId }).Count -ge 1) {
    Write-Output '[mumu] ERROR already_exists'
    exit 1
  }
  $tpl = @($all.PSObject.Properties | Where-Object { $_.Value.name -eq $Template })
  if ($tpl.Count -eq 0) { $tpl = @($all.PSObject.Properties | Where-Object { $_.Value.name -like "*$Template*" }) }
  if ($tpl.Count -eq 0) { Write-Output '[mumu] ERROR template_not_found'; exit 1 }
  $before = @($all.PSObject.Properties | ForEach-Object { $_.Name })
  $null = (& $ManagerPath 'clone' '-v' $tpl[0].Name) -join "`n"
  Start-Sleep 3
  $all2 = Get-All
  $new = @($all2.PSObject.Properties | Where-Object { $before -notcontains $_.Name })
  if ($new.Count -lt 1) { Write-Output '[mumu] ERROR clone_failed'; exit 1 }
  $idx = $new[0].Name
  $null = (& $ManagerPath 'rename' '-v' $idx '-n' $StudentId) -join "`n"
  return $idx
}

if ($Action -eq 'info') {
  $all = Get-All
  if (-not $all) { Write-Output '[mumu] ERROR info_failed'; exit 1 }
  $all.PSObject.Properties | ForEach-Object {
    Write-Output ("[mumu] index={0} name={1} started={2} port={3}" -f $_.Name, $_.Value.name, $_.Value.is_android_started, $_.Value.adb_port)
  }
  exit 0
}

if ($Action -eq 'create') {
  $idx = Invoke-CloneTemplate
  Write-Output "[mumu] CLONED index=$idx"
  Write-Output "[mumu] CREATED index=$idx name=$StudentId"
  exit 0
}

if ($Action -eq 'ensure') {
  $all = Get-All
  if (-not $all) { Write-Output '[mumu] ERROR info_failed'; exit 1 }
  $hit = @($all.PSObject.Properties | Where-Object { $_.Value.name -eq $StudentId })
  $idx = $null
  if ($hit.Count -ge 1) {
    $idx = $hit[0].Name
  } else {
    $idx = Invoke-CloneTemplate
    Write-Output "[mumu] CLONED index=$idx"
  }
  $one = Get-One $idx
  if ($one -and $one.is_android_started) {
    Write-Output ("[mumu] READY index={0} port={1} name={2}" -f $idx, $one.adb_port, $StudentId)
    exit 0
  }
  $null = (& $ManagerPath 'control' '-v' $idx 'launch') -join "`n"
  $deadline = (Get-Date).AddSeconds($BootTimeoutSec)
  $one = $null
  while ((Get-Date) -lt $deadline) {
    Start-Sleep 10
    $one = Get-One $idx
    if ($one -and $one.is_android_started) { break }
  }
  if (-not ($one -and $one.is_android_started)) { Write-Output "[mumu] ERROR boot_timeout index=$idx"; exit 1 }
  Write-Output ("[mumu] READY index={0} port={1} name={2}" -f $idx, (Get-One $idx).adb_port, $StudentId)
  exit 0
}

if ($Action -eq 'shutdown') {
  if ($Index -lt 0) {
    if (-not $StudentId) { Write-Output '[mumu] ERROR need_index_or_student_id'; exit 1 }
    $all = Get-All
    if (-not $all) { Write-Output '[mumu] ERROR info_failed'; exit 1 }
    $hit = @($all.PSObject.Properties | Where-Object { $_.Value.name -eq $StudentId })
    if ($hit.Count -lt 1) { Write-Output '[mumu] ERROR instance_not_found'; exit 1 }
    $Index = [int]$hit[0].Name
  }
  $null = (& $ManagerPath 'control' '-v' $Index 'shutdown') -join "`n"
  $deadline = (Get-Date).AddSeconds($StopTimeoutSec)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep 5
    $one = Get-One ([string]$Index)
    if ($one -and -not $one.is_process_started) { Write-Output "[mumu] STOPPED index=$Index"; exit 0 }
  }
  Write-Output "[mumu] ERROR stop_timeout index=$Index"; exit 1
}
