# ps-mtr

Live traceroute with per-hop packet-loss and latency statistics, in the style of the Linux `mtr` tool. The table redraws in place each cycle; `Ctrl+C` stops it.

On **Windows** the trace is implemented directly against the ICMP API — no `tracert`, no elevation, and considerably faster, because every TTL in a cycle is probed in parallel rather than one at a time with a 4-second timeout each.

On **Linux** it delegates to the installed `mtr` binary. This is deliberate: .NET's `Ping` class falls back to invoking `/bin/ping` when unprivileged, and that path doesn't reliably expose the intermediate hop address from a TTL-expired reply — which is the one thing a traceroute depends on.

## Syntax
```powershell
Start-Mtr [-Target] <string> [-NoDns] [-Interface <string>] [-MaxHops <int>] [-Interval <int>] [-Timeout <int>] [<CommonParameters>]
```

## Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `-Target` | Yes | — | Hostname or IPv4 address to trace to. |
| `-NoDns` | No | Off | Skip reverse-DNS lookups and show IP addresses only (mtr's `-n`). |
| `-Interface` | No | OS routing choice | Send probes from a specific network interface (mtr's `-I`). Accepts the interface name, description, or ID. |
| `-MaxHops` | No | `30` | Maximum number of hops to probe. |
| `-Interval` | No | `1` | Seconds to wait between cycles. |
| `-Timeout` | No | `1000` | Per-probe timeout in milliseconds. |

## Examples

**Basic trace**
```powershell
Start-Mtr 4.2.2.4
```

**No reverse DNS**
```powershell
Start-Mtr -Target 4.2.2.4 -NoDns
```

**Bound to a specific interface**
```powershell
Get-NetAdapter | Select-Object Name, Status     # find the name first
Start-Mtr -Target 4.2.2.4 -Interface "Ethernet"
```

**Shorter trace, faster cycles**
```powershell
Start-Mtr -Target google.com -MaxHops 15 -Interval 1 -Timeout 500
```

## Sample output
```
Target 4.2.2.4   Source 192.168.30.5   Interface VPN-FortiSSL (index 10)
Start-Mtr   Cycle 14   Ctrl+C to stop
Host                                    Loss%   Snt   Last    Avg   Best   Wrst  StDev
 1. Soheil-PC                            0.0%    14    1.0    1.1    0.9    2.0    0.3
 2. int0.client.access.fanaptelecom.n    0.0%    14    3.0    3.2    3.0    4.0    0.4
 3. 172.16.52.97                         7.1%    14    5.0    5.4    4.0    9.0    1.2
10. ???                                100.0%    14      -      -      -      -      -
12. d.resolvers.level3.net               0.0%    14  183.0  187.1  175.0  209.0   11.0
```

The header line shows what is being traced and where probes originate — the source address and the interface name with its IPv4 interface index. Without `-Interface`, that is the address the OS routing table selects; with `-Interface`, the header appends `[bound]` to show probes are explicitly source-bound.

Hops that never answer show as `???` with 100% loss. This is normal and usually means a router is configured not to send ICMP TTL-exceeded messages, **not** that traffic is being dropped there.

## Reading the numbers

Two caveats worth understanding, both inherent to how traceroute works rather than specific to this implementation:

- **Middle-row loss is often not real loss.** Routers rate-limit how quickly they generate ICMP TTL-exceeded replies, and probing all TTLs in parallel makes that throttling look like packet loss. It is common to see 10–40% on intermediate hops while the destination sits at 0%. **The final row is the one to trust for end-to-end loss.**
- **Middle-row latency can exceed the destination's.** Generating a TTL-exceeded message is low-priority work for a router, so the reported time includes that delay. Again, the final row is the meaningful measurement for end-to-end latency.

Timing on intermediate hops is measured locally, because Windows only populates the ICMP API's round-trip field for a successful echo — for TTL-expired replies it returns zero. Each probe therefore runs synchronously on its own dedicated thread so the measurement isn't distorted by thread-pool scheduling.
