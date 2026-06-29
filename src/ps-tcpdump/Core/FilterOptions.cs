namespace PsTcpDump.Core;

public class FilterOptions
{
    public string InterfaceName { get; set; } = string.Empty;
    public string InterfaceDescription { get; set; } = string.Empty;
    public string? SourceIp { get; set; }
    public string? DestinationIp { get; set; }
    public int? LocalPort { get; set; }

    public string BuildBpfFilter()
{
    var parts = new List<string>();

    if (!string.IsNullOrWhiteSpace(SourceIp))
        parts.Add($"src host {SourceIp}");

    if (!string.IsNullOrWhiteSpace(DestinationIp))
        parts.Add($"dst host {DestinationIp}");

    // Match port on EITHER src or dst side
    if (LocalPort.HasValue)
        parts.Add($"(src port {LocalPort.Value} or dst port {LocalPort.Value})");

    return string.Join(" and ", parts);
}

 public string GetFilterSummary()
{
    var parts = new List<string>
    {
        $"IF: {InterfaceDescription}"
    };

    if (!string.IsNullOrWhiteSpace(SourceIp))
        parts.Add($"SRC: {SourceIp}");

    if (!string.IsNullOrWhiteSpace(DestinationIp))
        parts.Add($"DST: {DestinationIp}");

    if (LocalPort.HasValue)
        parts.Add($"PORT: {LocalPort.Value} (src or dst)");

    return string.Join("  |  ", parts);
}
}