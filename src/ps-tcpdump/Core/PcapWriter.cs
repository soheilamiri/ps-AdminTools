namespace PsTcpDump.Core;

public class PcapWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private bool _disposed;

    // .pcap global header constants
    private const uint MagicNumber = 0xa1b2c3d4;
    private const ushort VersionMajor = 2;
    private const ushort VersionMinor = 4;
    private const int ThisZone = 0;
    private const uint SigFigs = 0;
    private const uint SnapLen = 65535;
    private const uint Network = 1; // LINKTYPE_ETHERNET

    public string FilePath { get; }

    public PcapWriter(string filePath)
    {
        FilePath = filePath;
        var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(fs);
        WriteGlobalHeader();
    }

    private void WriteGlobalHeader()
    {
        _writer.Write(MagicNumber);
        _writer.Write(VersionMajor);
        _writer.Write(VersionMinor);
        _writer.Write(ThisZone);
        _writer.Write(SigFigs);
        _writer.Write(SnapLen);
        _writer.Write(Network);
    }

    public void WritePacket(ParsedPacket packet)
    {
        if (_disposed) return;

        var ts = packet.Timestamp;
        var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var totalSeconds = (uint)(ts.ToUniversalTime() - epoch).TotalSeconds;
        var microseconds = (uint)(ts.Millisecond * 1000);
        var length = (uint)packet.RawBytes.Length;

        lock (_writer)
        {
            _writer.Write(totalSeconds);
            _writer.Write(microseconds);
            _writer.Write(length);      // captured length
            _writer.Write(length);      // original length
            _writer.Write(packet.RawBytes);
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Flush();
        _writer.Close();
        _writer.Dispose();
    }
}