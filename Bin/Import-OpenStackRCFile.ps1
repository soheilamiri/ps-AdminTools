function Import-OpenStackRCFile {
    <#
    .SYNOPSIS
        Imports an OpenStack RC ("openrc") shell script as PowerShell environment variables.

    .DESCRIPTION
        Parses every `export KEY=VALUE` line in an OpenStack RC file (e.g. as downloaded from
        Horizon's "Download OpenStack RC File") and sets each as a PowerShell environment
        variable ($env:KEY). Works generically - it does not hard-code specific OS_* variable
        names, so it picks up whatever the RC file actually exports.

        Comments, blank lines, and `unset`/`if [ -z ... ]` guard lines are ignored, since those
        have no literal value to import.

        Some RC files don't embed a literal OS_PASSWORD value and instead read it interactively
        in bash, e.g.:
            read -sr OS_PASSWORD_INPUT
            export OS_PASSWORD=$OS_PASSWORD_INPUT
        Since this function only parses the file (it doesn't execute it as bash), a value like
        that is an unresolved shell variable reference, not a real password. Import-OpenStackRCFile
        detects this pattern and treats it the same as OS_PASSWORD being missing entirely: it
        prompts interactively with a masked SecureString read instead of importing the literal
        text "$OS_PASSWORD_INPUT".

        Uses the Env: PSDrive rather than any Windows-specific API, so it works the same way on
        Windows and Linux PowerShell 7+.

    .PARAMETER Path
        Path to the OpenStack RC shell script (.sh) to import.

    .EXAMPLE
        Import-OpenStackRCFile -Path .\Fanap-kish.sh

        Imports every exported variable from the file. Since this file has a literal OS_PASSWORD
        value, no prompt is shown.

    .EXAMPLE
        Import-OpenStackRCFile -Path .\Fanap-Sharepoint-openrc.sh

        Imports every exported variable. Since this file's OS_PASSWORD line is an unresolved
        shell variable reference (from an interactive `read` in bash), the user is prompted for
        the password instead.

    .EXAMPLE
        $result = Import-OpenStackRCFile -Path .\Fanap-kish.sh
        Write-Host "Connected to $($result.Endpoint) as $($result.Username)"

        Captures the returned summary object for use in a larger script.

    .OUTPUTS
        PSCustomObject with Endpoint, ProjectName, Username, VariablesImported, and SourceFile.
    #>
    [CmdletBinding()]
    [OutputType([PSCustomObject])]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "OpenStack RC file not found: $Path"
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $lines = Get-Content -LiteralPath $resolvedPath

    # Matches: export KEY=VALUE  (VALUE optionally single/double-quoted, or empty)
    $exportPattern = '^\s*export\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$'
    # Detects a value that is itself an unresolved shell variable reference, e.g. $FOO or ${FOO}
    $shellVarRefPattern = '^\$\{?[A-Za-z_][A-Za-z0-9_]*\}?$'

    $imported = [ordered]@{}
    $skipped = [System.Collections.Generic.List[string]]::new()

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) {
            continue
        }

        $match = [regex]::Match($trimmed, $exportPattern)
        if (-not $match.Success) {
            continue
        }

        $key = $match.Groups[1].Value
        $value = $match.Groups[2].Value.Trim()

        # Strip a single layer of matching quotes: "value" or 'value' -> value
        if ($value.Length -ge 2 -and (
            ($value.StartsWith('"') -and $value.EndsWith('"')) -or
            ($value.StartsWith("'") -and $value.EndsWith("'"))
        )) {
            $value = $value.Substring(1, $value.Length - 2)
        }

        if ([string]::IsNullOrEmpty($value)) {
            $skipped.Add($key)
            continue
        }

        # A value like $OS_PASSWORD_INPUT can't be resolved from a static parse of the file -
        # it only has a real value once bash actually executes the preceding `read` command.
        if ($value -match $shellVarRefPattern) {
            $skipped.Add($key)
            continue
        }

        Set-Item -Path "Env:$key" -Value $value
        $imported[$key] = $value
    }

    # OS_PASSWORD fallback: prompt if it was missing, empty, or an unresolved shell reference.
    if (-not $imported.Contains('OS_PASSWORD')) {
        $promptUser = if ($imported.Contains('OS_USERNAME')) { $imported['OS_USERNAME'] } else { $env:OS_USERNAME }
        $promptProject = if ($imported.Contains('OS_PROJECT_NAME')) { $imported['OS_PROJECT_NAME'] } else { $env:OS_PROJECT_NAME }

        $securePassword = Read-Host -Prompt "Enter OpenStack password for user '$promptUser' (project '$promptProject')" -AsSecureString
        $plainPassword = [System.Net.NetworkCredential]::new('', $securePassword).Password

        Set-Item -Path 'Env:OS_PASSWORD' -Value $plainPassword
        # Store a masked placeholder in the summary hashtable - never the real secret -
        # purely so the variable count and success report are accurate.
        $imported['OS_PASSWORD'] = '********'
    }

    if (-not $imported.Contains('OS_AUTH_URL') -or [string]::IsNullOrEmpty($imported['OS_AUTH_URL'])) {
        Write-Warning 'OS_AUTH_URL was not found in the RC file - OpenStack Endpoint cannot be shown.'
        $endpoint = $null
    }
    else {
        # Strip a trailing :port from the endpoint, e.g. http://host:5000 -> http://host
        $endpoint = [regex]::Replace($imported['OS_AUTH_URL'], ':\d+$', '')
    }

    Write-Host "OpenStack RC file imported successfully from '$resolvedPath' ($($imported.Count) variable(s) set)." -ForegroundColor Green
    if ($endpoint) {
        Write-Host "OpenStack Endpoint : $endpoint" -ForegroundColor Cyan
    }
    if ($skipped.Count -gt 0) {
        Write-Verbose "Skipped empty/unresolved value(s) for: $($skipped -join ', ')"
    }

    [PSCustomObject]@{
        Endpoint          = $endpoint
        ProjectName       = $imported['OS_PROJECT_NAME']
        Username          = $imported['OS_USERNAME']
        VariablesImported = $imported.Count
        SourceFile        = $resolvedPath
    }
}
