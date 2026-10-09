param(
    [int]$Seconds = 120,
    [string]$Target = '208.67.222.222',
    [string]$Output = "LinkWeaver-continuity-$(Get-Date -Format yyyyMMdd-HHmmss).csv"
)

# Read-only test. Keep the chosen tunnel active and unplug/reconnect Ethernet
# during the capture. Physical probe destinations are intentionally avoided.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$ping = [Net.NetworkInformation.Ping]::new()
$http = [Net.Http.HttpClient]::new()
$http.Timeout = [TimeSpan]::FromSeconds(2)
$rows = [Collections.Generic.List[object]]::new()
$end = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
$lastIpCheck = [DateTimeOffset]::MinValue
$ipTask = $null
$publicIp = ''
$failureStarted = $null
$longestFailureMs = 0
Write-Host "Capturing for $Seconds seconds. Disconnect and reconnect Ethernet several times; keep Wi-Fi connected."
try {
    while ([DateTimeOffset]::UtcNow -lt $end) {
        $now = [DateTimeOffset]::UtcNow
        if ($ipTask -and $ipTask.IsCompleted) {
            if ($ipTask.Status -eq 'RanToCompletion') { $publicIp = $ipTask.Result.Trim() }
            $ipTask = $null
        }
        if (-not $ipTask -and ($now - $lastIpCheck).TotalSeconds -ge 5) {
            $ipTask = $http.GetStringAsync('https://checkip.amazonaws.com')
            $lastIpCheck = $now
        }
        try { $reply = $ping.Send($Target, 300); $success = $reply.Status -eq 'Success' }
        catch { $reply = $null; $success = $false }
        if (-not $success -and -not $failureStarted) { $failureStarted = $now }
        if ($success -and $failureStarted) {
            $longestFailureMs = [Math]::Max($longestFailureMs, ($now - $failureStarted).TotalMilliseconds)
            $failureStarted = $null
        }
        $links = [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
            Where-Object { $_.NetworkInterfaceType -in @('Ethernet','Wireless80211') } |
            ForEach-Object { "$($_.Name)=$($_.OperationalStatus)" }
        $rows.Add([pscustomobject]@{
            TimestampUtc = $now.ToString('O'); Reachable = $success
            RttMs = $(if ($success) { $reply.RoundtripTime } else { '' })
            Status = $(if ($reply) { $reply.Status } else { 'ProbeError' })
            PublicIp = $publicIp; Links = ($links -join ';')
        })
        Write-Host "$($now.ToLocalTime().ToString('HH:mm:ss.fff'))  $success  $publicIp"
        Start-Sleep -Milliseconds 100
    }
}
finally {
    if ($failureStarted) { $longestFailureMs = [Math]::Max($longestFailureMs, ([DateTimeOffset]::UtcNow - $failureStarted).TotalMilliseconds) }
    $rows | Export-Csv -NoTypeInformation -Encoding UTF8 -Path $Output
    $ping.Dispose(); $http.Dispose()
    $ips = @($rows | Where-Object PublicIp | Select-Object -ExpandProperty PublicIp -Unique)
    Write-Host "Saved: $Output"
    Write-Host "Observed failure interval (sampling estimate): $([Math]::Round($longestFailureMs)) ms"
    Write-Host "Observed public IPs: $($ips -join ', ')"
    Write-Host 'A failed ICMP sample can also mean ICMP filtering. Compare with game/session behavior and LinkWeaver history.'
}
