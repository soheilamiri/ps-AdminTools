using System;
using System.Collections.Generic;
using System.Management.Automation.Host;
using System.Text;

namespace PSAdminTools.Mtr
{
    /// <summary>
    /// Everything shown on the header line: what is being traced, and which local address and
    /// interface the probes leave from.
    /// </summary>
    internal sealed class TraceContext
    {
        public System.Net.IPAddress TargetAddress { get; set; } = System.Net.IPAddress.None;
        public System.Net.IPAddress? SourceAddress { get; set; }
        public string? InterfaceName { get; set; }
        public int? InterfaceIndex { get; set; }

        /// <summary>True when -Interface was supplied, so probes are explicitly source-bound.</summary>
        public bool ExplicitlyBound { get; set; }

        /// <summary>Set when tracing with TTL-limited TCP connects instead of ICMP echo.</summary>
        public int? TcpPort { get; set; }

        /// <summary>TCP mode only: "open" or "closed (RST)" once the destination answers.</summary>
        public string? TcpStatus { get; set; }
    }

    /// <summary>
    /// Draws the live mtr-style table, redrawing in place each cycle.
    ///
    /// Cursor movement uses ANSI escape sequences rather than PSHostRawUserInterface. An earlier
    /// version set RawUI.CursorPosition directly; when that throws - which it does on more hosts
    /// than expected - every frame fell through to sequential printing and the table appended
    /// itself down the screen instead of refreshing. ANSI works anywhere the colour codes already
    /// work, and is immune to the buffer scrolling out from under a saved origin.
    ///
    /// Each line ends with ESC[K (erase to end of line) so a shorter line never leaves fragments
    /// of the previous frame behind, which also removes any need to know the buffer width.
    /// </summary>
    internal sealed class MtrRenderer
    {
        private const string AnsiGreen = "\u001b[32m";
        private const string AnsiOrange = "\u001b[38;5;208m";
        private const string AnsiRed = "\u001b[31m";
        private const string AnsiDim = "\u001b[90m";
        private const string AnsiReset = "\u001b[0m";
        private const string AnsiEraseLine = "\u001b[K";

        private const int HostColumnWidth = 34;

        private readonly PSHostUserInterface _ui;
        /// <summary>
        /// Terminal rows occupied by the previous frame - not logical lines. A line long enough
        /// to wrap consumes more than one row, and moving the cursor up by a line count instead
        /// left the frame drifting down the screen each cycle.
        /// </summary>
        private int _lastRowCount;
        private bool _firstFrame = true;

        public MtrRenderer(PSHostUserInterface ui)
        {
            _ui = ui;
        }

        public void Render(
            TraceContext context,
            int cycle,
            IReadOnlyList<HopStats> hops,
            bool noDns,
            string? warning)
        {
            // Plain and coloured versions are tracked in parallel: the coloured text is what gets
            // written, but only the plain text has a meaningful length, and length is what decides
            // how many terminal rows a line actually occupies once it wraps.
            var plain = new List<string>();
            var coloured = new List<string>();

            // Line 1: what is being tested and where the probes are leaving from.
            string interfaceText;
            if (context.InterfaceName == null)
            {
                interfaceText = "unknown";
            }
            else if (context.InterfaceIndex.HasValue)
            {
                interfaceText = $"{context.InterfaceName} (index {context.InterfaceIndex.Value})";
            }
            else
            {
                interfaceText = context.InterfaceName;
            }

            string boundNote = context.ExplicitlyBound ? " [bound]" : string.Empty;
            string header =
                $"Target {context.TargetAddress}" +
                $"   Source {context.SourceAddress?.ToString() ?? "unknown"}" +
                $"   Interface {interfaceText}{boundNote}";
            plain.Add(header);
            coloured.Add(header);

            // Line 2: cycle counter and the stop hint.
            string modeText = context.TcpPort.HasValue
                ? $"   TCP :{context.TcpPort.Value}{(context.TcpStatus != null ? $" {context.TcpStatus}" : string.Empty)}"
                : string.Empty;
            string cycleLine = $"Start-Mtr   Cycle {cycle}{modeText}   Ctrl+C to stop";
            plain.Add(cycleLine);
            coloured.Add(AnsiDim + cycleLine + AnsiReset);

            string columns =
                "Host".PadRight(HostColumnWidth + 4) +
                "Loss%".PadLeft(6) +
                "Snt".PadLeft(6) +
                "Last".PadLeft(7) +
                "Avg".PadLeft(7) +
                "Best".PadLeft(7) +
                "Wrst".PadLeft(7) +
                "StDev".PadLeft(7);
            plain.Add(columns);
            coloured.Add(columns);

            foreach (HopStats hop in hops)
            {
                BuildRow(hop, noDns, out string rowPlain, out string rowColoured);
                plain.Add(rowPlain);
                coloured.Add(rowColoured);
            }

            if (!string.IsNullOrEmpty(warning))
            {
                plain.Add(string.Empty);
                coloured.Add(string.Empty);

                // Wrap explicitly. A line longer than the terminal gets width-wrapped by the
                // console into two physical rows, but the redraw moves the cursor up by the
                // number of LOGICAL lines - so each cycle the frame drifted down a row and left
                // the previous header stranded on screen. Wrapping here keeps the two in step.
                foreach (string chunk in WrapText(warning!, GetWidth() - 1))
                {
                    plain.Add(chunk);
                    coloured.Add(AnsiRed + chunk + AnsiReset);
                }
            }

            Write(plain, coloured);
        }

        /// <summary>Word-wraps plain text to the given width, breaking long words if necessary.</summary>
        private static IEnumerable<string> WrapText(string text, int width)
        {
            if (width < 10) { width = 10; }

            var current = new StringBuilder();

            foreach (string word in text.Split(' '))
            {
                string piece = word;

                // A single word longer than the line must still be broken somewhere.
                while (piece.Length > width)
                {
                    if (current.Length > 0)
                    {
                        yield return current.ToString();
                        current.Clear();
                    }
                    yield return piece.Substring(0, width);
                    piece = piece.Substring(width);
                }

                if (current.Length == 0)
                {
                    current.Append(piece);
                }
                else if (current.Length + 1 + piece.Length <= width)
                {
                    current.Append(' ').Append(piece);
                }
                else
                {
                    yield return current.ToString();
                    current.Clear();
                    current.Append(piece);
                }
            }

            if (current.Length > 0)
            {
                yield return current.ToString();
            }
        }

        private static void BuildRow(HopStats hop, bool noDns, out string plain, out string coloured)
        {
            string label;
            if (hop.IsUnknown)
            {
                // A hop that never replied normally has no identity at all. Hop 1 is the
                // exception: its address comes from the routing table, so it can still be named.
                label = string.IsNullOrEmpty(hop.FallbackAddress)
                    ? "???"
                    : $"{hop.FallbackAddress} (gateway)";
            }
            else if (!noDns && !string.IsNullOrEmpty(hop.HostName))
            {
                label = hop.HostName!;
            }
            else
            {
                label = hop.Address ?? "???";
            }

            string index = (hop.Ttl.ToString() + ".").PadLeft(3) + " ";
            string hostCell = Fit(label, HostColumnWidth);

            double loss = hop.LossPercent;
            string lossCell = (loss.ToString("0.0") + "%").PadLeft(6);
            string sentCell = hop.Sent.ToString().PadLeft(6);

            string lastCell = Metric(hop.IsUnknown ? (double?)null : hop.Last);
            string avgCell = Metric(hop.IsUnknown ? (double?)null : hop.Average);
            string bestCell = Metric(hop.IsUnknown ? (double?)null : hop.BestForDisplay);
            string worstCell = Metric(hop.IsUnknown ? (double?)null : hop.Worst);
            string stDevCell = Metric(hop.IsUnknown ? (double?)null : hop.StandardDeviation);

            string lossColour = loss <= 0d ? AnsiGreen : (loss >= 100d ? AnsiRed : AnsiOrange);
            string colouredHost = hop.IsUnknown ? AnsiDim + hostCell + AnsiReset : hostCell;

            plain = index + hostCell + lossCell + sentCell + lastCell + avgCell + bestCell + worstCell + stDevCell;

            coloured = index + colouredHost + lossColour + lossCell + AnsiReset +
                       sentCell + lastCell + avgCell + bestCell + worstCell + stDevCell;
        }

        private static string Metric(double? value)
        {
            return (value.HasValue ? value.Value.ToString("0.0") : "-").PadLeft(7);
        }

        private static string Fit(string text, int width)
        {
            if (text.Length > width)
            {
                return text.Substring(0, width - 1) + " ";
            }
            return text.PadRight(width);
        }

        /// <summary>
        /// Best-effort terminal width. Only used to work out how many rows a line occupies once
        /// wrapped, so a wrong answer degrades the redraw rather than breaking anything.
        /// </summary>
        private int GetWidth()
        {
            try
            {
                int width = _ui.RawUI.BufferSize.Width;
                if (width > 0)
                {
                    return width;
                }
            }
            catch (Exception)
            {
                // Host exposes no usable RawUI - fall through to the default.
            }

            return 120;
        }

        private static int RowsOccupied(string plain, int width)
        {
            if (width <= 0 || plain.Length == 0)
            {
                return 1;
            }

            // A line exactly as wide as the terminal still occupies one row, hence the -1.
            return ((plain.Length - 1) / width) + 1;
        }

        private void Write(List<string> plain, List<string> coloured)
        {
            int width = GetWidth();
            var buffer = new StringBuilder();

            if (!_firstFrame && _lastRowCount > 0)
            {
                // Move back by TERMINAL ROWS, not by logical lines. A line long enough to wrap
                // consumes more than one row, and counting lines instead left the frame drifting
                // down the screen a little further every cycle, stranding old headers behind it.
                buffer.Append("\u001b[").Append(_lastRowCount).Append('A');
            }

            int rowsWritten = 0;
            for (int i = 0; i < coloured.Count; i++)
            {
                buffer.Append(coloured[i]).Append(AnsiEraseLine).Append('\n');
                rowsWritten += RowsOccupied(plain[i], width);
            }

            // The previous frame may have been taller (hops trimmed, or a warning cleared);
            // blank the surplus rows so nothing stale is left behind.
            for (int i = rowsWritten; i < _lastRowCount; i++)
            {
                buffer.Append(AnsiEraseLine).Append('\n');
                rowsWritten++;
            }

            _lastRowCount = rowsWritten;
            _firstFrame = false;

            _ui.Write(buffer.ToString());
        }
    }
}
