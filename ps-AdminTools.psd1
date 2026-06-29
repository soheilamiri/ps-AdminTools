@{
    # Module identity
    ModuleVersion     = '1.0.0'
    GUID              = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
    Author            = 'Soheil Darvishamiri'
    CompanyName       = 'SysAdminTools'
    Description       = 'CloudAdmin toolkit — network bandwidth monitor and more'
    PowerShellVersion = '7.6'

    # Root module
    RootModule        = 'PS-AdminTools.psm1'

    # Exported commands
    FunctionsToExport = @('Start-BwMon')
    CmdletsToExport   = @()
    VariablesToExport = @()
    AliasesToExport   = @()

    # Module metadata
    PrivateData = @{
        PSData = @{
            Tags       = @('Network', 'Bandwidth', 'Monitor', 'SysAdmin')
            ProjectUri = ''
        }
    }
}