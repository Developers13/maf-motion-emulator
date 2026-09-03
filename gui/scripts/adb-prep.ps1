# adb-prep.ps1 - connect to a MuMu emulator, verify it, and check the env.
# Usage:  powershell -ExecutionPolicy Bypass -File adb-prep.ps1 -Port <port>
# Output: compact status lines only (keeps raw adb noise out of the transcript).
#   CONNECTED 127.0.0.1:<port>
#   MODEL <ro.product.model>
#   ANDROID <ro.build.version.release>
#   MAGISK <version> | MAGISK NOT-FOUND
#   LSPOSED LOADED | LSPOSED MISSING
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

# 1. Connect.
# Note: 2>$null (not 2>&1) — with $ErrorActionPreference='Stop', Windows
# PowerShell 5.1 turns redirected native stderr into a terminating error.
adb connect $serial 2>$null | Out-Null
$match = adb devices | Select-String -SimpleMatch $serial
if (-not $match -or ($match.ToString() -match "offline")) {
    Write-Result "FAIL connect $serial (offline or not listed)"
    exit 1
}
Write-Result "CONNECTED $serial"

# 2. Device identity.
$model   = (adb -s $serial shell getprop ro.product.model).Trim()
$android = (adb -s $serial shell getprop ro.build.version.release).Trim()
Write-Result "MODEL $model"
Write-Result "ANDROID $android"

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
