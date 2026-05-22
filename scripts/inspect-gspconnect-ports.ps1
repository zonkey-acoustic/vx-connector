# Lists every TCP and UDP endpoint GSPconnect.exe is currently using.
# Use this before starting a Wireshark capture so you know which ports
# (and which protocols) to watch.

$proc = Get-Process -Name GSPconnect -ErrorAction SilentlyContinue
if (-not $proc) {
    Write-Host "GSPconnect.exe is not running." -ForegroundColor Yellow
    Write-Host "Start it (and let it sit for ~5s) before running this script."
    exit 0
}

Write-Host ("GSPconnect.exe (PID {0}) at {1}" -f $proc.Id, $proc.Path) -ForegroundColor Cyan
Write-Host ""

$conns = Get-NetTCPConnection -OwningProcess $proc.Id -ErrorAction SilentlyContinue
$udp   = Get-NetUDPEndpoint   -OwningProcess $proc.Id -ErrorAction SilentlyContinue
if (-not $conns -and -not $udp) {
    Write-Host "No active TCP or UDP sockets found." -ForegroundColor Yellow
    exit 0
}

$listeners = $conns | Where-Object { $_.State -eq 'Listen' } | Sort-Object LocalPort
$established = $conns | Where-Object { $_.State -eq 'Established' } | Sort-Object LocalPort
$other = $conns | Where-Object { $_.State -notin @('Listen','Established') } | Sort-Object LocalPort

if ($listeners) {
    Write-Host "TCP listening on:" -ForegroundColor Green
    $listeners | Format-Table @{N='Local';E={"$($_.LocalAddress):$($_.LocalPort)"}}, State -AutoSize
}

if ($established) {
    Write-Host "TCP established connections:" -ForegroundColor Green
    $established | Format-Table `
        @{N='Local';E={"$($_.LocalAddress):$($_.LocalPort)"}}, `
        @{N='Remote';E={"$($_.RemoteAddress):$($_.RemotePort)"}}, `
        State -AutoSize
}

if ($other) {
    Write-Host "TCP other states:" -ForegroundColor DarkGray
    $other | Format-Table `
        @{N='Local';E={"$($_.LocalAddress):$($_.LocalPort)"}}, `
        @{N='Remote';E={"$($_.RemoteAddress):$($_.RemotePort)"}}, `
        State -AutoSize
}

if ($udp) {
    Write-Host "UDP endpoints (Windows does not track UDP peers):" -ForegroundColor Green
    $udp | Sort-Object LocalPort |
        Format-Table @{N='Local';E={"$($_.LocalAddress):$($_.LocalPort)"}} -AutoSize

    $udpPorts = $udp | ForEach-Object { $_.LocalPort } | Sort-Object -Unique
    if ($udpPorts -contains 5353) {
        Write-Host "  Note: port 5353 = mDNS. GSPconnect is doing service discovery." -ForegroundColor Cyan
    }
}

# Wireshark filter hint
$tcpPorts = $conns | Where-Object { $_.State -eq 'Listen' } |
            ForEach-Object { $_.LocalPort } | Sort-Object -Unique
$udpPorts = if ($udp) { $udp | ForEach-Object { $_.LocalPort } | Sort-Object -Unique } else { @() }

if ($tcpPorts -or $udpPorts) {
    Write-Host ""
    Write-Host "Wireshark capture filter suggestion:" -ForegroundColor Cyan
    $clauses = @()
    foreach ($p in $tcpPorts) { $clauses += "tcp port $p" }
    foreach ($p in $udpPorts) { $clauses += "udp port $p" }
    $sep = ' or '
    $filter = [string]::Join($sep, $clauses)
    Write-Host "  $filter"
}
