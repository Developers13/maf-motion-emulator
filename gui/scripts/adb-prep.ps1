# adb-prep.ps1 - connect to a MuMu emulator, verify it, and check the env.
# Usage:  powershell -ExecutionPolicy Bypass -File adb-prep.ps1 -Port <port>
# Output: compact status lines only (keeps raw adb noise out of the transcript).
#   CONNECTED 127.0.0.1:<port>
#   MODEL <ro.product.model>
#   ANDROID <ro.build.version.release>
#   MAGISK <version> | MAGISK NOT-FOUND
#   LSPOSED LOADED | LSPOSED MISSING
#   FAIL device_not_ready (90s, last state=<state>)   <- adb never came up
#   FAIL RECOVERY_INCOMPLETE (...)                    <- magisk installer still foreground
# Note: root is NOT checked via su - root hiding denies su to adb shells by
# design; the LSPosed status notification is the root evidence.
# Exit:   0 on success, 1 on any failure.
param(
    [Parameter(Mandatory = $true)]
    [int]$Port
)

# Note: no $ErrorActionPreference='Stop' here — on Windows PowerShell 5.1 that
# turns native-command stderr (e.g. a missing `magisk` binary) into a
# terminating error, which would abort before the MAGISK NOT-FOUND result line.
# The checks below are explicit; adb failures surface as empty output / FAIL.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$serial = "127.0.0.1:${Port}"

function Write-Result([string]$line) {
    Write-Output "[adb-prep] $line"
}

# 1. Connect + readiness gate.
# MuMuManager 的 is_android_started 置位早于 adbd 真正可用（实测 2026-09-26：ensure
# 报 READY 后 adb shell 仍整段 offline 90s+，全部 getprop 返回空，把 root 环境误判
# 成缺失并触发无谓的 root 修复）。这里等设备真正可查询：get-state=device 且 shell
# 有回声；期间每 15s 断开重连一次 —— 与 root-recover.ps1 §9 等开机循环同一打法。
adb connect $serial 2>$null | Out-Null
$ready = $false
$lastState = 'no-response'
$gateDeadline = (Get-Date).AddSeconds(90)
$nextReconnect = (Get-Date).AddSeconds(15)
while ((Get-Date) -lt $gateDeadline) {
    $raw = (& adb -s $serial get-state 2>$null | Out-String).Trim()
    if (-not $raw) {
        # offline 等错误走 stderr（已被丢弃），get-state 的 stdout 为空；
        # adb devices 行的状态（offline/unauthorized/...）在 stdout 上，拿来当诊断。
        $devLine = (adb devices 2>$null | Select-String -SimpleMatch $serial | Out-String).Trim()
        if ($devLine) { $raw = ($devLine -split '\s+')[-1] }
    }
    if ($raw) { $lastState = $raw.Substring(0, [Math]::Min(40, $raw.Length)) }
    if ($lastState -eq 'device' -and
        ((& adb -s $serial shell "echo ok" 2>$null | Out-String).Trim() -eq 'ok')) {
        $ready = $true
        break
    }
    if ((Get-Date) -ge $nextReconnect) {
        $null = (& adb disconnect $serial 2>$null)
        $null = (& adb connect $serial 2>$null)
        $nextReconnect = (Get-Date).AddSeconds(15)
    }
    Start-Sleep 3
}
if (-not $ready) {
    Write-Result "FAIL device_not_ready (90s, last state=$lastState)"
    exit 1
}
Write-Result "CONNECTED $serial"

# 2. Device identity.（就绪门保证设备可查询；adb 出错时 stdout 为空，
#    用 Out-String 归一成空串再 Trim —— 直接 (输出).Trim() 会命中 $null 抛
#    「不能对 Null 值表达式调用方法」，GUI 日志刷屏且检查结果全部失真）
$model   = (& adb -s $serial shell getprop ro.product.model 2>$null | Out-String).Trim()
$android = (& adb -s $serial shell getprop ro.build.version.release 2>$null | Out-String).Trim()
Write-Result "MODEL $model"
Write-Result "ANDROID $android"

# 2b. 半途中断的 root 恢复检测：Magisk 安装器还在前台 = 上次恢复装完（或装到一半）没重启。
# 此状态下 magisk -v 已可用、旧的 LSPosed 通知也仍挂在通知栏，仅靠 §3/§4 会误判环境正常，
# 让 e2e 跑在停在安装器页面的设备上（实测 2026-11-08：StartEmulation 空转 60s 超时、整单失败）。
# 报 RECOVERY_INCOMPLETE，由 GUI 触发 root-recover 补完（等装完 → 重启 → 验证）。
$focus = (adb -s $serial shell "dumpsys window" 2>$null | Select-String "mCurrentFocus|mFocusedApp" | Out-String)
if ($focus -match "magisk|huskydg|tusbfyo") {
    Write-Result "FAIL RECOVERY_INCOMPLETE (magisk installer foreground; reboot pending)"
    exit 1
}

# 3. Magisk / Kitsune check. Absence of "Magisk" in output => NOT-FOUND
# (native stderr dropped for the same reason as above).
$magisk = (adb -s $serial shell "magisk -v" 2>$null | Out-String).Trim()
if ($magisk -match "Magisk") {
    Write-Result "MAGISK $magisk"
}
else {
    Write-Result "MAGISK NOT-FOUND"
}

# 4. LSPosed-loaded check (the real root evidence; hidden root denies su).
$notif = adb -s $serial shell "dumpsys notification --noredact" | Out-String
if ($notif -match "LSPosed 已加载") {
    Write-Result "LSPOSED LOADED"
}
else {
    Write-Result "LSPOSED MISSING"
}

exit 0
