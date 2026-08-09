using System;

namespace PSAdminTools.Mtr
{
    /// <summary>
    /// Cumulative statistics for a single TTL hop, aggregated across every cycle so far
    /// (matching real mtr, which reports running totals rather than per-cycle values).
    /// </summary>
    public sealed class HopStats
    {
        private readonly object _sync = new object();
        private double _sum;
        private double _sumOfSquares;

        public HopStats(int ttl)
        {
            Ttl = ttl;
            Best = double.MaxValue;
        }

        public int Ttl { get; }

        /// <summary>Last address that replied for this TTL. Null if nothing has ever replied.</summary>
        public string? Address { get; private set; }

        /// <summary>Resolved reverse-DNS name, when available and -NoDns was not used.</summary>
        public string? HostName { get; set; }

        /// <summary>
        /// Address known from a source other than this hop's own replies - currently only the
        /// default gateway at hop 1, read from the routing table. Used for display when the hop
        /// has never answered, so a silent gateway shows its address rather than "???".
        /// </summary>
        public string? FallbackAddress { get; set; }

        public int Sent { get; private set; }
        public int Received { get; private set; }
        public double Last { get; private set; }
        public double Best { get; private set; }
        public double Worst { get; private set; }

        /// <summary>True once a reply for this TTL came from the trace target itself.</summary>
        public bool IsDestination { get; private set; }

        public void RecordReply(string address, double roundTripMs, bool isDestination)
        {
            lock (_sync)
            {
                Sent++;
                Received++;
                Address = address;

                if (isDestination)
                {
                    IsDestination = true;
                }

                Last = roundTripMs;
                if (roundTripMs < Best) { Best = roundTripMs; }
                if (roundTripMs > Worst) { Worst = roundTripMs; }

                _sum += roundTripMs;
                _sumOfSquares += roundTripMs * roundTripMs;
            }
        }

        public void RecordTimeout()
        {
            lock (_sync)
            {
                Sent++;
            }
        }

        public double LossPercent
        {
            get
            {
                lock (_sync)
                {
                    return Sent == 0 ? 0d : (Sent - Received) * 100d / Sent;
                }
            }
        }

        public double Average
        {
            get
            {
                lock (_sync)
                {
                    return Received == 0 ? 0d : _sum / Received;
                }
            }
        }

        /// <summary>Population standard deviation of the round-trip times seen so far.</summary>
        public double StandardDeviation
        {
            get
            {
                lock (_sync)
                {
                    if (Received < 2) { return 0d; }

                    double mean = _sum / Received;
                    double variance = (_sumOfSquares / Received) - (mean * mean);
                    return variance <= 0d ? 0d : Math.Sqrt(variance);
                }
            }
        }

        /// <summary>Display value for Best - 0 rather than double.MaxValue when nothing replied yet.</summary>
        public double BestForDisplay
        {
            get
            {
                lock (_sync)
                {
                    return Received == 0 ? 0d : Best;
                }
            }
        }

        /// <summary>True if this hop has never produced a reply (renders as "???" like mtr).</summary>
        public bool IsUnknown
        {
            get
            {
                lock (_sync)
                {
                    return Received == 0;
                }
            }
        }
    }
}
