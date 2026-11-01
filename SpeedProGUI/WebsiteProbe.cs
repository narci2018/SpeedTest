using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace SpeedPro;

/// <summary>Shared result record for one website probe sample.</summary>
public sealed record WebSample(
    int StatusCode, string Server, string ContentType, long BodyBytes,
    double DnsMs, double TcpMs, double TlsMs, double TtfbMs, double ContentMs, double TotalMs,
    string FinalUrl, string Ip, int Redirects, string Via);

/// <summary>
/// Static probe library: polite single-connection website measurement, tracert runner
/// and formatting helpers. Shared by the Website page, Monitor page, Route Monitor
/// page and the CLI mode.
/// </summary>
public static class WebsiteProbe
{
    /// <summary>
    /// Builds a connected (and TLS-wrapped for https) stream to the target,
    /// optionally through an HTTP proxy CONNECT tunnel. Returns the stream and
    /// the DNS/TCP/TLS stage timings.
    /// </summary>
    public static async Task<(Stream stream, string ip, double dnsMs, double tcpMs, double tlsMs)>
        ConnectStreamAsync(Uri uri, Uri? proxy, CancellationToken ct)
    {
        // ── DNS: target (direct) or proxy host ──
        string connectHost = proxy?.Host ?? uri.Host;
        int connectPort = proxy?.Port ?? uri.Port;
        var sw = Stopwatch.StartNew();
        IPAddress[] addrs;
        try { addrs = await Dns.GetHostAddressesAsync(connectHost).WaitAsync(TimeSpan.FromSeconds(6), ct); }
        catch (TimeoutException) { throw new Exception("DNS lookup timed out (>6s): " + connectHost); }
        if (addrs.Length == 0) throw new Exception("DNS found no address for " + connectHost);
        var ipAddr = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0];
        string ip = ipAddr.ToString();
        double dnsMs = Math.Max(0.05, sw.Elapsed.TotalMilliseconds);

        // ── TCP: to target (direct) or to proxy ──
        var socket = new Socket(ipAddr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        Stream? stream = null;
        SslStream? ssl = null;
        double tcpMs, tlsMs = 0;
        try
        {
            sw.Restart();
            try { await socket.ConnectAsync(new IPEndPoint(ipAddr, connectPort), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(8), ct); }
            catch (TimeoutException) { throw new Exception($"TCP connect timed out (>8s): {connectHost}:{connectPort}"); }
            catch (SocketException se) { throw new Exception($"TCP connect failed: {se.SocketErrorCode} → {connectHost}:{connectPort}"); }
            tcpMs = sw.Elapsed.TotalMilliseconds;
            stream = new NetworkStream(socket, ownsSocket: true);

            // ── Proxy tunnel: CONNECT target authority ──
            if (proxy != null)
            {
                string authority = uri.Host + ":" + uri.Port;   // always include port (curl-compatible)
                string connectReq = $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\nProxy-Connection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(connectReq), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
                string resp = await ReadProxyConnectResponseAsync(stream, ct);
                var cm = System.Text.RegularExpressions.Regex.Match(resp, @"HTTP/\d(?:\.\d)?\s+(\d{3})");
                if (!cm.Success || cm.Groups[1].Value != "200")
                    throw new Exception("Proxy CONNECT failed: " + resp.Split("\r\n")[0]);
            }

            if (uri.Scheme == "https")
            {
                sw.Restart();
                ssl = new SslStream(stream);
                try
                {
                    await ssl.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions { TargetHost = uri.Host }, CancellationToken.None)
                        .WaitAsync(TimeSpan.FromSeconds(8), ct);
                }
                catch (TimeoutException) { throw new Exception("TLS handshake timed out (>8s): " + uri.Host); }
                catch (System.Security.Authentication.AuthenticationException aex)
                { throw new Exception("TLS certificate error for " + uri.Host + ": " + aex.Message); }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    throw new Exception(
                        $"TLS handshake was forcibly reset for {uri.Host} — TCP connected fine, so the domain is likely blocked by your network (SNI reset) or refused by the edge. Try opening the site in a browser to confirm. If you have a proxy (Clash/v2rayN/…), set it in Settings → Proxy Server and retry. [{ex.InnerException?.Message ?? ex.Message}]");
                }
                tlsMs = sw.Elapsed.TotalMilliseconds;
                stream = ssl;
            }
            return (stream, ip, dnsMs, tcpMs, tlsMs);
        }
        catch
        {
            try { stream?.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Bandwidth probe: opens `connections` parallel downloads of a large object
    /// and counts wire bytes for `seconds` seconds (headers excluded).
    /// Returns aggregated throughput in Mbps and total bytes pulled.
    /// </summary>
    public static async Task<(double Mbps, long TotalBytes, double Seconds)> MeasureDownloadAsync(
        Uri url, int connections, int seconds, Uri? proxy, CancellationToken ct, Action<long>? onProgress = null)
    {
        long total = 0;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        var start = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, connections).Select(async _ =>
        {
            try
            {
                var (stream, _, _, _, _) = await ConnectStreamAsync(url, proxy, cts.Token);
                using (stream)
                {
                    string hostHeader = url.IsDefaultPort ? url.Host : url.Authority;
                    string req = $"GET {url.PathAndQuery} HTTP/1.1\r\nHost: {hostHeader}\r\n" +
                                 "User-Agent: SpeedPro-BandwidthProbe/3.13\r\nAccept: */*\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(req), cts.Token);
                    // 读掉响应头
                    var hdr = new MemoryStream();
                    var buf = new byte[65536];
                    int hn;
                    while ((hn = await stream.ReadAsync(buf, cts.Token)) > 0)
                    {
                        hdr.Write(buf, 0, hn);
                        var all = hdr.GetBuffer(); int len = (int)hdr.Length;
                        bool done = false;
                        for (int k = Math.Max(0, len - hn - 3); k <= len - 4; k++)
                            if (all[k] == 13 && all[k + 1] == 10 && all[k + 2] == 13 && all[k + 3] == 10)
                            { done = true; break; }
                        if (done) break;
                    }
                    // 流式计数正文，直到时间窗关闭
                    while (true)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        int n = await stream.ReadAsync(buf, cts.Token);
                        if (n <= 0) break;
                        Interlocked.Add(ref total, n);
                        onProgress?.Invoke(Interlocked.Read(ref total));
                    }
                }
            }
            catch (OperationCanceledException) { /* 时间窗关闭 — 正常结束 */ }
            catch { /* 单连接失败不影响整体 */ }
        }));
        cts.Dispose();
        double secs = Math.Max(0.2, start.Elapsed.TotalSeconds);
        return (total * 8 / secs / 1e6, total, secs);
    }

    /// <summary>
    /// Runs one polite single-connection GET: DNS → TCP → (proxy CONNECT) → TLS → TTFB → body,
    /// following up to 5 redirects (http→https etc.). Connection: close; 25 MB cap.
    /// When a proxy is set, DNS/TCP target the proxy and a CONNECT tunnel carries the TLS.
    /// </summary>
    public static async Task<WebSample> RunAsync(Uri startUri, Uri? proxy, CancellationToken ct = default)
    {
        var uri = startUri;
        int redirects = 0;
        while (true)
        {
            var (stream, ip, dnsMs, tcpMs, tlsMs) = await ConnectStreamAsync(uri, proxy, ct);
            SslStream? ssl = stream as SslStream;
            try
            {
                // ── HTTP GET (Connection: close → read to EOF) ──
                string hostHeader = uri.IsDefaultPort ? uri.Host : uri.Authority;
                string req = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                             $"Host: {hostHeader}\r\n" +
                             "User-Agent: SpeedPro-WebsiteTest/3.11\r\n" +
                             "Accept: */*\r\n" +
                             "Accept-Encoding: gzip, deflate\r\n" +
                             "Connection: close\r\n\r\n";
                var reqBytes = Encoding.ASCII.GetBytes(req);
                var totalSw = Stopwatch.StartNew();
                await stream.WriteAsync(reqBytes, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);

                // Read until end of headers → TTFB = first byte received
                var headerBuf = new MemoryStream();
                var chunk = new byte[8192];
                int n, tail = 0;
                bool headerDone = false;
                double ttfbMs = 0;
                try
                {
                    while (!headerDone &&
                           (n = await stream.ReadAsync(chunk, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct)) > 0)
                    {
                        if (ttfbMs == 0) ttfbMs = Math.Max(0.05, totalSw.Elapsed.TotalMilliseconds);
                        headerBuf.Write(chunk, 0, n);
                        var buf = headerBuf.GetBuffer();
                        int len = (int)headerBuf.Length;
                        for (int k = Math.Max(0, len - n - 3); k <= len - 4; k++)
                        {
                            if (buf[k] == 13 && buf[k + 1] == 10 && buf[k + 2] == 13 && buf[k + 3] == 10)
                            {
                                headerDone = true;
                                tail = len - (k + 4);
                                break;
                            }
                        }
                    }
                }
                catch (IOException io)
                {
                    throw new Exception(
                        "Connection was reset while waiting for the HTTP response — the target or a middlebox closed the link (site down, bot protection, or unstable path). [" +
                        (io.InnerException?.Message ?? io.Message) + "]");
                }
                if (!headerDone)
                    throw new Exception("Connection closed before any HTTP response — target is not an HTTP service.");

                var headerBytes = headerBuf.ToArray();
                int headerLen = headerBytes.Length - tail;
                string headerText = Encoding.ASCII.GetString(headerBytes, 0, headerLen);
                var lines = headerText.Split("\r\n");
                var m = System.Text.RegularExpressions.Regex.Match(lines[0], @"HTTP/\d(?:\.\d)?\s+(\d{3})");
                if (!m.Success) throw new Exception("Unrecognized HTTP status line: " + lines[0]);
                int status = int.Parse(m.Groups[1].Value);
                string GetHeader(string name) => lines.Skip(1)
                    .Select(l => l.Split(new[] { ':' }, 2))
                    .Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[1].Trim())
                    .FirstOrDefault() ?? "";

                // Follow redirects (http→https, host changes, …) up to 5 hops
                if (status is 301 or 302 or 303 or 307 or 308 && redirects < 5)
                {
                    string loc = GetHeader("Location");
                    if (!string.IsNullOrEmpty(loc))
                    {
                        uri = new Uri(uri, loc);
                        redirects++;
                        continue;   // finally below closes the current connection first
                    }
                }

                // ── Body: header tail + read to EOF ──
                long bodyBytes = tail;
                try
                {
                    while ((n = await stream.ReadAsync(chunk, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct)) > 0)
                    {
                        bodyBytes += n;
                        if (bodyBytes > 25_000_000) break;   // 25 MB politeness cap
                    }
                }
                catch (IOException io)
                {
                    throw new Exception(
                        $"Connection was reset while downloading the response body (received {FormatBytes(bodyBytes)} before the reset). [" +
                        (io.InnerException?.Message ?? io.Message) + "]");
                }
                double totalMs = totalSw.Elapsed.TotalMilliseconds;
                double contentMs = Math.Max(0.05, totalMs - ttfbMs);
                return new WebSample(status, GetHeader("Server"), GetHeader("Content-Type"), bodyBytes,
                    dnsMs, tcpMs, tlsMs, ttfbMs, contentMs, totalMs, uri.ToString(), ip, redirects,
                    proxy != null ? proxy.Authority : "");
            }
            finally
            {
                ssl?.Dispose();                 // disposes the inner NetworkStream + socket too
                if (ssl == null) stream?.Dispose();
            }
        }
    }

    /// <summary>Reads a CONNECT response (headers only) from the proxy tunnel.</summary>
    private static async Task<string> ReadProxyConnectResponseAsync(Stream stream, CancellationToken ct)
    {
        var buf = new MemoryStream();
        var chunk = new byte[4096];
        int n;
        while ((n = await stream.ReadAsync(chunk, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(15), ct)) > 0)
        {
            buf.Write(chunk, 0, n);
            var all = buf.GetBuffer();
            int len = (int)buf.Length;
            for (int k = Math.Max(0, len - n - 3); k <= len - 4; k++)
                if (all[k] == 13 && all[k + 1] == 10 && all[k + 2] == 13 && all[k + 3] == 10)
                    return Encoding.ASCII.GetString(all, 0, k + 4);
        }
        throw new Exception("Proxy closed the connection before answering CONNECT.");
    }

    /// <summary>
    /// Runs tracert (-d) against a host, streaming every raw line to <paramref name="onLine"/>
    /// and returning parsed hops (hop number, best latency of the probes, endpoint label).
    /// Locale-robust: matches numeric probes regardless of the unit word (ms/毫秒).
    /// </summary>
    public static async Task<List<(int Hop, double? BestMs, string Endpoint)>> RunTracertAsync(
        string host, Action<string>? onLine, Action<Process>? onProcess, CancellationToken ct)
    {
        var hops = new List<(int, double?, string)>();
        var psi = new ProcessStartInfo("tracert", $"-d {host}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        try { psi.StandardOutputEncoding = Encoding.GetEncoding(936); } catch { }
        using var proc = Process.Start(psi)!;
        onProcess?.Invoke(proc);
        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (line.Trim().Length == 0) continue;
            onLine?.Invoke(line);
            var hop = ParseTracertLine(line);
            if (hop != null && hop.Value.hop > 0) hops.Add(hop.Value);
        }
        await proc.WaitForExitAsync(ct);
        return hops;
    }

    /// <summary>Parses one tracert line: hop number, best latency (any locale unit), endpoint.</summary>
    public static (int hop, double? bestMs, string ep)? ParseTracertLine(string line)
    {
        var tok = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tok.Length < 2 || !int.TryParse(tok[0], out var hop) || hop <= 0) return null;
        var lat = new List<double>();
        string? endpoint = null;
        for (int i = 1; i < tok.Length; i++)
        {
            string t = tok[i];
            if (t == "*") continue;
            string num = t.StartsWith("<") ? t[1..] : t;
            bool numeric = num.Length > 0 && num.All(char.IsDigit);
            if (numeric && i + 1 < tok.Length && tok[i + 1] != "*")
            {
                // 数字 + 单位 token（ms / 毫秒 / 任何非数字词）
                if (double.TryParse(num, out var v)) { lat.Add(v); i++; continue; }
            }
            if (t.All(c => c < 128) && (t.Contains('.') || t.Contains(':')) && !double.TryParse(t, out _))
                endpoint = t;
        }
        if (endpoint == null && lat.Count == 0) return null;
        return (hop, lat.Count > 0 ? lat.Min() : (double?)null, endpoint ?? "timeout");
    }

    /// <summary>One ICMP echo to a host/IP; null on failure or timeout.</summary>
    public static async Task<double?> PingRttAsync(string host, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var p = new System.Net.NetworkInformation.Ping();
            var reply = await p.SendPingAsync(host, timeoutMs).WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 500), ct);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch { return null; }
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_048_576 => (bytes / 1_048_576.0).ToString("F2") + " MB",
        >= 1024      => (bytes / 1024.0).ToString("F1") + " KB",
        _            => bytes + " B"
    };

    public static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1_048_576 => (bytesPerSec / 1_048_576.0).ToString("F2") + " MB/s",
        >= 1024      => (bytesPerSec / 1024.0).ToString("F1") + " KB/s",
        _            => bytesPerSec.ToString("F0") + " B/s"
    };
}
