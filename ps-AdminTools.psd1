@{
    # Module identity
    ModuleVersion     = '1.7.0.perview'
    GUID              = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author            = 'Soheil Darvishamiri'
    CompanyName       = 'SysAdminTools'
    Description       = 'SysAdminTools toolkit - bandwidth monitor, packet capture, NTP checks, SSL certificate info, live traceroute, and more'
    PowerShellVersion = '7.6'

    # Root module
    RootModule        = 'PS-AdminTools.psm1'

    # Binary modules loaded into this module's session state. PowerShell auto-discovers the
    # [Cmdlet] classes inside each DLL and registers them as native cmdlets.
    NestedModules     = @(
        'Bin\NtpCheck.dll',
        'Bin\SslCheck.dll',
        'Bin\MtrCheck.dll'
    )

    # Controls which properties TestTimeResult shows by default in Format-Table.
    TypesToProcess    = @('Bin\NtpCheck.types.ps1xml')

    # Exported commands. Functions come from the .ps1 files dot-sourced by the .psm1;
    # cmdlets come from the binary modules above.
    FunctionsToExport = @(
        'Start-BwMon',
        'Start-TcpDump',
        'Import-OpenStackRCFile',
        'Start-Watch'
    )
    CmdletsToExport   = @(
        'Test-Time',
        'Get-NtpConf',
        'Set-NtpConf',
        'Get-SslInfo',
        'Start-Mtr'
    )
    VariablesToExport = @()
    AliasesToExport   = @()

    # Module metadata
    PrivateData = @{
        PSData = @{
            Tags       = @('Network', 'Bandwidth', 'Monitor', 'SysAdmin', 'TcpDump', 'PacketCapture', 'NTP', 'Time', 'SSL', 'Traceroute', 'MTR')
            ProjectUri = ''
        }
    }
}
