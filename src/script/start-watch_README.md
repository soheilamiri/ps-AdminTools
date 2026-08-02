# script

Plain PowerShell script functions — no compilation needed, no `.dll`. Just `.ps1` files dot-sourced directly by `PS-AdminTools.psm1`. Cross-platform (Windows & Linux) unless noted otherwise.

| Command | Description |
|---|---|
| [`Start-Watch`](#start-watch) | Run any command on an interval, refreshing the output in place |
| [`Import-OpenStackRCFile`](#import-openstackrcfile) | Parse an OpenStack RC shell script into PowerShell environment variables |

---

## Start-Watch

A PowerShell equivalent of the Linux `watch` command. Clears the screen, then re-runs a command every few seconds, redrawing the result over the previous output so you get a live view instead of a growing scrollback.

Press `Ctrl+C` to stop.

### Syntax
```powershell
Start-Watch [-Command] <string> [[-WaitSeconds] <int>] [-Differences] [-NoClear] [<CommonParameters>]
```

### Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `-Command` | Yes | — | The command to run, as a string. Anything with pipes, arguments, or operators must be quoted. |
| `-WaitSeconds` | No | `5` | Seconds to wait between runs (1–86400). |
| `-Differences` | No | Off | Change-log mode: instead of overwriting, appends each change below the last with a timestamp, split into Added and Removed entries. |
| `-NoClear` | No | Off | Skip the initial screen clear and draw from the current cursor position. |

### Examples

**Watch a DNS record**
```powershell
Start-Watch -WaitSeconds 5 -Command "Resolve-DnsName -Name example.com -Server 8.8.8.8"
```

**Watch another ps-AdminTools command**
```powershell
Start-Watch -WaitSeconds 30 -Command "Test-Time -Remote time.windows.com"
Start-Watch -WaitSeconds 60 -Command "Get-SslInfo -Url example.com"
```

**Track changes rather than overwriting**
```powershell
Start-Watch "Get-Process | Select-Object -First 12" -Differences -WaitSeconds 3
```

### Sample output
```
Watching: 'Resolve-DnsName -Name chat.lalavij.ir -Server 8.8.8.8' | Interval: 5s | Last run: 2026-08-02 21:25:39

Name                Type  TTL   Section    IPAddress
----                ----  ---   -------    ---------
chat.lalavij.ir     A     190   Answer     185.37.54.252
```

The header label renders in cyan and the timestamp in yellow. That timestamp is rewritten every cycle, so it always reflects when the data below it was collected — not when watching began.

### Notes

- **Needs an interactive console.** It repositions the cursor to redraw, so it can't run with redirected output. It fails with a clear message rather than producing escape-code noise.
- **`-Command` runs through `Invoke-Expression`.** That's what makes the string interface work, but it executes whatever it's given — fine for a command you type, not for one built from untrusted input.
- **Errors are suppressed** (`-ErrorAction SilentlyContinue`), so a failing command shows as blank output. Test the command on its own first if nothing appears.
- **`-Differences` compares via `Compare-Object`**, which matches on the string form of each object. For `Get-Process` that detects processes appearing and disappearing, but not CPU or memory changing, since those stringify identically.

Based on [`Watch-Command`](https://github.com/TechDufus/AdminToolkit) by Matthew J. DeGarmo (@TechDufus).

---

## Import-OpenStackRCFile

Parses an OpenStack RC ("openrc") shell script — the kind downloaded from Horizon's "Download OpenStack RC File" — and imports it as PowerShell environment variables, without needing bash or the file to actually be executed.

### Syntax
```powershell
Import-OpenStackRCFile -Path <string>
```

### What it does
1. Reads every `export KEY=VALUE` line in the file generically (not hard-coded to specific `OS_*` names), so it works with RC files from any OpenStack project. Comments, blank lines, and `unset`/`if [ -z ... ]` guard lines are ignored.
2. Sets each as a PowerShell environment variable (`$env:KEY`) via the `Env:` drive — works identically on Windows and Linux.
3. Strips a single layer of surrounding quotes from values.
4. **Handles RC files that read the password interactively in bash**, e.g.:
   ```bash
   read -sr OS_PASSWORD_INPUT
   export OS_PASSWORD=$OS_PASSWORD_INPUT
   ```
   Since this function only parses the file rather than executing it, `$OS_PASSWORD_INPUT` can't be resolved to a real value. It's detected as an unresolved shell variable reference and treated the same as `OS_PASSWORD` being missing entirely.
5. If `OS_PASSWORD` is missing, empty, or unresolved, prompts interactively with a masked `Read-Host -AsSecureString` — the literal placeholder text is never imported as if it were a real password.
6. Reports a success message and the OpenStack endpoint (`OS_AUTH_URL` with the trailing `:port` stripped), and returns a summary object.

### Parameters

| Parameter | Required | Description |
|---|---|---|
| `-Path` | Yes | Path to the OpenStack RC shell script (`.sh`) to import. |

### Returns

A `PSCustomObject`:

| Property | Description |
|---|---|
| `Endpoint` | `OS_AUTH_URL` with the trailing port stripped |
| `ProjectName` | Value of `OS_PROJECT_NAME` |
| `Username` | Value of `OS_USERNAME` |
| `VariablesImported` | Count of environment variables set |
| `SourceFile` | Resolved full path of the file that was imported |

### Examples

**RC file with a literal password — imports directly, no prompt**
```powershell
Import-OpenStackRCFile -Path .\Fanap-kish.sh
```
```
OpenStack RC file imported successfully from '...\Fanap-kish.sh' (10 variable(s) set).
OpenStack Endpoint : http://cld-epanel.Fanap-infra.local
```

**RC file that reads the password interactively — prompts for it**
```powershell
Import-OpenStackRCFile -Path .\Fanap-Sharepoint-openrc.sh
```
```
Enter OpenStack password for user 's.amiri' (project 'Fanap-Sharepoint'): ****************
OpenStack RC file imported successfully from '...\Fanap-Sharepoint-openrc.sh' (10 variable(s) set).
OpenStack Endpoint : http://cld-epanel.Fanap-infra.local
```

**Capture the result for use in a script**
```powershell
$conn = Import-OpenStackRCFile -Path .\Fanap-kish.sh
Write-Host "Connected to $($conn.Endpoint) as $($conn.Username)"
```

### Security note

`OS_PASSWORD` ultimately has to end up as plaintext in the environment variable, since that's what the `openstack` CLI itself reads — this is inherent to how OpenStack RC files work, not something this function can avoid. The function's contribution is only avoiding an *unnecessary* additional exposure: the password is never echoed to the console, and if it's read interactively, it's captured via a masked `SecureString` prompt rather than plain text.
