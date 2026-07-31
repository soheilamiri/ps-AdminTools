using System;
using System.Collections.Generic;
using System.Management.Automation.Host;
using System.Text;

namespace PSAdminTools.Mtr
{
    /// <summary>
    /// Draws the live mtr-style table. When the host supports cursor positioning (Windows
    /// Terminal, the VS Code integrated terminal, conhost) the table is redrawn in place each
    /// cycle. When it doesn't - for example when output is piped or redirected - it falls back
    /// to printing each cycle sequentially rather than producing escape-code garbage.
    /// </summary>
    internal sealed class MtrRenderer
    {
        private const string AnsiGreen = "\u001b[32m";
        private const string AnsiOrange = "\u001b[38;5;208m";
        private const string AnsiRed = "\u001b[31m";
        private const string AnsiDim = "\u001b[90m";
        private const string AnsiReset = "\u001b[0m";

        private const int HostColumnWidth = 34;

        private readonly PSHostUserInterface _ui;
        private readonly bool _supportsCursor;
        private Coordinates _origin;
        private int _lastLineCount;

        public MtrRenderer(PSHostUserInterface ui)
        {
            _ui = ui;

            try
            {
                _origin = ui.RawUI.CursorPosition;
                _supportsCursor = true;
            }
            catch (Exception)
            {
                // Host has no usable RawUI (redirected output, remoting, some editors).
                _supportsCursor = false;
            }
        }

        public void Render(string target, int cycle, IReadOnlyList<HopStats> hops, bool noDns)
        {
            var plainLines = new List<string>();
            var colouredLines = new List<string>();

            string header = $"Start-Mtr  {target}";
            string cycleText = $"Cycle {cycle}   Ctrl+C to stop";
            plainLines.Add(header + "   " + cycleText);
            colouredLines.Add(header + "   " + AnsiDim + cycleText + AnsiReset);

            string columns =
                "Host".PadRight(HostColumnWidth + 4) +
                "Loss%".PadLeft(6) +
                "Snt".PadLeft(6) +
                "Last".PadLeft(7) +
                "Avg".PadLeft(7) +
                "Best".PadLeft(7) +
                "Wrst".PadLeft(7) +
                "StDev".PadLeft(7);
            plainLines.Add(columns);
            colouredLines.Add(columns);

            foreach (HopStats hop in hops)
            {
                BuildRow(hop, noDns, out string plain, out string coloured);
                plainLines.Add(plain);
                colouredLines.Add(coloured);
            }

            if (_supportsCursor)
            {
                RenderInPlace(plainLines, colouredLines);
            }
            else
            {
                RenderSequential(colouredLines);
            }
        }

        private void BuildRow(HopStats hop, bool noDns, out string plain, out string coloured)
        {
            string label;
            if (hop.IsUnknown)
            {
                label = "???";
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

            plain = index + hostCell + lossCell + sentCell + lastCell + avgCell + bestCell + worstCell + stDevCell;

            string lossColour = loss <= 0d ? AnsiGreen : (loss >= 100d ? AnsiRed : AnsiOrange);
            string colouredHost = hop.IsUnknown ? AnsiDim + hostCell + AnsiReset : hostCell;

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

        private void RenderInPlace(List<string> plainLines, List<string> colouredLines)
        {
            int bufferWidth;
            try
            {
                _ui.RawUI.CursorPosition = _origin;
                bufferWidth = _ui.RawUI.BufferSize.Width;
            }
            catch (Exception)
            {
                // Window resized or host became unavailable mid-run - degrade gracefully.
                RenderSequential(colouredLines);
                return;
            }

            var buffer = new StringBuilder();
            for (int i = 0; i < colouredLines.Count; i++)
            {
                // Pad using the PLAIN length so ANSI escape codes (zero visible width) don't
                // throw the erase-to-end-of-line padding off.
                int padding = Math.Max(0, bufferWidth - 1 - plainLines[i].Length);
                buffer.Append(colouredLines[i]).Append(new string(' ', padding)).Append('\n');
            }

            // A previous frame may have been taller (a hop dropped off); blank the leftovers.
            for (int i = colouredLines.Count; i < _lastLineCount; i++)
            {
                buffer.Append(new string(' ', Math.Max(0, bufferWidth - 1))).Append('\n');
            }

            _ui.Write(buffer.ToString());
            _lastLineCount = colouredLines.Count;
        }

        private void RenderSequential(List<string> colouredLines)
        {
            var buffer = new StringBuilder();
            foreach (string line in colouredLines)
            {
                buffer.Append(line).Append('\n');
            }
            buffer.Append('\n');
            _ui.Write(buffer.ToString());
        }
    }
}
