# Resets the Infinite Tees listening port from 921 back to 999.
# Use this if you previously ran the old set-itees-port.ps1 script
# and want VX Proxy to run in normal proxy mode (port 921 -> 999).
#
# If iTees is left on Port=921, VX Proxy will auto-detect that and run
# in passthrough mode instead.

$iniPath = "$env:LOCALAPPDATA\InfiniteTees\Saved\Config\Windows\GameUserSettings.ini"

if (-not (Test-Path $iniPath)) {
    Write-Error "GameUserSettings.ini not found at: $iniPath"
    exit 1
}

$content = Get-Content $iniPath -Raw

if ($content -match 'Port=999') {
    Write-Host "Infinite Tees is already on port 999. No changes needed."
    exit 0
}
if ($content -notmatch 'Port=921') {
    Write-Error "Expected Port=921 not found in $iniPath. Nothing to reset."
    exit 1
}

$updated = $content -replace 'Port=921', 'Port=999'
Set-Content $iniPath -Value $updated -NoNewline
Write-Host "Reset Port=921 -> Port=999 in $iniPath"
Write-Host "Restart Infinite Tees for the change to take effect."
