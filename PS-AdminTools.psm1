#Requires -Version 7.0
Set-StrictMode -Version Latest

# ── Resolve paths ─────────────────────────────────────────────────────────────
$ModuleRoot = $PSScriptRoot
$BinPath    = Join-Path $ModuleRoot 'Bin'

if (-not (Test-Path $BinPath)) {
    throw "Bin folder not found at: $BinPath"
}

# ── Store BinPath in AppDomain so the resolver closure can reach it ───────────
[System.AppDomain]::CurrentDomain.SetData('SysAdminToolsBinPath', $BinPath)

# ── Register assembly resolver once per process ───────────────────────────────
if (-not [System.AppDomain]::CurrentDomain.GetData('SysAdminToolsResolver')) {
    [System.AppDomain]::CurrentDomain.add_AssemblyResolve(
        [System.ResolveEventHandler] {
            param($sender, $resolveArgs)
            try {
                $bin     = [System.AppDomain]::CurrentDomain.GetData('SysAdminToolsBinPath')
                $asmName = $resolveArgs.Name.Split(',')[0].Trim()
                $dllPath = [System.IO.Path]::Combine($bin, "$asmName.dll")
                if ([System.IO.File]::Exists($dllPath)) {
                    return [System.Reflection.Assembly]::LoadFrom($dllPath)
                }
            } catch {}
            return $null
        }
    )
    [System.AppDomain]::CurrentDomain.SetData('SysAdminToolsResolver', $true)
}

# ── Load DLLs in dependency order ─────────────────────────────────────────────
foreach ($dll in @('PacketDotNet.dll', 'SharpPcap.dll', 'BandwidthMonitor.dll', 'ps-tcpdump.dll')) {
    $fullPath = [System.IO.Path]::Combine($BinPath, $dll)
    if (-not [System.IO.File]::Exists($fullPath)) {
        throw "Missing required DLL: $fullPath"
    }
    [System.Reflection.Assembly]::LoadFrom($fullPath) | Out-Null
    Write-Verbose "Loaded: $fullPath"
}

# ── Verify BwMonitor type loaded correctly ─────────────────────────────────────
$script:BwMonitorType = $null
foreach ($asm in [System.AppDomain]::CurrentDomain.GetAssemblies()) {
    if ($asm.GetName().Name -eq 'BandwidthMonitor') {
        $t = $asm.GetType('BandwidthMonitor.BwMonitor')
        if ($t) {
            $script:BwMonitorType = $t
            Write-Verbose "Type resolved: $($t.FullName)"
            break
        }
    }
}

if (-not $script:BwMonitorType) {
    Write-Warning "Could not resolve BandwidthMonitor.BwMonitor. Loaded assemblies:"
    [System.AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { -not $_.IsDynamic } |
        ForEach-Object { Write-Warning "  $($_.GetName().Name)  →  $($_.Location)" }
    throw "Failed to load BandwidthMonitor type."
}

# ── Verify TcpDumpRunner type loaded correctly ─────────────────────────────────
$script:TcpDumpType = $null
foreach ($asm in [System.AppDomain]::CurrentDomain.GetAssemblies()) {
    if ($asm.GetName().Name -eq 'ps-tcpdump') {
        $t = $asm.GetType('PsTcpDump.TcpDumpRunner')
        if ($t) {
            $script:TcpDumpType = $t
            Write-Verbose "Type resolved: $($t.FullName)"
            break
        }
    }
}

if (-not $script:TcpDumpType) {
    Write-Warning "Could not resolve PsTcpDump.TcpDumpRunner. Loaded assemblies:"
    [System.AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { -not $_.IsDynamic } |
        ForEach-Object { Write-Warning "  $($_.GetName().Name)  →  $($_.Location)" }
    throw "Failed to load PsTcpDump.TcpDumpRunner type."
}

# ── Start-BwMon ───────────────────────────────────────────────────────────────
function Start-BwMon {
    <#
    .SYNOPSIS
        Live per-process network bandwidth monitor.

    .DESCRIPTION
        Captures packets on a selected NIC using Npcap and displays
        real-time RX/TX Mbps per process in a live console table.
        Requires Administrator privileges and Npcap 1.00+.

    .EXAMPLE
        Start-BwMon

    .NOTES
        Keys while running:
          B  = sort by Bandwidth (default)
          R  = sort by RX
          T  = sort by TX
          C  = sort by CPU
          M  = sort by Memory
          Type a PID + Enter  = drill into process connections
          ESC                 = back to overview
          Ctrl+C              = quit
    #>
    [CmdletBinding()]
    param()

    if (-not $IsWindows) {
        Write-Error "Start-BwMon requires Windows."
        return
    }

    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Warning "Start-BwMon should be run as Administrator for full packet capture access."
    }

    if (-not $script:BwMonitorType) {
        Write-Error "BandwidthMonitor type not loaded. Try: Import-Module ps-admintools -Force"
        return
    }

    try {
        $script:BwMonitorType.GetMethod('Start').Invoke($null, $null)
    }
    catch {
        $msg = if ($_.Exception.InnerException) {
            $_.Exception.InnerException.Message
        } else {
            $_.Exception.Message
        }
        Write-Error "BwMonitor error: $msg"
    }
}

# ── Start-TcpDump ─────────────────────────────────────────────────────────────
function Start-TcpDump {
    <#
    .SYNOPSIS
        Interactive packet capture tool, like tcpdump for Windows.

    .DESCRIPTION
        Captures live network packets using Npcap with an interactive setup
        wizard for interface selection and optional filters (source IP,
        destination IP, port). Output is color-coded by protocol and can
        optionally be saved to a Wireshark-compatible .pcap file.

    .EXAMPLE
        Start-TcpDump

    .NOTES
        Ctrl+C stops the capture and shows a summary without closing your terminal.
    #>
    [CmdletBinding()]
    param()

    if (-not $IsWindows) {
        Write-Error "Start-TcpDump requires Windows."
        return
    }

    if (-not $script:TcpDumpType) {
        Write-Error "PsTcpDump.TcpDumpRunner type not loaded. Try: Import-Module ps-admintools -Force"
        return
    }

    try {
        $script:TcpDumpType.GetMethod('Start').Invoke($null, $null)
    }
    catch {
        $msg = if ($_.Exception.InnerException) {
            $_.Exception.InnerException.Message
        } else {
            $_.Exception.Message
        }
        Write-Error "TcpDump error: $msg"
    }
}

Export-ModuleMember -Function 'Start-BwMon', 'Start-TcpDump' -Cmdlet 'Test-Time', 'Get-NtpConf', 'Set-NtpConf'