@{
    # Module identity
    ModuleVersion     = '1.4.0'
    GUID              = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author            = 'Soheil Darvishamiri'
    CompanyName       = 'SysAdminTools'
    Description       = 'SysAdminTools toolkit — network bandwidth monitor, packet capture (tcpdump-style), NTP time check, and more'
    PowerShellVersion = '7.6'

    # Root module
    RootModule        = 'PS-AdminTools.psm1'

    # Binary modules loaded into this module's session state.
    # NtpCheck.dll contains the Test-Time [Cmdlet] class - listing it here
    # makes PowerShell auto-discover and register it as a native cmdlet.
    NestedModules     = @('Bin\NtpCheck.dll')

    # Exported commands
    FunctionsToExport = @('Start-BwMon', 'Start-TcpDump', 'Import-OpenStackRCFile')
    CmdletsToExport = @('Test-Time', 'Get-NtpConf', 'Set-NtpConf')
    VariablesToExport = @()
    AliasesToExport   = @()

    # Module metadata
    PrivateData = @{
        PSData = @{
            Tags       = @('Network', 'Bandwidth', 'Monitor', 'SysAdmin', 'TcpDump', 'PacketCapture', 'NTP', 'Time')
            ProjectUri = ''
        }
    }
}