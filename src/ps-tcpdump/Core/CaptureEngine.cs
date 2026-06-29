using SharpPcap;
using PsTcpDump.Core;

namespace PsTcpDump.Core;

public class CaptureEngine
{
    private ICaptureDevice? _device;
    private readonly FilterOptions _filters;
    private volatile bool _running;

    public event Action<ParsedPacket>? PacketCaptured;
    public event Action<string>? ErrorOccurred;

    public long TotalPackets { get; private set; }
    public long TotalBytes { get; private set; }
    public DateTime? StartTime { get; private set; }

    public CaptureEngine(FilterOptions filters)
    {
        _filters = filters;
    }

    public void Start()
    {
        var devices = CaptureDeviceList.Instance;
        _device = devices.FirstOrDefault(d => d.Name == _filters.InterfaceName);

        if (_device == null)
        {
            ErrorOccurred?.Invoke($"Interface not found: {_filters.InterfaceName}");
            return;
        }

        _device.Open(DeviceModes.Promiscuous, 1000);

        var bpf = _filters.BuildBpfFilter();
        if (!string.IsNullOrEmpty(bpf))
            _device.Filter = bpf;

        _device.OnPacketArrival += OnPacketArrival;
        _running = true;
        StartTime = DateTime.Now;
        _device.StartCapture();
    }

    public void Stop()
    {
        _running = false;
        try
        {
            _device?.StopCapture();
            _device?.Close();
        }
        catch { /* ignore on shutdown */ }
    }

    private void OnPacketArrival(object sender, PacketCapture capture)
    {
        if (!_running) return;

        var parsed = PacketParser.Parse(capture);
        if (parsed == null) return;

        TotalPackets++;
        TotalBytes += parsed.Length;

        PacketCaptured?.Invoke(parsed);
    }

    public double GetPacketsPerSecond()
    {
        if (StartTime == null) return 0;
        var elapsed = (DateTime.Now - StartTime.Value).TotalSeconds;
        return elapsed > 0 ? TotalPackets / elapsed : 0;
    }
}