# Test-Time

Cross-platform NTP time-drift checker. Compares a source (your local clock, by default, or any NTP server) against up to 5 remote NTP servers, and flags any that drift beyond an allowed offset.

Runs identically on Windows and Linux under PowerShell 7+ — no Npcap, no admin/root privileges, no external dependencies. It queries NTP servers directly over UDP/123 using a minimal built-in SNTP client.

## Syntax
```powershell
Test-Time [[-Source] <string>] -Remote <string[]> [-MaxOffset <int>] [-Retry <int>] [-Output] [<CommonParameters>]
```

## Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `-Source` | No | Local machine clock | The time to treat as the baseline. Omit it to use the local system clock, or pass an NTP server hostname/IP to query that server instead. |
| `-Remote` | Yes | — | One or more NTP servers to compare against `-Source`. Comma-separated, up to 5. |
| `-MaxOffset` | No | `60` | Maximum allowed drift, in seconds, between `-Source` and each remote before a warning is raised. |
| `-Retry` | No | `1` | Number of times to repeat the full comparison (1–10), with a fixed 2-second pause between attempts. Each attempt reports its own result. |
| `-Output` | No | Off | Suppresses the formatted report and returns only `$true`/`$false` per remote server (`$true` = within `-MaxOffset`) — for use in scripts and pipelines. |

## Examples

**Compare local clock against a single NTP server**
```powershell
Test-Time -Remote time.windows.com
```

**Compare two NTP servers against each other**
```powershell
Test-Time -Source 192.168.247.10 -Remote time.windows.com -MaxOffset 5
```

**Check multiple remotes in one call**
```powershell
Test-Time -Remote 192.168.247.10,192.168.247.11,time.windows.com -MaxOffset 110
```

**Retry 3 times with a custom tolerance**
```powershell
Test-Time -Remote time.windows.com -MaxOffset 30 -Retry 3
```

**Scripted pass/fail check (no report text, just booleans)**
```powershell
$results = Test-Time -Remote 192.168.247.10,192.168.247.11 -Output
if ($results -contains $false) {
    Write-Warning "One or more NTP sources are out of tolerance"
}
```

## Sample output
```
CLD-SOHEILAMIRI : 2026-07-14 14:39:46.334
192.168.247.10  : 2026-07-14 14:37:46.164
192.168.147.11  : ERROR - No response

Result for 192.168.247.10: WARNING - Source has exceeded  MaxOffset with value of 120 in second.
Result for 192.168.147.11: ERROR - No response from NTP server (timeout).
```

Servers that fail to respond (timeout, DNS failure, or invalid response) are reported inline without stopping the rest of the batch. Run with `-Verbose` to see the full underlying exception text for troubleshooting.
