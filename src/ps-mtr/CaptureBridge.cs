using System;
using System.IO;
using System.Reflection;

namespace PSAdminTools.Mtr
{
    /// <summary>
    /// Thin reflective wrapper around MtrCapture.dll.
    ///
    /// The capture assembly targets net10.0-windows because SharpPcap cannot run on
    /// netstandard2.0. Referencing it directly would force this project onto the same target and
    /// make the whole of Start-Mtr Windows-only, losing both the cross-platform ICMP mode and the
    /// Linux mtr delegation. Loading it by reflection keeps that separation: if the assembly is
    /// missing, or Npcap is not installed, TCP mode simply falls back to unidentified hops.
    ///
    /// The capture API deliberately exposes only primitives, so nothing here needs its types.
    /// </summary>
    internal sealed class CaptureBridge : IDisposable
    {
        private readonly object _instance;
        private readonly MethodInfo _takeHop;
        private readonly MethodInfo _stop;

        private CaptureBridge(object instance, MethodInfo takeHop, MethodInfo stop)
        {
            _instance = instance;
            _takeHop = takeHop;
            _stop = stop;
        }

        /// <summary>
        /// Loads the capture assembly and starts capturing on the interface owning the given
        /// source address. Returns null when capture is unavailable for any reason - a missing
        /// assembly, no Npcap, or a driver that refuses access.
        /// </summary>
        public static CaptureBridge? TryStart(string sourceAddress, Action<string> log)
        {
            try
            {
                // Sits next to MtrCheck.dll in Bin.
                string? binPath = Path.GetDirectoryName(typeof(CaptureBridge).Assembly.Location);
                if (string.IsNullOrEmpty(binPath))
                {
                    log("Could not determine the module directory; hop identification disabled.");
                    return null;
                }

                string capturePath = Path.Combine(binPath, "MtrCapture.dll");
                if (!File.Exists(capturePath))
                {
                    log($"MtrCapture.dll not found at {capturePath}; hops will not be identified.");
                    return null;
                }

                Assembly assembly = Assembly.LoadFrom(capturePath);
                Type? type = assembly.GetType("PSAdminTools.MtrCapture.HopCapture");
                if (type == null)
                {
                    log("HopCapture type not found in MtrCapture.dll; hops will not be identified.");
                    return null;
                }

                MethodInfo? isAvailable = type.GetMethod("IsAvailable", BindingFlags.Public | BindingFlags.Static);
                if (isAvailable != null && isAvailable.Invoke(null, null) is bool available && !available)
                {
                    log("Npcap is not installed; hops will not be identified. Install it from https://npcap.com");
                    return null;
                }

                object? instance = Activator.CreateInstance(type);
                if (instance == null)
                {
                    return null;
                }

                MethodInfo? start = type.GetMethod("Start", new[] { typeof(string) });
                MethodInfo? takeHop = type.GetMethod("TakeHop", new[] { typeof(int) });
                MethodInfo? stop = type.GetMethod("Stop", Type.EmptyTypes);

                if (start == null || takeHop == null || stop == null)
                {
                    log("MtrCapture.dll does not expose the expected methods; hops will not be identified.");
                    return null;
                }

                if (start.Invoke(instance, new object[] { sourceAddress }) is bool started && !started)
                {
                    log("Packet capture could not be started. If Npcap was installed with " +
                        "'Restrict to Administrators', run this session elevated or reinstall Npcap without that option.");
                    return null;
                }

                return new CaptureBridge(instance, takeHop, stop);
            }
            catch (Exception ex)
            {
                log($"Hop identification unavailable: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the router that answered for a probe's local port, or null if none has.
        /// The capture returns "address|arrivalTicks"; round-trip time is derived here by
        /// comparing arrival against the moment the probe was sent.
        /// </summary>
        public bool TryTakeHop(int localPort, DateTime sentUtc, out string address, out double roundTripMs)
        {
            address = string.Empty;
            roundTripMs = 0d;

            try
            {
                if (_takeHop.Invoke(_instance, new object[] { localPort }) is not string raw)
                {
                    return false;
                }

                int separator = raw.LastIndexOf('|');
                if (separator <= 0)
                {
                    return false;
                }

                address = raw.Substring(0, separator);

                if (long.TryParse(raw.Substring(separator + 1), out long arrivalTicks))
                {
                    double elapsed = new DateTime(arrivalTicks, DateTimeKind.Utc)
                        .Subtract(sentUtc).TotalMilliseconds;

                    // Clock skew or a reply that beat our own timestamp would otherwise show a
                    // negative latency.
                    roundTripMs = elapsed > 0d ? elapsed : 0d;
                }

                return address.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _stop.Invoke(_instance, null);
            }
            catch (Exception)
            {
                // Nothing useful to do if the capture is already gone.
            }
        }
    }
}
