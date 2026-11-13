# MuMu instance lifecycle wrapper around MuMuManager.exe (host-side CLI).
# One action per call; prints machine-readable [mumu] lines; exit 0 ok / 1 error.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action create   -StudentId 20230001
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action ensure   -StudentId 20230001
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action shutdown -StudentId 20230001
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action shutdown-all
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action shutdown-all -KeepStudentId 20230001
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/mumu.ps1 -Action info
#
# create   -> [mumu] CREATED index=<i> name=<student_id>
#             (clones the 模板 template and renames the copy to student_id;
#              does NOT launch; errors if the name is already taken)
# ensure   -> [mumu] READY index=<i> port=<adb_port> name=<student_id>
#             (finds the instance; if missing clones the 模板 template and
#              renames the copy; launches; polls until Android booted)
# shutdown -> [mumu] STOPPED index=<i>
# shutdown-all -> [mumu] SHUTDOWN index=<i> name=<n> per stopped instance, then
#             [mumu] SHUTDOWN-ALL done count=<n>; with -KeepStudentId that
#             student's instance is left running (模板 is never touched)
# info     -> one [mumu] index=... line per instance
param(
  [Parameter(Mandatory = $true)][ValidateSet('create', 'info', 'ensure', 'shutdown', 'shutdown-all')][string]$Action,
  [string]$StudentId = '',
  [int]$Index = -1,
  [string]$KeepStudentId = '',
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

# 解析实例 adb 端口。
# 首选 `adb -c connect`：MuMu 自己完成连接并回显权威端口（JSON: adb_host/adb_port），
# 不依赖 info 的字段时序，也不会猜错端口。
# 退路：轮询 `info -v <index>` 的 adb_port（MuMu 会在开机后回填，立即读取可能为空）。
# 两者都拿不到时明确报错——绝不猜测端口，避免连到别的实例。
function Resolve-AdbPort([string]$i) {
  try {
    $raw = (& $ManagerPath 'adb' '-v' $i '-c' 'connect' 2>$null) -join "`n"
    $json = $raw | ConvertFrom-Json
    if ($json -and $json.adb_port) { return [int]$json.adb_port }
  } catch { }

  $deadline = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $deadline) {
    $one = Get-One $i
    if ($one -and $one.adb_port) { return [int]$one.adb_port }
    Start-Sleep 5
  }

  Write-Output "[mumu] ERROR adb_port_unresolved index=$i"
  exit 1
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
    Write-Output ("[mumu] READY index={0} port={1} name={2}" -f $idx, (Resolve-AdbPort $idx), $StudentId)
    exit 0
  }
  $null = (& $ManagerPath 'control' '-v' $idx 'launch') -join "`n"
  $deadline = (Get-Date).AddSeconds($BootTimeoutSec)
  $one = $null
  $bootWaited = 0
  while ((Get-Date) -lt $deadline) {
    Start-Sleep 10
    $bootWaited += 10
    $one = Get-One $idx
    if ($one -and $one.is_android_started) { break }
    # 冷启动要 1~5 分钟且期间无任何输出，GUI 日志整段沉默会被当成卡死（实测 2026-11-08）。
    if (($bootWaited % 30) -eq 0) { Write-Output "[mumu] booting index=$idx waited=${bootWaited}s" }
  }
  if (-not ($one -and $one.is_android_started)) { Write-Output "[mumu] ERROR boot_timeout index=$idx"; exit 1 }
  Write-Output ("[mumu] READY index={0} port={1} name={2}" -f $idx, (Resolve-AdbPort $idx), $StudentId)
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

# 关闭所有正在运行的实例（批量运行的收尾 / 逐个处理前清场）。
# 模板实例永不关闭：它是克隆源，且不属于任何学号。
# -KeepStudentId 用于「只保留当前要处理的学号，关掉其余」。
# 已在运行但 Android 尚未开机的实例同样会关（进程已占用资源）。
if ($Action -eq 'shutdown-all') {
  $all = Get-All
  if (-not $all) { Write-Output '[mumu] ERROR info_failed'; exit 1 }
  $targets = @($all.PSObject.Properties | Where-Object {
      $_.Value.is_process_started -and
      $_.Value.name -ne $Template -and
      (-not $KeepStudentId -or $_.Value.name -ne $KeepStudentId)
    })
  if ($targets.Count -eq 0) {
    Write-Output '[mumu] SHUTDOWN-ALL done count=0 failed=0'
    exit 0
  }
  $stopped = 0
  $failedCount = 0
  foreach ($t in $targets) {
    $idx = $t.Name
    $name = $t.Value.name
    $null = (& $ManagerPath 'control' '-v' $idx 'shutdown') -join "`n"
    $deadline = (Get-Date).AddSeconds($StopTimeoutSec)
    $done = $false
    while ((Get-Date) -lt $deadline) {
      Start-Sleep 3
      $one = Get-One ([string]$idx)
      if ($one -and -not $one.is_process_started) { $done = $true; break }
    }
    if ($done) {
      $stopped++
      Write-Output "[mumu] SHUTDOWN index=$idx name=$name"
    } else {
      $failedCount++
      Write-Output "[mumu] WARN shutdown_timeout index=$idx name=$name"
    }
  }
  Write-Output "[mumu] SHUTDOWN-ALL done count=$stopped failed=$failedCount"
  exit 0
}
