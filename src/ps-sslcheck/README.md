# ps-sslcheck

Cross-platform TLS certificate checker for PowerShell 7+. Runs identically on Windows and Linux — no OpenSSL, no external CLI, no dependencies beyond what's built into .NET (`SslStream`, `TcpClient`, `X509Certificate2`).

## Get-SslInfo

Connects to a remote host over TLS and returns the certificate it presents — including how many days remain until it expires.

### Syntax
```powershell
Get-SslInfo -Url <string> [-Port <int>] [<CommonParameters>]
```

### Parameters

| Parameter | Required | Default | Description |
|---|---|---|---|
| `-Url` | Yes | — | Remote host to check. A scheme, path, or query string is ignored if present (e.g. `https://example.com/path` is treated the same as `example.com`). |
| `-Port` | No | `443`, or the port embedded in `-Url` if present | TCP port to connect on. If `-Url` includes an embedded port (`host:8443`) it's used automatically unless `-Port` is explicitly given, in which case `-Port` wins. |

### Returns

A `SslCertInfo` object:

| Property | Description |
|---|---|
| `Host` | The host that was queried |
| `Port` | The port that was queried |
| `Subject` | Certificate subject |
| `Issuer` | Certificate issuer |
| `NotBefore` | Certificate validity start (UTC) |
| `NotAfter` | Certificate expiration date (UTC) |
| `RemainingDays` | Days until expiry. **Negative if the certificate has already expired.** |
| `Thumbprint` | Certificate SHA-1 thumbprint |

### Examples

**Basic check**
```powershell
Get-SslInfo -Url yahoo.com
```
```
Host          : yahoo.com
Port          : 443
Subject       : CN=*.yahoo.com
Issuer        : CN=DigiCert Global G3 TLS ECC SHA384 2020 CA1, O=DigiCert Inc, C=US
NotBefore     : 2/1/2026 12:00:00 AM
NotAfter      : 3/1/2027 12:00:00 AM
RemainingDays : 223
Thumbprint    : A1B2C3D4E5F6...
```

**Custom port**
```powershell
Get-SslInfo -Url internal.example.com -Port 8443
# or, equivalently:
Get-SslInfo -Url internal.example.com:8443
```

**Scripting: alert on certificates expiring soon**
```powershell
$sites = 'yahoo.com', 'google.com', 'internal.example.com'
$sites | ForEach-Object {
    $cert = Get-SslInfo -Url $_
    if ($cert.RemainingDays -le 30) {
        Write-Warning "$($cert.Host) expires in $($cert.RemainingDays) day(s)!"
    }
}
```

**Just the day count, for pipelines**
```powershell
(Get-SslInfo -Url yahoo.com).RemainingDays
```

### Notes

- **Deliberately accepts any certificate during the handshake** (ignores trust/validity errors like expiration or an unknown CA). This is intentional — the cmdlet's job is to *inspect* a certificate, including ones that are expired, self-signed, or untrusted, not to validate them. Don't rely on `Get-SslInfo` completing successfully as a signal that a certificate is trustworthy; it will happily report on a certificate that a browser would reject.
- If the connection fails (DNS failure, timeout, connection refused, or the server presents no certificate at all), `Get-SslInfo` writes a non-terminating error rather than throwing, so it's safe to use inside a loop checking multiple sites without one failure stopping the rest.
