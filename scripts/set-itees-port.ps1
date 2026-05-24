# Sets the Infinite Tees listening port from 999 to 921 so ProTee Labs talks
# straight to Infinite Tees without anything in the data path. Power-user
# setup for the undocumented Direct mode (run vx-connector.exe --direct).
#
# Run scripts/reset-itees-port.ps1 to undo (921 -> 999).

$iniPath = "$env:LOCALAPPDATA\InfiniteTees\Saved\Config\Windows\GameUserSettings.ini"

if (-not (Test-Path $iniPath)) {
    Write-Error "GameUserSettings.ini not found at: $iniPath"
    exit 1
}

$content = Get-Content $iniPath -Raw

if ($content -match 'Port=921') {
    Write-Host "Infinite Tees is already on port 921. No changes needed."
    exit 0
}
if ($content -notmatch 'Port=999') {
    Write-Error "Expected Port=999 not found in $iniPath. Cannot set 921."
    exit 1
}

$updated = $content -replace 'Port=999', 'Port=921'
Set-Content $iniPath -Value $updated -NoNewline
Write-Host "Set Port=999 -> Port=921 in $iniPath"
Write-Host "Restart Infinite Tees for the change to take effect."
