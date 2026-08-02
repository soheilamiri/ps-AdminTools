function Start-Watch {
    <#
    .SYNOPSIS
        Run a command repeatedly on an interval, refreshing the output in place.

    .DESCRIPTION
        A PowerShell equivalent of the Linux 'watch' command. Clears the screen, then runs the
        supplied command every -WaitSeconds, redrawing the result over the previous output so the
        console shows a live view rather than a growing scrollback.

        The header line - which shows the command, the interval, and the time - is rewritten on
        every cycle, so the timestamp always reflects when the data currently on screen was
        collected.

        Press Ctrl+C to stop.

    .PARAMETER Command
        The command to run, supplied as a string and executed with Invoke-Expression. Anything
        involving pipes, arguments, or operators must be wrapped in quotes.

        Note that Invoke-Expression runs whatever it is given: this is fine for a command you type
        yourself, but do not pass it a string built from untrusted input.

    .PARAMETER WaitSeconds
        Seconds to wait between runs. Defaults to 5.

    .PARAMETER Differences
        Switches from overwrite mode to change-log mode. Instead of replacing the previous output,
        each change is appended below the last with a timestamp, split into Added and Removed
        entries so the history of changes stays on screen.

    .PARAMETER NoClear
        Skip the initial screen clear and draw from the current cursor position instead, leaving
        whatever is already on screen intact.

    .EXAMPLE
        Start-Watch -Command Get-Process

        Runs Get-Process every 5 seconds, refreshing in place.

    .EXAMPLE
        Start-Watch -WaitSeconds 5 -Command "Resolve-DnsName -Name chat.lalavij.ir -Server 8.8.8.8"

        Re-resolves a DNS record every 5 seconds. The header timestamp updates with each lookup,
        so it always shows when the record below was last read.

    .EXAMPLE
        Start-Watch "Get-Process | Select-Object -First 12" -Differences -WaitSeconds 3

        Monitors for changes every 3 seconds, appending each change with a timestamp rather than
        overwriting.

    .NOTES
        Based on Watch-Command by Matthew J. DeGarmo (@TechDufus), from the AdminToolkit project:
        https://github.com/TechDufus/AdminToolkit

        Adapted for ps-AdminTools: renamed to Start-Watch for consistency with the module's other
        live console tools, the header timestamp refreshes on every cycle rather than only at
        startup, the screen is cleared before the first run, the header is colourised, and stale
        text from a previous, longer frame is cleared.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string] $Command,

        [Parameter(Position = 1)]
        [ValidateRange(1, 86400)]
        [int] $WaitSeconds = 5,

        [switch] $Differences,

        [switch] $NoClear
    )

    begin {
        # Header colours: label in cyan, timestamp in yellow so the value that changes each
        # cycle is the one that stands out.
        $LabelColour = [System.ConsoleColor]::Cyan
        $TimeColour  = [System.ConsoleColor]::Yellow

        $Output = $null
        $PreviousOutput = $null
        $Difference = $null
        $PreviousLineCount = 0

        try {
            if (-not $NoClear.IsPresent) {
                Clear-Host
            }

            $SaveX = [console]::CursorLeft
            $SaveY = [console]::CursorTop
        }
        catch {
            throw "Start-Watch needs an interactive console; it cannot run with redirected output."
        }
    }

    process {
        try {
            while ($true) {
                [console]::SetCursorPosition($SaveX, $SaveY)

                $Output = (Invoke-Expression -Command $Command -ErrorAction SilentlyContinue)

                # Built fresh each cycle, so the time shown is when the data below was actually
                # collected rather than when watching started.
                $HeaderLabel = "Watching: '$Command' | Interval: $WaitSeconds`s | Last run: "
                $HeaderTime  = [datetime]::Now.ToString('yyyy-MM-dd HH:mm:ss')

                if ($PreviousOutput -and $Output -and $Differences.IsPresent) {
                    $Difference = (Compare-Object $PreviousOutput $Output -PassThru)
                    if ($Difference) {
                        ($PreviousOutput | Out-String).Trim()
                        "|-------------------------------| |-----------------|"
                        "There was a change in the output: $([datetime]::Now)"
                        "|-------------------------------| |-----------------|"

                        $AddedDifferences = $Difference | Where-Object { $_.SideIndicator -eq "=>" }
                        $RemovedDifferences = $Difference | Where-Object { $_.SideIndicator -eq "<=" }
                        if ($AddedDifferences) { "Added:"; ($AddedDifferences | Out-String).Trim(); "" }
                        if ($RemovedDifferences) { "Removed:"; ($RemovedDifferences | Out-String).Trim(); "" }
                        ""

                        # History has been appended above, so re-anchor below it and begin a fresh
                        # redraw region from this point.
                        $SaveX = [console]::CursorLeft
                        $SaveY = [console]::CursorTop
                        $PreviousLineCount = 0
                    }
                }

                if ($Differences.IsPresent) {
                    $PreviousOutput = $Output
                }

                $Width = [console]::WindowWidth - 1
                $Body  = ($Output | Out-String).Trim()
                $BodyLines = @($Body -split "`r?`n")

                # --- header: two colours on one line ---
                if ($HeaderLabel.Length -ge $Width) {
                    Write-Host $HeaderLabel.Substring(0, $Width) -ForegroundColor $LabelColour
                }
                else {
                    Write-Host $HeaderLabel -ForegroundColor $LabelColour -NoNewline
                    # Pad the timestamp out to the window width so a shorter line fully
                    # overwrites whatever occupied that row previously.
                    Write-Host $HeaderTime.PadRight($Width - $HeaderLabel.Length) -ForegroundColor $TimeColour
                }

                # --- body ---
                foreach ($Line in $BodyLines) {
                    if ($Line.Length -gt $Width) {
                        Write-Host $Line.Substring(0, $Width)
                    }
                    else {
                        Write-Host $Line.PadRight($Width)
                    }
                }

                $FrameLineCount = 1 + $BodyLines.Count

                # A previous frame may have been taller; blank the surplus rows so nothing stale
                # is left stranded below the current output.
                for ($i = $FrameLineCount; $i -lt $PreviousLineCount; $i++) {
                    Write-Host (' ' * $Width)
                }

                $PreviousLineCount = [math]::Max($FrameLineCount, $PreviousLineCount)

                Start-Sleep -Seconds $WaitSeconds
            }
        }
        finally {
            $Output = $null
            $PreviousOutput = $null
            $Difference = $null
        }
    }

    end {}
}
