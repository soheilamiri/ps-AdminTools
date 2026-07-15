# ps-ntpcheck

Cross-platform NTP tooling for PowerShell 7+. Runs identically on Windows and Linux — no Npcap, no external dependencies. Includes three cmdlets:

| Command | Purpose | Privileges needed |
|---|---|---|
| [`Test-Time`](#test-time) | Compare a source clock against up to 5 remote NTP servers and flag drift | None |
| [`Get-NtpConf`](#get-ntpconf) | Read the current time, time zone, and active NTP reference | None |
| [`Set-NtpConf`](#set-ntpconf) | Configure the system's NTP server(s) and restart the time service | Administrator (Windows) / root (Linux) |

`Test-Time` and `Get-NtpConf` are read-only and safe to run anytime. `Set-NtpConf` changes system configuration — see its section below before using it.

---

## Test-Time

Compares a source (your local clock, by default, or any NTP server) against up to 5 remote NTP servers, and flags any that drift beyond an allowed offset. Queries NTP servers directly over UDP/123 using a minimal built-in SNTP client.

### Syntax
```powershell
Test-Time [[-Source] <string>] -Remote <string[]> [-MaxOffset <int>] [-Retry <int>] [-Output] [<CommonParameters>]
```

### Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `-Source` | No | Local machine clock | The time to treat as the baseline. Omit it to use the local system clock, or pass an NTP server hostname/IP to query that server instead. |
| `-Remote` | Yes | — | One or more NTP servers to compare against `-Source`. Comma-separated, up to 5. |
| `-MaxOffset` | No | `60` | Maximum allowed drift, in seconds, between `-Source` and each remote before a warning is raised. |
| `-Retry` | No | `1` | Number of times to repeat the full comparison (1–10), with a fixed 2-second pause between attempts. Each attempt reports its own result. |
| `-Output` | No | Off | Suppresses the formatted report and returns only `$true`/`$false` per remote server (`$true` = within `-MaxOffset`) — for use in scripts and pipelines. |

### Examples

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

### Sample output
```
CLD-SOHEILAMIRI : 2026-07-14 14:39:46.334
192.168.247.10  : 2026-07-14 14:37:46.164
192.168.147.11  : ERROR - No response

Result for 192.168.247.10: WARNING - Source has exceeded  MaxOffset with value of 120 in second.
Result for 192.168.147.11: ERROR - No response from NTP server (timeout).
```

Servers that fail to respond (timeout, DNS failure, or invalid response) are reported inline without stopping the rest of the batch. Run with `-Verbose` to see the full underlying exception text for troubleshooting.

---

## Get-NtpConf

Returns the local machine's current time, time zone, and active NTP reference as a structured object. Auto-detects the underlying NTP mechanism: `W32Time` on Windows; `chrony`, `systemd-timesyncd`, or `ntpd` (in that order) on Linux. Read-only — no elevated privileges required.

### Syntax
```powershell
Get-NtpConf [<CommonParameters>]
```

### Returns

A `NtpConfigInfo` object:

| Property | Description |
|---|---|
| `ComputerName` | Local machine name |
| `CurrentTime` | Local system time at the moment of the call |
| `TimeZoneId` | System time zone identifier |
| `TimeZoneDisplayName` | Human-readable time zone name |
| `NtpService` | Detected mechanism: `W32Time`, `chrony`, `systemd-timesyncd`, `ntpd`, or `Unknown` |
| `NtpReference` | The active NTP server/reference currently in use |
| `Stratum` | NTP stratum of the reference, when reported (nullable) |

### Examples

```powershell
Get-NtpConf

Get-NtpConf | Format-List

Get-NtpConf | Select-Object ComputerName, NtpService, NtpReference
```

### Sample output
```
ComputerName        : CLD-SOHEILAMIRI
CurrentTime         : 7/15/2026 9:07:10 AM
TimeZoneId          : Iran Standard Time
TimeZoneDisplayName : (UTC+03:30) Tehran
NtpService          : W32Time
NtpReference        : 192.168.247.10
Stratum             : 4
```

> **Note:** Right after the underlying time service (re)starts — whether from `Restart-Service`, `Set-NtpConf`, or an OS reboot — `NtpReference` may briefly show `Local CMOS Clock` with `Stratum 1` on Windows. This is W32Time's placeholder state before it completes its first poll cycle against the configured server, not a configuration loss. Query again a minute or two later to see the real reference.

---

## Set-NtpConf

Configures the local machine's NTP server list and restarts the relevant time service so the change takes effect. Auto-detects OS and NTP service the same way `Get-NtpConf` does. **Replaces any previously configured servers entirely** — this is not additive.

**Requires elevated privileges:** Administrator on Windows, root on Linux. Fails immediately with a clear error if not elevated — no config files are touched in that case.

### Syntax
```powershell
Set-NtpConf -Server <string[]> [<CommonParameters>]
```

### Parameters

| Parameter | Required | Description |
|---|---|---|
| `-Server` | Yes | One or more NTP server hostnames/IPs, comma-separated (up to 10). Replaces the existing configuration. |

### What it changes, per platform

| Platform/Service | Config location | Restart command |
|---|---|---|
| Windows (W32Time) | Registry, via `w32tm /config /manualpeerlist:...` | `net stop`/`start w32time`, then `w32tm /resync /force` |
| Linux + chrony | `/etc/chrony.conf` or `/etc/chrony/chrony.conf` | `systemctl restart chronyd` (falls back to `chrony`), then `chronyc makestep` |
| Linux + systemd-timesyncd | `/etc/systemd/timesyncd.conf` (`[Time]` section, `NTP=`) | `systemctl restart systemd-timesyncd` |
| Linux + ntpd | `/etc/ntp.conf` | `systemctl restart ntpd` (falls back to `ntp`) |

On success, it returns the refreshed `Get-NtpConf` output so you can verify the change immediately. There is no `-WhatIf`/`-Confirm` support and no automatic backup of the previous config — it applies directly.

### Persistence

Changes are written to permanent configuration (Windows registry / Linux config files), not held in memory — they **survive both a service restart and a full OS reboot**. You can confirm anytime with:
```powershell
# Windows
w32tm /query /configuration

# Linux
Get-NtpConf
```

### Examples

**Windows (run as Administrator)**
```powershell
Set-NtpConf -Server time.windows.com
```

**Linux (run as root)**
```powershell
sudo pwsh -c "Set-NtpConf -Server pool.ntp.org,time.google.com"
```

**Configure and immediately verify drift dropped**
```powershell
Set-NtpConf -Server 192.168.247.10
w32tm /resync /force        # Windows-only: forces an immediate correction attempt
Test-Time -Remote 192.168.247.10
```

### Sample output
```
W32Time configured with 1 server(s) and restarted.
ComputerName        : CLD-SOHEILAMIRI
CurrentTime         : 7/15/2026 8:52:18 AM
TimeZoneId          : Iran Standard Time
TimeZoneDisplayName : (UTC+03:30) Tehran
NtpService          : W32Time
NtpReference        : 192.168.247.10
Stratum             : 4
```

> **Note on convergence speed:** Windows typically *slews* large time corrections gradually rather than stepping the clock instantly, to avoid breaking timestamp-sensitive processes. After configuring a new server, expect the offset reported by `Test-Time` to close in gradually over several poll cycles rather than reaching zero immediately.
