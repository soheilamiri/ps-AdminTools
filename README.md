# ps-AdminTools

[![PowerShell Gallery](https://img.shields.io/powershellgallery/v/ps-AdminTools.svg)](https://www.powershellgallery.com/packages/ps-AdminTools)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![PowerShell](https://img.shields.io/badge/PowerShell-7.6%2B-blue.svg)](https://github.com/PowerShell/PowerShell)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-0078D6.svg)](#prerequisites)

A PowerShell 7.6 module of native sysadmin and networking tools, built on C# and .NET 10. Most tools use Npcap for deep packet-level visibility on Windows; `Test-Time` is fully cross-platform and also runs on Linux. No extra dependencies beyond what's listed below, no separate installers — just `Import-Module` and go.

## Tools included

| Command | Description | Platform |
|---|---|---|
| [`Start-BwMon`](src/ps-bandwidthmonitor/README.md) | Live per-process network bandwidth monitor with drill-down into connections | Windows only |
| [`Start-TcpDump`](src/ps-tcpdump/README.md) | Interactive, color-coded packet capture — tcpdump-style, with optional `.pcap` export | Windows only |
| [`Test-Time`](src/ps-ntpcheck/README.md) | Compare local or NTP source time against up to 5 remote NTP servers, with configurable offset tolerance and retry | Windows & Linux |

Each tool has its own README linked above with full usage details, options, and examples.
<img width="774" height="315" alt="image" src="https://github.com/user-attachments/assets/97eef8c4-ca0f-47ed-a6ae-da8884b034b6" />
<img width="855" height="781" alt="image" src="https://github.com/user-attachments/assets/7482af61-4539-4e26-9ee5-b58084be3597" />

## Prerequisites

**All tools:**
- PowerShell 7.0+ (module targets 7.6)
- .NET 10 runtime

**`Start-BwMon` / `Start-TcpDump` (Windows only):**
- Windows 10/11 or Windows Server 2019/2022/2025
- [Npcap](https://npcap.com/#download) 1.00+ (install with **WinPcap API-compatible mode** checked)

**`Test-Time` (Windows & Linux):**
- No additional dependencies — queries NTP servers directly over UDP/123, no packet capture required

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
# Monitor bandwidth per process (Windows only)
Start-BwMon

# Capture packets interactively (Windows only)
Start-TcpDump

# Compare local clock against an NTP server (Windows or Linux)
Test-Time -Remote time.windows.com

# Compare two NTP servers against each other, with custom tolerance
Test-Time -Source 192.168.247.10 -Remote time.windows.com -MaxOffset 5

# Check multiple remote servers at once, returning only pass/fail for scripting
Test-Time -Remote 192.168.247.10,192.168.247.11,time.windows.com -Output
```

## Project structure
```
ps-AdminTools/
├── Bin/                      # Compiled DLLs loaded by the module
│   └── en-US/                # Cmdlet help (MAML) for binary modules
├── src/
│   ├── ps-bandwidthmonitor/  # C# source for Start-BwMon
│   ├── ps-tcpdump/           # C# source for Start-TcpDump
│   └── ps-ntpcheck/          # C# source for Test-Time (cross-platform)
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

`Test-Time` builds the same way, and works identically whether you build it on Windows or Linux:
```powershell
cd src\ps-ntpcheck
dotnet build -c Release
# copy bin\Release\netstandard2.0\NtpCheck.dll -> ..\..\Bin\NtpCheck.dll
```

Then reload the module:
```powershell
Import-Module ps-AdminTools -Force
```

## Contributing
Issues and pull requests are welcome. If you run into a bug, please include your PowerShell version (`$PSVersionTable`), OS/build, and — for `Start-BwMon`/`Start-TcpDump` — your Npcap version.

## License
Distributed under the MIT License. See [LICENSE](LICENSE) for details.
