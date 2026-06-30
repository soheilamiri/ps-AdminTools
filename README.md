# ps-AdminTools

[![PowerShell Gallery](https://img.shields.io/powershellgallery/v/ps-AdminTools.svg)](https://www.powershellgallery.com/packages/ps-AdminTools)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![PowerShell](https://img.shields.io/badge/PowerShell-7.6%2B-blue.svg)](https://github.com/PowerShell/PowerShell)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6.svg)](#prerequisites)

A PowerShell 7.6 module of native Windows networking and sysadmin tools, built on C# .NET 10 and Npcap for deep packet-level visibility — no extra dependencies, no separate installers, just `Import-Module` and go.

## Tools included

| Command | Description |
|---|---|
| [`Start-BwMon`](src/ps-bandwidthmonitor/README.md) | Live per-process network bandwidth monitor with drill-down into connections |
| [`Start-TcpDump`](src/ps-tcpdump/README.md) | Interactive, color-coded packet capture — tcpdump-style, with optional `.pcap` export |

Each tool has its own README linked above with full usage details, options, and examples.

## Prerequisites
- Windows 10/11 or Windows Server 2019/2022/2025
- PowerShell 7.0+
- [Npcap](https://npcap.com/#download) 1.00+ (install with **WinPcap API-compatible mode** checked)
- .NET 10 runtime

## Installation

### From PowerShell Gallery
```powershell
Install-Module -Name ps-AdminTools
```

### From source
```powershell
git clone https://github.com/soheilamiri/ps-AdminTools.git
Import-Module .\ps-AdminTools\ps-AdminTools.psd1
```

## Quick start
```powershell
# Monitor bandwidth per process
Start-BwMon

# Capture packets interactively
Start-TcpDump
```

## Project structure
```
ps-AdminTools/
├── Bin/                      # Compiled DLLs loaded by the module
├── src/
│   ├── ps-bandwidthmonitor/  # C# source for Start-BwMon
│   └── ps-tcpdump/           # C# source for Start-TcpDump
├── ps-AdminTools.psd1        # Module manifest
├── PS-AdminTools.psm1        # Module loader / exported functions
├── LICENSE
└── README.md
```

## Building from source

Each tool under `src/` is a standard .NET class library. To rebuild a tool's DLL and drop it into the module's `Bin/` folder:

```powershell
cd src\ps-tcpdump
dotnet build -c Release -o ..\..\Bin\ps-tcpdump
Copy-Item .\..\..\Bin\ps-tcpdump\ps-tcpdump.dll -Destination ..\..\Bin -Force
```

Then reload the module:
```powershell
Import-Module ps-AdminTools -Force
```

## Contributing
Issues and pull requests are welcome. If you run into a bug, please include your PowerShell version (`$PSVersionTable`), Windows build, and Npcap version.

## License
Distributed under the MIT License. See [LICENSE](LICENSE) for details.