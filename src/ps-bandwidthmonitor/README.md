# SysAdminTools — BwMon

A PowerShell 7.6.2 module for **live per-process network bandwidth monitoring** on Windows.
Built with C# .NET 10 and Npcap for deep packet inspection.

## Features
- Live per-process RX/TX Mbps in a clean console table
- Drill into any process by PID to see all active connections (src/dst IP + port)
- Color-coded traffic levels (green → yellow → red)
- NIC speedometer showing total RX/TX
- Sort by bandwidth, RX, TX, CPU or memory on the fly
- Built-in prerequisite checker
- Ctrl+C stops the monitor without closing your terminal

## Prerequisites
- Windows 10/11 or Windows Server 2019/2022/2025
- PowerShell 7.0+
- [Npcap](https://npcap.com/#download) 1.00+ (install with WinPcap API-compatible mode)
  how install npcap via CLI:
  $npcapUrl = "https://npcap.com/dist/npcap-1.80.exe"
  $installer = "$env:TEMP\npcap-installer.exe"

Invoke-WebRequest -Uri $npcapUrl -OutFile $installer

Start-Process -FilePath $installer -ArgumentList "/S" -Wait -Verb RunAs

- .NET 10 runtime

## Installation

### From PowerShell Gallery
```powershell
Install-Module -Name ps-AdminTools
```


## Usage
```powershell
Start-BwMon
```

### Keys while running
| Key | Action |
|-----|--------|
| `B` | Sort by bandwidth (default) |
| `R` | Sort by RX |
| `T` | Sort by TX |
| `C` | Sort by CPU |
| `M` | Sort by memory |
| Type PID + `Enter` | Drill into process connections |
| `ESC` | Back to overview |
| `Ctrl+C` | Stop monitor |