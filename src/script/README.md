# script

Plain PowerShell script functions — no compilation needed, no `.dll`. Just `.ps1` files dot-sourced directly by `PS-AdminTools.psm1`. Cross-platform (Windows & Linux) unless noted otherwise.

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
Import-OpenStackRCFile -Path .\openstack_rcfile.sh
```
```
OpenStack RC file imported successfully from '...\openstack_rcfile.sh' (10 variable(s) set).
OpenStack Endpoint : http://cld-epanel.openstack.local
```

**RC file that reads the password interactively — prompts for it**
```powershell
Import-OpenStackRCFile -Path .\openstack-Sharepoint-openrc.sh
```
```
Enter OpenStack password for user 's.amiri' (project 'openstack-Sharepoint'): ****************
OpenStack RC file imported successfully from '...\openstack-Sharepoint-openrc.sh' (10 variable(s) set).
OpenStack Endpoint : http://cld-epanel.openstack-infra.local
```

**Capture the result for use in a script**
```powershell
$conn = Import-OpenStackRCFile -Path .\openstack_RCfile.sh
Write-Host "Connected to $($conn.Endpoint) as $($conn.Username)"
```

### Security note

`OS_PASSWORD` ultimately has to end up as plaintext in the environment variable, since that's what the `openstack` CLI itself reads — this is inherent to how OpenStack RC files work, not something this function can avoid. The function's contribution is only avoiding an *unnecessary* additional exposure: the password is never echoed to the console, and if it's read interactively, it's captured via a masked `SecureString` prompt rather than plain text.
