using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32;
using PacketDotNet;
using SharpPcap;
using SharpPcap.LibPcap;

namespace BandwidthMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcpRowOwnerPid
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId, RemotePort, State, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdpRowOwnerPid
    {
        public uint LocalAddr, LocalPort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MibUdp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId, LocalPort, OwningPid;
    }

    internal class ProcessStats
    {
        public string Name     = "";
        public int    Pid;
        public double RxMbps;
        public double TxMbps;
        public double CpuPercent;
        public long   MemoryMB;
    }

    internal record ConnectionKey(
        string    Proto,
        IPAddress SrcIP,
        ushort    SrcPort,
        IPAddress DstIP,
        ushort    DstPort
    );

    internal class ConnectionStats
    {
        public int       Pid;
        public string    Proto   = "";
        public IPAddress SrcIP   = IPAddress.None;
        public ushort    SrcPort;
        public IPAddress DstIP   = IPAddress.None;
        public ushort    DstPort;
        public double    RxMbps;
        public double    TxMbps;
        public long      RxBytes;
        public long      TxBytes;
    }

    internal enum SortMode { Bandwidth, RX, TX, CPU, Memory }
    internal enum AppMode  { Overview, Detail }

    [SupportedOSPlatform("windows")]
    public static class BwMonitor
    {
        static ConcurrentDictionary<int, long>            rxBytes  = new();
        static ConcurrentDictionary<int, long>            txBytes  = new();
        static ConcurrentDictionary<ConnectionKey,
                                    ConnectionStats>      connMap  = new();
        static HashSet<IPAddress>                         localIPs = new();
        static Dictionary<int,(TimeSpan cpu,DateTime t)> cpuTrack = new();
        static readonly object cpuLock = new();

        static long nicRxBytes = 0;
        static long nicTxBytes = 0;

        static volatile SortMode sortMode    = SortMode.Bandwidth;
        static volatile AppMode  appMode     = AppMode.Overview;
        static volatile int      detailPid   = -1;
        static string            detailName  = "";
        static string            inputBuffer = "";
        static int               tableStartRow = 0;

        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr p, ref int sz, bool ord, int af, int cls, uint res);
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedUdpTable(IntPtr p, ref int sz, bool ord, int af, int cls, uint res);

        const int AF_INET = 2, AF_INET6 = 23;
        const int TCP_TABLE_OWNER_PID_ALL = 5, UDP_TABLE_OWNER_PID = 1;

        const char TL = '┌', TR = '┐', BL = '└', BR = '┘';
        const char HL = '─', VL = '│';

        public static void Start()
        {
            // ── Reset state ──────────────────────────────────────────────────
            rxBytes.Clear(); txBytes.Clear(); connMap.Clear();
            localIPs.Clear(); cpuTrack.Clear();
            nicRxBytes = 0; nicTxBytes = 0;
            sortMode = SortMode.Bandwidth;
            appMode  = AppMode.Overview;
            detailPid = -1; detailName = ""; inputBuffer = "";

            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "SysAdminTools — Bandwidth Monitor";
            Console.Clear();

            // ── Banner ───────────────────────────────────────────────────────
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"  ╔══════════════════════════════════════╗");
            Console.WriteLine(@"  ║     SysAdminTools — BwMonitor        ║");
            Console.WriteLine(@"  ║     Per-Process  •  Live  •  Npcap   ║");
            Console.WriteLine(@"  ╚══════════════════════════════════════╝");
            Console.ResetColor();

            // ── Prerequisites ────────────────────────────────────────────────
            if (!CheckPrerequisites())
            {
                Console.WriteLine(" Press any key to exit...");
                Console.ReadKey(true);
                return;
            }

            // ── Interface list ───────────────────────────────────────────────
            var devices = CaptureDeviceList.Instance;
            if (devices.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" No capture devices found!");
                Console.ResetColor();
                Console.ReadKey(true);
                return;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" Available Interfaces:");
            Console.ResetColor();
            Console.WriteLine($"  {TL}{new string(HL,4)}{"┬"}{new string(HL,48)}{TR}");
            for (int i = 0; i < devices.Count; i++)
            {
                string n = devices[i].Description ?? devices[i].Name;
                if (n.Length > 47) n = n[..47];
                Console.WriteLine($"  {VL}{i,3} {"│"} {n,-47}{VL}");
            }
            Console.WriteLine($"  {BL}{new string(HL,4)}{"┴"}{new string(HL,48)}{BR}");
            Console.WriteLine();
            Console.Write(" Select interface index: ");

            string? input = Console.ReadLine();
            if (!int.TryParse(input, out int idx) || idx < 0 || idx >= devices.Count)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" Invalid selection.");
                Console.ResetColor();
                return;
            }

            if (devices[idx] is not LibPcapLiveDevice device)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" Selected device is not a LibPcap device.");
                Console.ResetColor();
                return;
            }

            string nicName = device.Description ?? device.Name;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($" Using: {nicName}");
            Console.ResetColor();

            CollectLocalIPs(device);
            device.Open(new DeviceConfiguration
            {
                Mode        = DeviceModes.Promiscuous,
                ReadTimeout = 1000
            });
            device.OnPacketArrival += OnPacketArrival;
            device.StartCapture();

            // ── Cancellation token — Ctrl+C sets this, does NOT kill terminal ─
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;   // prevent terminal from closing
                cts.Cancel();      // signal render loop to stop
            };

            Thread.Sleep(300);
            Console.Clear();
            Console.CursorVisible = false;
            tableStartRow = 0;

            for (int r = 0; r < Console.WindowHeight - 1; r++)
                Console.WriteLine(new string(' ', Console.WindowWidth - 1));

            // ── Key listener thread ──────────────────────────────────────────
            new Thread(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    var k = Console.ReadKey(intercept: true);
                    if (appMode == AppMode.Overview)
                    {
                        switch (k.Key)
                        {
                            case ConsoleKey.B: sortMode = SortMode.Bandwidth; break;
                            case ConsoleKey.R: sortMode = SortMode.RX;        break;
                            case ConsoleKey.T: sortMode = SortMode.TX;        break;
                            case ConsoleKey.C: sortMode = SortMode.CPU;       break;
                            case ConsoleKey.M: sortMode = SortMode.Memory;    break;
                            case ConsoleKey.Backspace:
                                if (inputBuffer.Length > 0)
                                    inputBuffer = inputBuffer[..^1];
                                break;
                            case ConsoleKey.Enter:
                                if (int.TryParse(inputBuffer, out int pid) && pid > 0)
                                {
                                    detailPid  = pid;
                                    try   { detailName = Process.GetProcessById(pid).ProcessName; }
                                    catch { detailName = $"PID {pid}"; }
                                    inputBuffer = "";
                                    appMode = AppMode.Detail;
                                }
                                else inputBuffer = "";
                                break;
                            default:
                                if (char.IsDigit(k.KeyChar))
                                    inputBuffer += k.KeyChar;
                                break;
                        }
                    }
                    else
                    {
                        if (k.Key == ConsoleKey.Escape)
                        {
                            detailPid   = -1;
                            inputBuffer = "";
                            appMode     = AppMode.Overview;
                            Console.Clear();
                            for (int r = 0; r < Console.WindowHeight - 1; r++)
                            {
                                Console.SetCursorPosition(0, r);
                                Console.Write(new string(' ', Console.WindowWidth - 1));
                            }
                        }
                    }
                }
            }) { IsBackground = true }.Start();

            // ── Main render loop ─────────────────────────────────────────────
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(1000);

                if (cts.Token.IsCancellationRequested) break;

                var rx = new Dictionary<int, long>();
                var tx = new Dictionary<int, long>();
                foreach (var kv in rxBytes) { rx[kv.Key] = kv.Value; rxBytes[kv.Key] = 0; }
                foreach (var kv in txBytes) { tx[kv.Key] = kv.Value; txBytes[kv.Key] = 0; }

                double nicRxMbps = Interlocked.Exchange(ref nicRxBytes, 0) * 8.0 / 1_000_000.0;
                double nicTxMbps = Interlocked.Exchange(ref nicTxBytes, 0) * 8.0 / 1_000_000.0;

                var connSnapshot = new List<ConnectionStats>();
                foreach (var kv in connMap)
                {
                    var cs = kv.Value;
                    long r = Interlocked.Exchange(ref cs.RxBytes, 0);
                    long t = Interlocked.Exchange(ref cs.TxBytes, 0);
                    connSnapshot.Add(new ConnectionStats
                    {
                        Pid=cs.Pid, Proto=cs.Proto,
                        SrcIP=cs.SrcIP, SrcPort=cs.SrcPort,
                        DstIP=cs.DstIP, DstPort=cs.DstPort,
                        RxMbps=r*8.0/1_000_000.0,
                        TxMbps=t*8.0/1_000_000.0
                    });
                }

                var allPids = new HashSet<int>(rx.Keys);
                allPids.UnionWith(tx.Keys);

                var stats = new List<ProcessStats>();
                foreach (int pid in allPids)
                {
                    rx.TryGetValue(pid, out long rb);
                    tx.TryGetValue(pid, out long tb);
                    stats.Add(new ProcessStats
                    {
                        Name       = GetProcessName(pid),
                        Pid        = pid,
                        RxMbps     = rb * 8.0 / 1_000_000.0,
                        TxMbps     = tb * 8.0 / 1_000_000.0,
                        CpuPercent = GetCpuPercent(pid),
                        MemoryMB   = GetMemoryMB(pid)
                    });
                }

                stats.Sort(sortMode switch
                {
                    SortMode.RX     => (Comparison<ProcessStats>)((a,b) => b.RxMbps.CompareTo(a.RxMbps)),
                    SortMode.TX     => (a,b) => b.TxMbps.CompareTo(a.TxMbps),
                    SortMode.CPU    => (a,b) => b.CpuPercent.CompareTo(a.CpuPercent),
                    SortMode.Memory => (a,b) => b.MemoryMB.CompareTo(a.MemoryMB),
                    _               => (a,b) => (b.RxMbps+b.TxMbps).CompareTo(a.RxMbps+a.TxMbps)
                });

                if (appMode == AppMode.Overview)
                    RenderOverview(stats, nicName, nicRxMbps, nicTxMbps);
                else
                    RenderDetail(connSnapshot, nicName, nicRxMbps, nicTxMbps);
            }

            // ── Graceful shutdown ────────────────────────────────────────────
            device.StopCapture();
            device.Close();
            Console.CursorVisible = true;
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  BwMonitor stopped. Type Start-BwMon to restart.");
            Console.ResetColor();
        }

        // ── Prerequisite checker ─────────────────────────────────────────────
        static bool CheckPrerequisites()
        {
            bool allOk = true;
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" Checking prerequisites...");
            Console.ResetColor();
            Console.WriteLine();

            // Admin — warning only, not a hard block
            bool isAdmin = false;
            try
            {
                var id  = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pri = new System.Security.Principal.WindowsPrincipal(id);
                isAdmin = pri.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { }
            PrintCheck("Running as Administrator", true,
                isAdmin ? "OK" : "WARNING: Not Admin — some processes may not be visible");

            // Npcap
            string? npcapVer = null;
            foreach (var kp in new[]{ @"SOFTWARE\Npcap", @"SOFTWARE\WOW6432Node\Npcap" })
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(kp);
                    if (key != null)
                    {
                        npcapVer = key.GetValue("Version") as string
                                ?? key.GetValue("")         as string
                                ?? "(version unknown)";
                        break;
                    }
                }
                catch { }
            }
            if (npcapVer == null)
            {
                try
                {
                    using var svc = Registry.LocalMachine
                        .OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap");
                    if (svc != null) npcapVer = "(service found)";
                }
                catch { }
            }
            bool npcapOk = npcapVer != null;
            PrintCheck("Npcap driver  (minimum: 1.00)", npcapOk,
                npcapOk ? $"Found v{npcapVer}"
                        : "Not found — https://npcap.com/#download");
            if (!npcapOk) allOk = false;

            // Windows build
            var osVer  = Environment.OSVersion.Version;
            bool winOk = osVer.Major > 10 || (osVer.Major == 10 && osVer.Build >= 17763);
            PrintCheck("Windows  (minimum: Server 2019 / build 17763)", winOk,
                winOk ? $"OK  (build {osVer.Build})" : $"Unsupported build {osVer.Build}");
            if (!winOk) allOk = false;

            // .NET runtime
            var rt        = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
            bool dotnetOk = rt.Contains("10.") || rt.Contains("9.") || rt.Contains("8.");
            PrintCheck(".NET runtime  (minimum: .NET 8)", dotnetOk,
                dotnetOk ? $"OK  ({rt})" : $"Found: {rt}");
            if (!dotnetOk) allOk = false;

            Console.WriteLine();
            if (!allOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" ✗  Prerequisites failed. Fix issues above and retry.");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" ✓  All prerequisites satisfied.");
            }
            Console.ResetColor();
            Console.WriteLine();
            return allOk;
        }

        static void PrintCheck(string label, bool ok, string detail)
        {
            Console.Write("  ");
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? " ✓ " : " ✗ ");
            Console.ResetColor();
            Console.Write($"{label,-52}");
            Console.ForegroundColor = ok ? ConsoleColor.DarkGray : ConsoleColor.Yellow;
            Console.WriteLine($"  {detail}");
            Console.ResetColor();
        }

        // ── Packet handler ───────────────────────────────────────────────────
        static void OnPacketArrival(object sender, PacketCapture e)
        {
            try
            {
                var raw    = e.GetPacket();
                var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
                var ip     = packet.Extract<IPPacket>();
                if (ip == null) return;

                IPAddress src = ip.SourceAddress;
                IPAddress dst = ip.DestinationAddress;
                int       len = ip.TotalLength;

                ushort srcPort=0, dstPort=0;
                string proto="OTH";
                var tcp = ip.Extract<TcpPacket>();
                var udp = ip.Extract<UdpPacket>();
                if      (tcp!=null){proto="TCP";srcPort=tcp.SourcePort;dstPort=tcp.DestinationPort;}
                else if (udp!=null){proto="UDP";srcPort=udp.SourcePort;dstPort=udp.DestinationPort;}

                if (localIPs.Contains(src))
                {
                    Interlocked.Add(ref nicTxBytes, len);
                    int pid = FindPid(src, srcPort, dst, dstPort, tcp!=null);
                    if (pid>0)
                    {
                        txBytes.AddOrUpdate(pid, len, (k,v)=>v+len);
                        TrackConnection(pid, proto, src, srcPort, dst, dstPort, tx:len);
                    }
                }
                else if (localIPs.Contains(dst))
                {
                    Interlocked.Add(ref nicRxBytes, len);
                    int pid = FindPid(dst, dstPort, src, srcPort, tcp!=null);
                    if (pid>0)
                    {
                        rxBytes.AddOrUpdate(pid, len, (k,v)=>v+len);
                        TrackConnection(pid, proto, dst, dstPort, src, srcPort, rx:len);
                    }
                }
            }
            catch { }
        }

        static void TrackConnection(int pid, string proto,
                                    IPAddress localIP,  ushort localPort,
                                    IPAddress remoteIP, ushort remotePort,
                                    long rx=0, long tx=0)
        {
            var key = new ConnectionKey(proto, localIP, localPort, remoteIP, remotePort);
            var cs  = connMap.GetOrAdd(key, _ => new ConnectionStats
            {
                Pid=pid, Proto=proto,
                SrcIP=localIP,  SrcPort=localPort,
                DstIP=remoteIP, DstPort=remotePort
            });
            if (rx>0) Interlocked.Add(ref cs.RxBytes, rx);
            if (tx>0) Interlocked.Add(ref cs.TxBytes, tx);
            if (connMap.Count>500)
                foreach (var kv in connMap)
                    if (kv.Value.RxBytes==0 && kv.Value.TxBytes==0)
                        connMap.TryRemove(kv.Key, out _);
        }

        static void CollectLocalIPs(LibPcapLiveDevice device)
        {
            foreach (var addr in device.Addresses)
                if (addr.Addr?.ipAddress != null)
                    localIPs.Add(addr.Addr.ipAddress);
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                localIPs.Add(ip);
        }

        static int FindPid(IPAddress local, ushort localPort,
                           IPAddress remote, ushort remotePort, bool isTcp)
        {
            if (isTcp)
            {
                foreach (var row in GetTcp4Table())
                    if (NetworkToHostPort(row.LocalPort)==localPort) return (int)row.OwningPid;
                foreach (var row in GetTcp6Table())
                    if (NetworkToHostPort(row.LocalPort)==localPort) return (int)row.OwningPid;
            }
            else
            {
                foreach (var row in GetUdp4Table())
                    if (NetworkToHostPort(row.LocalPort)==localPort) return (int)row.OwningPid;
                foreach (var row in GetUdp6Table())
                    if (NetworkToHostPort(row.LocalPort)==localPort) return (int)row.OwningPid;
            }
            return 0;
        }

        static uint NetworkToHostPort(uint p) =>
            ((p & 0xFF) << 8) | ((p >> 8) & 0xFF);

        static List<MibTcpRowOwnerPid> GetTcp4Table()
        {
            int sz=0; GetExtendedTcpTable(IntPtr.Zero,ref sz,true,AF_INET,TCP_TABLE_OWNER_PID_ALL,0);
            IntPtr buf=Marshal.AllocHGlobal(sz);
            try
            {
                if (GetExtendedTcpTable(buf,ref sz,true,AF_INET,TCP_TABLE_OWNER_PID_ALL,0)!=0) return new();
                int n=Marshal.ReadInt32(buf); IntPtr ptr=buf+4; int rs=Marshal.SizeOf<MibTcpRowOwnerPid>();
                var list=new List<MibTcpRowOwnerPid>(n);
                for(int i=0;i<n;i++,ptr+=rs) list.Add(Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr));
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static List<MibTcp6RowOwnerPid> GetTcp6Table()
        {
            int sz=0; GetExtendedTcpTable(IntPtr.Zero,ref sz,true,AF_INET6,TCP_TABLE_OWNER_PID_ALL,0);
            IntPtr buf=Marshal.AllocHGlobal(sz);
            try
            {
                if (GetExtendedTcpTable(buf,ref sz,true,AF_INET6,TCP_TABLE_OWNER_PID_ALL,0)!=0) return new();
                int n=Marshal.ReadInt32(buf); IntPtr ptr=buf+4; int rs=Marshal.SizeOf<MibTcp6RowOwnerPid>();
                var list=new List<MibTcp6RowOwnerPid>(n);
                for(int i=0;i<n;i++,ptr+=rs) list.Add(Marshal.PtrToStructure<MibTcp6RowOwnerPid>(ptr));
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static List<MibUdpRowOwnerPid> GetUdp4Table()
        {
            int sz=0; GetExtendedUdpTable(IntPtr.Zero,ref sz,true,AF_INET,UDP_TABLE_OWNER_PID,0);
            IntPtr buf=Marshal.AllocHGlobal(sz);
            try
            {
                if (GetExtendedUdpTable(buf,ref sz,true,AF_INET,UDP_TABLE_OWNER_PID,0)!=0) return new();
                int n=Marshal.ReadInt32(buf); IntPtr ptr=buf+4; int rs=Marshal.SizeOf<MibUdpRowOwnerPid>();
                var list=new List<MibUdpRowOwnerPid>(n);
                for(int i=0;i<n;i++,ptr+=rs) list.Add(Marshal.PtrToStructure<MibUdpRowOwnerPid>(ptr));
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static List<MibUdp6RowOwnerPid> GetUdp6Table()
        {
            int sz=0; GetExtendedUdpTable(IntPtr.Zero,ref sz,true,AF_INET6,UDP_TABLE_OWNER_PID,0);
            IntPtr buf=Marshal.AllocHGlobal(sz);
            try
            {
                if (GetExtendedUdpTable(buf,ref sz,true,AF_INET6,UDP_TABLE_OWNER_PID,0)!=0) return new();
                int n=Marshal.ReadInt32(buf); IntPtr ptr=buf+4; int rs=Marshal.SizeOf<MibUdp6RowOwnerPid>();
                var list=new List<MibUdp6RowOwnerPid>(n);
                for(int i=0;i<n;i++,ptr+=rs) list.Add(Marshal.PtrToStructure<MibUdp6RowOwnerPid>(ptr));
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static string GetProcessName(int pid)
        {
            try   { return Process.GetProcessById(pid).ProcessName; }
            catch { return $"[{pid}]"; }
        }

        static double GetCpuPercent(int pid)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                var now  = proc.TotalProcessorTime;
                var time = DateTime.UtcNow;
                lock (cpuLock)
                {
                    if (cpuTrack.TryGetValue(pid, out var prev))
                    {
                        double el=(time-prev.t).TotalSeconds;
                        double us=(now-prev.cpu).TotalSeconds;
                        cpuTrack[pid]=(now,time);
                        if (el>0) return Math.Round(us/el/Environment.ProcessorCount*100,1);
                    }
                    else cpuTrack[pid]=(now,time);
                }
            }
            catch { }
            return 0.0;
        }

        static long GetMemoryMB(int pid)
        {
            try   { return Process.GetProcessById(pid).WorkingSet64/1024/1024; }
            catch { return 0; }
        }

        static ConsoleColor TrafficColor(double mbps) =>
            mbps >= 10.0 ? ConsoleColor.Red    :
            mbps >= 2.0  ? ConsoleColor.Yellow :
            mbps >= 0.1  ? ConsoleColor.Green  :
                           ConsoleColor.DarkGray;

        static string SpeedBar(double mbps, double max, int w=20)
        {
            int f=(int)Math.Clamp(mbps/max*w,0,w);
            return new string('█',f)+new string('░',w-f);
        }

        static string SepRow(int iw, char l='├', char r='┤') =>
            $"{l}{new string(HL,iw)}{r}";

        static string ContentRow(string text, int iw)
        {
            if (text.Length>iw) text=text[..iw];
            else                text=text.PadRight(iw);
            return $"{VL}{text}{VL}";
        }

        static void WriteAt(int row, string text, ConsoleColor fg=ConsoleColor.White)
        {
            if (row>=Console.WindowHeight-1) return;
            Console.SetCursorPosition(0,row);
            Console.ForegroundColor=fg;
            int w=Console.WindowWidth-1;
            if (text.Length>w) text=text[..w];
            else               text=text.PadRight(w);
            Console.Write(text);
            Console.ResetColor();
        }

        static void RenderOverview(List<ProcessStats> stats, string nicName,
                                   double nicRxMbps, double nicTxMbps)
        {
            int totalW=Math.Min(Console.WindowWidth-1,100);
            int iw=totalW-2;
            int row=tableStartRow;
            double nicMax=Math.Max(1.0,Math.Max(nicRxMbps,nicTxMbps));

            WriteAt(row++,$"{TL}{new string(HL,iw)}{TR}",ConsoleColor.DarkCyan);
            WriteAt(row++,ContentRow($" NIC : {nicName}",iw),ConsoleColor.White);
            WriteAt(row++,ContentRow($" RX  : {SpeedBar(nicRxMbps,nicMax,20)}  {nicRxMbps,7:F2} Mbps  ▼",iw),ConsoleColor.Cyan);
            WriteAt(row++,ContentRow($" TX  : {SpeedBar(nicTxMbps,nicMax,20)}  {nicTxMbps,7:F2} Mbps  ▲",iw),ConsoleColor.Green);
            WriteAt(row++,SepRow(iw),ConsoleColor.DarkCyan);

            string sl=sortMode switch
            {
                SortMode.RX     =>"[R]RX",
                SortMode.TX     =>"[T]TX",
                SortMode.CPU    =>"[C]CPU",
                SortMode.Memory =>"[M]Mem",
                _               =>"[B]BW"
            };
            string pp=inputBuffer.Length>0?$"PID> {inputBuffer}_":"PID> type & Enter";
            WriteAt(row++,ContentRow($" Sort:{sl}  B R T C M  |  {pp}  |  Ctrl+C to stop",iw),ConsoleColor.DarkYellow);
            WriteAt(row++,SepRow(iw),ConsoleColor.DarkCyan);
            WriteAt(row++,ContentRow($" {"Process",-21} {"PID",6} {"CPU%",5} {"MemMB",7} {"RX Mbps",8} {"TX Mbps",8}  {"Bandwidth",-16}",iw),ConsoleColor.Cyan);
            WriteAt(row++,SepRow(iw),ConsoleColor.DarkCyan);

            int dataRows=Math.Max(1,Console.WindowHeight-row-2);
            var display=stats.Take(dataRows).ToList();
            double maxTot=display.Count>0?Math.Max(0.01,display.Max(s=>s.RxMbps+s.TxMbps)):0.01;

            for(int i=0;i<dataRows;i++)
            {
                if(i<display.Count)
                {
                    var s=display[i];
                    double tot=s.RxMbps+s.TxMbps;
                    int filled=(int)Math.Clamp(tot/maxTot*10,0,10);
                    string bar=new string('█',filled)+new string('░',10-filled);
                    string name=s.Name.Length>21?s.Name[..21]:s.Name.PadRight(21);
                    WriteAt(row++,ContentRow($" {name} {s.Pid,6} {s.CpuPercent,5:F1} {s.MemoryMB,7} {s.RxMbps,8:F2} {s.TxMbps,8:F2}  {bar} {tot:F2}",iw),TrafficColor(tot));
                }
                else WriteAt(row++,ContentRow("",iw));
            }

            WriteAt(row++,$"{BL}{new string(HL,iw)}{BR}",ConsoleColor.DarkCyan);
            WriteAt(row,ContentRow($" {DateTime.Now:HH:mm:ss}  Procs:{display.Count}  RX:{nicRxMbps:F2}  TX:{nicTxMbps:F2} Mbps",iw),ConsoleColor.DarkGray);
            Console.CursorVisible=false;
        }

        static void RenderDetail(List<ConnectionStats> allConns, string nicName,
                                 double nicRxMbps, double nicTxMbps)
        {
            int totalW=Math.Min(Console.WindowWidth-1,100);
            int iw=totalW-2;
            int row=tableStartRow;

            var conns=allConns
                .Where(c=>c.Pid==detailPid)
                .OrderByDescending(c=>c.RxMbps+c.TxMbps)
                .ToList();

            double nicMax=Math.Max(1.0,Math.Max(nicRxMbps,nicTxMbps));
            double procRx=conns.Sum(c=>c.RxMbps);
            double procTx=conns.Sum(c=>c.TxMbps);

            WriteAt(row++,$"{TL}{new string(HL,iw)}{TR}",ConsoleColor.DarkCyan);
            WriteAt(row++,ContentRow($" NIC : {nicName}",iw),ConsoleColor.White);
            WriteAt(row++,ContentRow($" RX  : {SpeedBar(nicRxMbps,nicMax,20)}  {nicRxMbps,7:F2} Mbps  ▼",iw),ConsoleColor.Cyan);
            WriteAt(row++,ContentRow($" TX  : {SpeedBar(nicTxMbps,nicMax,20)}  {nicTxMbps,7:F2} Mbps  ▲",iw),ConsoleColor.Green);
            WriteAt(row++,SepRow(iw),ConsoleColor.DarkCyan);
            WriteAt(row++,ContentRow($" Process: {detailName}   PID: {detailPid}   RX: {procRx:F2} Mbps   TX: {procTx:F2} Mbps",iw),ConsoleColor.Yellow);
            WriteAt(row++,ContentRow($" ESC = back to overview  |  Ctrl+C to stop",iw),ConsoleColor.DarkGray);
            WriteAt(row++,SepRow(iw),ConsoleColor.DarkCyan);
            WriteAt(row++,ContentRow($" {"Proto",-5} {"Local IP",-18} {"LPort",6}  {"Remote IP",-18} {"RPort",6}  {"RX Mbps",8} {"TX Mbps",8}",iw),ConsoleColor.Cyan);
            WriteAt(row++,SepRow(iw),ConsoleColor.DarkCyan);

            int dataRows=Math.Max(1,Console.WindowHeight-row-2);
            if(conns.Count==0)
            {
                WriteAt(row++,ContentRow("  No connections yet — waiting for traffic...",iw),ConsoleColor.DarkGray);
                for(int i=1;i<dataRows;i++) WriteAt(row++,ContentRow("",iw));
            }
            else
            {
                for(int i=0;i<dataRows;i++)
                {
                    if(i<conns.Count)
                    {
                        var c=conns[i];
                        double tot=c.RxMbps+c.TxMbps;
                        WriteAt(row++,ContentRow($" {c.Proto,-5} {c.SrcIP,-18} {c.SrcPort,6}  {c.DstIP,-18} {c.DstPort,6}  {c.RxMbps,8:F2} {c.TxMbps,8:F2}",iw),TrafficColor(tot));
                    }
                    else WriteAt(row++,ContentRow("",iw));
                }
            }

            WriteAt(row++,$"{BL}{new string(HL,iw)}{BR}",ConsoleColor.DarkCyan);
            WriteAt(row,ContentRow($" {DateTime.Now:HH:mm:ss}  Conns:{conns.Count}  RX:{nicRxMbps:F2}  TX:{nicTxMbps:F2} Mbps  |  Ctrl+C to stop",iw),ConsoleColor.DarkGray);
            Console.CursorVisible=false;
        }
    }
}