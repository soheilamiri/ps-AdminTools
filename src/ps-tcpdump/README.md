# SysAdminTools — TcpDump

A PowerShell 7.6.2 module for **interactive packet capture** on Windows, modeled after the classic Linux `tcpdump`.
Built with C# .NET 10 and Npcap for deep packet inspection.

## Features
- Interactive setup wizard for network interface selection
- Optional filters: source IP, destination IP, and port (matches src or dst)
- Live, color-coded packet feed printed directly to your terminal (TCP, UDP, ICMP, ARP)
- Native terminal scrolling — review captured packets just by scrolling your console, no custom UI to fight with
- Optional save to a Wireshark-compatible `.pcap` file, with custom folder and file name
- Runs without requiring Administrator privileges
- Ctrl+C stops the capture and prints a summary without closing your terminal

## Prerequisites
- Windows 10/11 or Windows Server 2019/2022/2025
- PowerShell 7.0+
- [Npcap](https://npcap.com/#download) 1.00+ (install with WinPcap API-compatible mode)
- .NET 10 runtime

## Installation

### From PowerShell Gallery
```powershell
Install-Module -Name ps-AdminTools
```

## Usage
```powershell
Start-TcpDump
```

You'll be prompted to:
1. Select a network interface from the detected list
2. Optionally enter a source IP address to filter on
3. Optionally enter a destination IP address to filter on
4. Optionally enter a port to filter on (matches either source or destination)
5. Choose whether to save the capture to a `.pcap` file, and if so, where

### Example session
```
Save capture to .pcap file? [y/N]: y
Save folder (Enter for default):
File name   (Enter for default): my-capture

  Interface : Realtek PCIe GbE Family Controller
  Filter    : dst host 8.8.8.8
  Saving to : C:\Users\you\Documents\ps-tcpdump\my-capture.pcap

  Time            Proto  Src IP          SrcPort  Dst IP          DstPort  Info
  ────            ─────  ──────          ───────  ──────          ───────  ────
  [Capturing... press Ctrl+C to stop]

  10:55:12.443   TCP    192.168.1.5     :51234   8.8.8.8         :443     [SYN] Seq=0 Len=0
  10:55:12.445   TCP    8.8.8.8         :443     192.168.1.5     :51234   [SYN,ACK] Seq=0 Len=0
```

### Stopping a capture
Press `Ctrl+C` at any time. The capture stops, a summary is printed, and if you opted to save, the `.pcap` file is finalized — all without closing your terminal, so you can scroll back through everything that was captured.

```
  ──────────────────────────────────────────────────────
  Captured : 1,741 packets (1,031,505 bytes)
  Rate     : 53.8 packets/sec
  Saved to : C:\Users\you\Documents\ps-tcpdump\my-capture.pcap
```