# root-recover.ps1 - 自动修复克隆实例的 root 环境（Magisk system-mode 安装）。
# 复现 references/mumu-manager.md § Clone root recovery 的流程（实测走通，1440x2560）：
#   预授权安装权限 → 启动 Magisk → （stub 机型先经系统安装器装完整版）→
#   安装 → 直接安装（直接修改/system）→ 开始 → 等 "- All done!" →
#   宿主机 adb reboot（不点屏幕「重启」）→ 等开机 → 验证 magisk -v + LSPosed
#
# 坐标全部由 uiautomator dump 实时解析（不硬编码）；点击优先 adb input，
# 失效时回退 nemu SDK（MuMu 二级显示器上 adb input 可能够不到）。
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File root-recover.ps1 -Port 16544 [-Index 5]
#
# Output:  [root-recover] 结果行
#   ALREADY_OK / INSTALLED_OK / ERROR <step>
#   （安装器页面残留前台 = 上次恢复被中断：自动等装完→重启→验证，不再重装）
# Exit:    0 成功（含本来就是好的）/ 1 失败
param(
  [Parameter(Mandatory = $true)][int]$Port,
  [int]$Index = -1,
  [string]$MagiskPackage = 'com.cj.u.tusbfyo',       # 伪装包名（stub / 完整版安装后的包名可能不同）
  [string]$FullAppPackage = 'io.github.huskydg.magisk',
  [string]$NemuPath = '',
  [int]$WaitInstallSec = 180,
  [int]$WaitBootSec = 360
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ErrorActionPreference = 'Continue'

$serial = "127.0.0.1:${Port}"
$script:ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)  # gui/
if (-not $NemuPath) { $NemuPath = Join-Path $script:ProjectRoot 'tools\nemu\nemu.exe' }
$dumpLocal = Join-Path $env:TEMP "root_recover_dump_$Port.xml"

function Fail([string]$step) { Write-Output "[root-recover] ERROR $step"; exit 1 }
function Say([string]$msg) { Write-Output "[root-recover] $msg" }

# ---- adb / UI 基元 ----
# 注意：函数名不要叫 Adb —— PowerShell 解析命令时函数优先于外部程序且大小写不敏感，
# 那会让脚本里所有 `& adb ...` 命中该函数造成无限递归。
function AdbShell([string]$cmd) { return (& adb -s $serial shell $cmd 2>$null | Out-String) }

function Get-UiDump {
  # 拉取到本地再按 UTF-8 读取，中文文本稳定可匹配。
  $null = (& adb -s $serial shell "uiautomator dump /sdcard/_.xml" 2>$null)
  $null = (& adb -s $serial pull /sdcard/_.xml $dumpLocal 2>$null)
  if (-not (Test-Path $dumpLocal)) { return '' }
  return (Get-Content -Path $dumpLocal -Encoding UTF8 -Raw)
}

# 在 dump 中找文本匹配的节点，返回中心坐标 / enabled / checked / 所属包名。
function Find-Node([string]$xmlText, [string]$pattern, [string]$packageLike = '') {
  if (-not $xmlText) { return $null }
  foreach ($chunk in ($xmlText -split '<node ')) {
    if ($chunk -notmatch 'text="([^"]*)"') { continue }
    if ($Matches[1] -notmatch $pattern) { continue }
    if ($packageLike -and ($chunk -notmatch ('package="[^"]*' + [regex]::Escape($packageLike) + '[^"]*"'))) { continue }
    if ($chunk -notmatch 'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"') { continue }
    return [pscustomobject]@{
      X = [int](([int]$Matches[1] + [int]$Matches[3]) / 2)
      Y = [int](([int]$Matches[2] + [int]$Matches[4]) / 2)
      Text = $Matches[1]
      Enabled = ($chunk -match 'enabled="true"')
      Checked = ($chunk -match 'checked="true"')
    }
  }
  return $null
}

function Get-DumpPackage([string]$xmlText) {
  if ($xmlText -match 'package="([^"]+)"') { return $Matches[1] }
  return $MagiskPackage
}

# 点击：优先 adb input；若 dump 仍是旧状态（adb 够不到该显示器），回退 nemu。
function Invoke-Tap([int]$x, [int]$y, [string]$xmlBefore, [string]$pkg) {
  $null = AdbShell "input tap $x $y"
  Start-Sleep -Milliseconds 1200
  $after = Get-UiDump
  if ($after -and $after -ne $xmlBefore) { return $true }   # 界面有变化 = 点击生效

  if ($Index -ge 0 -and (Test-Path $NemuPath)) {
    $null = (& $NemuPath $Index 'tap' $pkg $x $y 2>$null)
    Start-Sleep -Milliseconds 1200
    $after2 = Get-UiDump
    if ($after2 -and $after2 -ne $after) { return $true }
  }
  return $false
}

function Test-RootState {
  $magiskOk = $false
  $lsposedOk = $false
  $mv = (AdbShell "magisk -v").Trim()
  if ($mv -match 'Magisk|MAGISK') { $magiskOk = $true }
  $notif = AdbShell "dumpsys notification --noredact"
  # 主判据是通知标题；若控制台编码不是 UTF-8（中文会被解成乱码），
  # 退回到纯 ASCII 的 LSPosed 通知渠道 id，避免误报缺失。
  if ($notif -match 'LSPosed 已加载') { $lsposedOk = $true }
  elseif ($notif -match 'lsposed_status') { $lsposedOk = $true }
  return [pscustomobject]@{ Magisk = $magiskOk; Lsposed = $lsposedOk; MagiskVersion = $mv }
}

# 等安装日志 "- All done!"（正常恢复 §7 / 中断补完共用）。安装静默 1~3 分钟，
# 没有进度行时 GUI 日志整段沉默，会被当成卡死（实测 2026-11-08），故每 30s 报一次。
function Wait-InstallDone {
  $deadline = (Get-Date).AddSeconds($WaitInstallSec)
  $started = Get-Date
  $nextLog = 30.0
  while ((Get-Date) -lt $deadline) {
    if ((Get-UiDump) -match 'All done') { return $true }
    $elapsed = ((Get-Date) - $started).TotalSeconds
    if ($elapsed -ge $nextLog) {
      Say ("waiting for Magisk install to finish... {0:n0}s" -f $elapsed)
      $nextLog = $elapsed + 30
    }
    Start-Sleep 5
  }
  return $false
}

# 收尾：宿主机重启 → 等开机 → 验证（正常恢复 §8-§10 与中断补完共用）。
# 成功 Say INSTALLED_OK 并 exit 0；失败走 Fail（exit 1）——两个调用点都视失败为终态。
function Complete-Recovery {
  # 宿主机 adb 重启（不要点屏幕上的「重启」按钮）
  $null = (& adb -s $serial reboot 2>$null)
  Start-Sleep 15
  $null = (& adb connect $serial 2>$null)

  # 等待开机完成。重启期间 adb 连接会断开，且设备可能需要 1~2 分钟才重新可连。
  # 只在开头连一次会在设备尚未就绪时白白失败，导致后续 getprop 一直拿不到值、
  # 误报 boot_timeout（实测：设备其实已正常开机）。故在等待循环里持续重连。
  $deadline = (Get-Date).AddSeconds($WaitBootSec)
  $booted = $false
  $nextConnect = [datetime]::MinValue
  $started = Get-Date
  $nextLog = 30.0
  while ((Get-Date) -lt $deadline) {
    if (((Get-Date) - $nextConnect).TotalSeconds -ge 15) {
      $null = (& adb connect $serial 2>$null)
      $nextConnect = Get-Date
    }

    if (((& adb -s $serial get-state 2>$null | Out-String).Trim()) -eq 'device') {
      $bc = (& adb -s $serial shell getprop sys.boot_completed 2>$null | Out-String).Trim()
      if ($bc -eq '1') { $booted = $true; break }
    }

    $elapsed = ((Get-Date) - $started).TotalSeconds
    if ($elapsed -ge $nextLog) {
      Say ("waiting for emulator to boot... {0:n0}s" -f $elapsed)
      $nextLog = $elapsed + 30
    }
    Start-Sleep 6
  }
  if (-not $booted) { Fail 'boot_timeout' }
  Say 'boot completed'

  # 开机后回桌面：部分系统会恢复重启前的前台任务（Magisk 安装器页面），
  # 不清掉会让随后的 adb-prep 复检再次误报 RECOVERY_INCOMPLETE。
  $null = AdbShell "input keyevent KEYCODE_HOME"
  Start-Sleep 2

  # 验证
  Start-Sleep 10  # 等 LSPosed 通知就绪
  $state = Test-RootState
  if ($state.Magisk -and $state.Lsposed) {
    Say "INSTALLED_OK magisk=$($state.MagiskVersion)"
    exit 0
  }

  Fail "verify_failed magisk=$($state.Magisk) lsposed=$($state.Lsposed)"
}

# ---- 0. 前置检查 ----
$null = (& adb disconnect $serial 2>$null)      # 清掉可能残留的 offline 条目
$null = (& adb connect $serial 2>$null)

# 等设备真正就绪：刚 connect / 刚开机时 adb 可能短暂 offline 或非 device 状态，
# 此时查询返回空串，会把「已 root」误判为缺失并触发无谓重装。
# 实测（2026-09-26 批量首单）：offline 能持续超过一次 connect 的有效期 —— 只在
# 开头连一次、后面干等 get-state，90s 整都没等到（ERROR device_not_ready）。
# 故与 §9 等开机循环同一打法：每 15s 断开重连一次，并加 shell 回声检查
# （get-state=device 不代表 adbd 真能应答）。
$ready = $false
$lastState = 'no-response'
$readyDeadline = (Get-Date).AddSeconds(90)
$nextConnect = [datetime]::MinValue
while ((Get-Date) -lt $readyDeadline) {
  if (((Get-Date) - $nextConnect).TotalSeconds -ge 15) {
    $null = (& adb disconnect $serial 2>$null)
    $null = (& adb connect $serial 2>$null)
    $nextConnect = Get-Date
  }

  $stateRaw = (& adb -s $serial get-state 2>$null | Out-String).Trim()
  if ($stateRaw) { $lastState = $stateRaw.Substring(0, [Math]::Min(40, $stateRaw.Length)) }
  if ($stateRaw -eq 'device' -and
      ((& adb -s $serial shell "echo ok" 2>$null | Out-String).Trim() -eq 'ok')) {
    $ready = $true
    break
  }

  Start-Sleep 3
}
if (-not $ready) { Fail "device_not_ready (90s, last state=$lastState)" }

$state = Test-RootState

# ---- 0a+. 恢复中断检测：Magisk 安装器页面还在前台 = 上次恢复装完（或装到一半）没重启。
# 实测（2026-11-08）：装完后被中断的设备 magisk -v 已可用、旧 LSPosed 通知未清，
# 仅靠下面的 ALREADY_OK 检查会误判环境正常，e2e 会在安装器页面上空转到超时。
# 这里把收尾补完：还没装完就等 All done，然后宿主机重启 → 等开机 → 验证。
$dump0 = Get-UiDump
$installRunning = $dump0 -match 'Installing:|Remount system partition'
$installDone = $dump0 -match 'All done'
if ($installDone -or $installRunning) {
  Say 'interrupted recovery detected (magisk installer page foreground)'
  if (-not $installDone -and -not (Wait-InstallDone)) { Fail 'install_timeout' }
  Complete-Recovery
}

if ($state.Magisk -and $state.Lsposed) {
  Say "ALREADY_OK magisk=$($state.MagiskVersion)"
  exit 0
}
Say "start port=$Port index=$Index magisk=$($state.Magisk) lsposed=$($state.Lsposed)"

# ---- 0b. 预授权「安装未知应用」----
# 未授权时点「安装」会被系统重定向到「安装未知应用」设置页，流程无法继续。
$null = AdbShell "cmd appops set $MagiskPackage REQUEST_INSTALL_PACKAGES allow"
$null = AdbShell "cmd appops set $FullAppPackage REQUEST_INSTALL_PACKAGES allow"

# ---- 1. 启动：优先已装的完整版，否则伪装包（stub）----
$installed = AdbShell "pm list packages $FullAppPackage"
$launchPkg = if ($installed -match [regex]::Escape($FullAppPackage)) { $FullAppPackage } else { $MagiskPackage }
Say "launch $launchPkg"
$null = AdbShell "monkey -p $launchPkg -c android.intent.category.LAUNCHER 1"
Start-Sleep 5

# ---- 2~4. 状态机：推进到「直接安装（直接修改/system）」方法页 ----
# 需处理三种中间态：
#   a) stub 机型的系统安装器确认框（「要安装此应用吗」/ com.android.packageinstaller）
#   b) 完整版首次启动的非官方版本警告条（「不再显示」）
#   c) 权限设置页（「安装未知应用」，appops 未生效时）
$methodPattern = '直接安装（直接修改/system）'
$onMethodPage = $false
for ($i = 0; $i -lt 25 -and -not $onMethodPage; $i++) {
  $dump = Get-UiDump
  if ($dump -match [regex]::Escape($methodPattern)) { $onMethodPage = $true; break }

  if ($dump -match '安装未知应用') {
    Say 'permission page; pressing BACK'
    $null = AdbShell 'input keyevent KEYCODE_BACK'
    Start-Sleep 2
    continue
  }

  if ($dump -match 'packageinstaller') {
    $node = Find-Node $dump '^安装$' 'packageinstaller'
    if (-not $node) { $node = Find-Node $dump '^安装$' }
    if ($node) {
      Say "system installer: tap 安装 at $($node.X),$($node.Y)"
      # 安装器确认后需较长时间关闭并拉起完整版；此处不用 Invoke-Tap 的变化判定
      # （对话框关闭慢会被误判为失败而重复点击），改为固定等待后回循环顶部重判。
      $null = AdbShell "input tap $($node.X) $($node.Y)"
      Start-Sleep 12
      $null = AdbShell "monkey -p $FullAppPackage -c android.intent.category.LAUNCHER 1"
      Start-Sleep 5
    }
    continue
  }

  if ($dump -match '不再显示') {
    $node = Find-Node $dump '不再显示'
    if ($node) { Say "dismiss unofficial warning"; $null = Invoke-Tap $node.X $node.Y $dump (Get-DumpPackage $dump) }
    continue
  }

  $installNode = Find-Node $dump '^安装$'
  if ($installNode) {
    Say "tap 安装 at $($installNode.X),$($installNode.Y)"
    $null = Invoke-Tap $installNode.X $installNode.Y $dump (Get-DumpPackage $dump)
    Start-Sleep 3
    continue
  }

  Start-Sleep 2
}
if (-not $onMethodPage) { Fail 'install_method_page_not_reached' }

# ---- 5. 「直接安装（直接修改/system）」radio ----
$tapped = $false
for ($i = 0; $i -lt 10 -and -not $tapped; $i++) {
  $dump = Get-UiDump
  $node = Find-Node $dump $methodPattern
  if ($node) {
    Say "tap $methodPattern at $($node.X),$($node.Y)"
    $tapped = Invoke-Tap $node.X $node.Y $dump (Get-DumpPackage $dump)
    break
  }
  Start-Sleep 2
}
if (-not $tapped) { Fail 'system_mode_radio_not_found' }
Start-Sleep 2

# ---- 6. 「开始」（选中 radio 后变为 enabled）----
$tapped = $false
for ($i = 0; $i -lt 10 -and -not $tapped; $i++) {
  $dump = Get-UiDump
  $node = Find-Node $dump '^开始$'
  if ($node -and $node.Enabled) {
    Say "tap 开始 at $($node.X),$($node.Y)"
    $tapped = Invoke-Tap $node.X $node.Y $dump (Get-DumpPackage $dump)
    break
  }
  Start-Sleep 2
}
if (-not $tapped) { Fail 'start_button_not_enabled' }

# ---- 7. 等待安装日志 "- All done!" ----
if (-not (Wait-InstallDone)) { Fail 'install_timeout' }

# ---- 8~10. 宿主机重启 + 等开机 + 验证（见 Complete-Recovery）----
Complete-Recovery
