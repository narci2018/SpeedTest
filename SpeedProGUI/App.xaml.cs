using System.IO;
using System.Windows;
using System.Windows.Markup;

namespace SpeedPro;

using MessageBox = System.Windows.MessageBox;

public partial class App : System.Windows.Application
{
    // Keep a strong reference to the active theme so GC doesn't collect it
    private ResourceDictionary? _activeTheme;
    public ResourceDictionary? ActiveTheme => _activeTheme;
    private Mutex? _singleInstanceMutex;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        // CLI 自动化模式：SpeedPro.exe cli --url https://x [--proxy http://...] [--samples N]
        if (e.Args.Length > 0 && e.Args[0].Equals("cli", StringComparison.OrdinalIgnoreCase))
        {
            RunCliMode(e.Args);
            return;
        }

        // Single instance: 带信号量的命名互斥体，二次启动直接提示退出
        _singleInstanceMutex = new Mutex(true, @"Local\SpeedPro_SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("SpeedPro 已在运行中。", "SpeedPro",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        LoadTheme("DarkTech");
        new MainWindow().Show();
    }

    private async void RunCliMode(string[] args)
    {
        try { AttachConsole(-1); } catch { }          // attach to the calling console
        int exitCode = 0;
        try
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            string? Get(string name)
            {
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                        return i + 1 < args.Length ? args[i + 1].Trim() : null;
                    if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                        return args[i][(name.Length + 1)..].Trim();
                }
                return null;
            }
            string? urlArg = Get("--url");
            if (urlArg == null || urlArg.Length == 0)
                throw new Exception("usage: SpeedPro.exe cli --url https://host [--proxy http://127.0.0.1:7890] [--samples 1]");
            string proxyArg = Get("--proxy") ?? "";
            int samples = Get("--samples") is { Length: > 0 } sn && int.TryParse(sn, out var n) ? Math.Clamp(n, 1, 5) : 1;

            if (!Uri.TryCreate(urlArg, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new Exception("invalid --url");
            Uri? proxy = null;
            if (proxyArg.Length > 0)
            {
                if (!proxyArg.Contains("://")) proxyArg = "http://" + proxyArg;
                if (!Uri.TryCreate(proxyArg, UriKind.Absolute, out proxy) || proxy.Scheme != "http")
                    throw new Exception("invalid --proxy (http://host:port only)");
            }

            var results = new List<(WebSample? s, Exception? err)>();
            for (int i = 0; i < samples; i++)
            {
                try { results.Add((await WebsiteProbe.RunAsync(uri, proxy), null)); }
                catch (Exception ex) { results.Add((null, ex)); }
            }
            var ok = results.Where(r => r.err == null).Select(r => r.s).ToList();
            var payload = new Dictionary<string, object?>
            {
                ["url"] = uri.ToString(),
                ["proxy"] = proxy?.Authority,
                ["samples"] = results.Count,
                ["ok"] = ok.Count,
                ["avg_speed_mbps"] = ok.Count > 0 ? Math.Round(ok.Average(s => s.BodyBytes / Math.Max(0.001, s.ContentMs / 1000.0)) * 8 / 1e6, 2) : 0,
                ["avg_ttfb_ms"] = ok.Count > 0 ? Math.Round(ok.Average(s => s.TtfbMs), 1) : 0,
                ["results"] = results.Select(r => r.err == null
                    ? (object)new Dictionary<string, object?>
                    {
                        ["status"] = r.s!.StatusCode, ["final_url"] = r.s.FinalUrl,
                        ["speed_mbps"] = Math.Round(r.s.BodyBytes / Math.Max(0.001, r.s.ContentMs / 1000.0) * 8 / 1e6, 2),
                        ["ttfb_ms"] = Math.Round(r.s.TtfbMs, 1), ["dns_ms"] = Math.Round(r.s.DnsMs, 1),
                        ["tcp_ms"] = Math.Round(r.s.TcpMs, 1), ["tls_ms"] = Math.Round(r.s.TlsMs, 1),
                        ["total_ms"] = Math.Round(r.s.TotalMs, 1), ["bytes"] = r.s.BodyBytes,
                        ["redirects"] = r.s.Redirects, ["ip"] = r.s.Ip, ["via_proxy"] = r.s.Via.Length > 0
                    }
                    : new Dictionary<string, object?> { ["error"] = r.err!.Message }).ToList()
            };
            string json = System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            exitCode = ok.Count > 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine("{\"error\":\"" + ex.Message.Replace("\"", "'").Replace("\n", " ") + "\"}");
            exitCode = 2;
        }
        finally
        {
            // 让父控制台提示符回到行首
            try { System.Windows.Forms.SendKeys.SendWait("{ENTER}"); } catch { }
            Environment.Exit(exitCode);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Loads a theme dictionary by pack URI (embedded Resource).
    /// The theme .xaml files are embedded as plain text (Resource build action,
    /// not compiled to BAML), so we read the text and parse it with
    /// XamlReader.Load(XmlReader). Using ResourceDictionary.Source would parse the
    /// text with a *restrictive* reader that rejects WPF types like ResourceDictionary.
    /// Keeps a strong reference in _activeTheme to prevent GC.
    /// </summary>
    internal void LoadTheme(string themeName)
    {
        if (_activeTheme != null && Resources.MergedDictionaries.Contains(_activeTheme))
            Resources.MergedDictionaries.Remove(_activeTheme);
        _activeTheme = null;

        try
        {
            _activeTheme = ParseTheme(themeName);
        }
        catch
        {
            // Fallback to DarkTech
            _activeTheme = ParseTheme("DarkTech");
        }

        Resources.MergedDictionaries.Add(_activeTheme);
    }

    private ResourceDictionary ParseTheme(string themeName)
    {
        var uri = new System.Uri($"pack://application:,,,/Themes/{themeName}.xaml", System.UriKind.Absolute);
        var info = GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Resource '{uri}' not found.");
        string xaml;
        using (var reader = new StreamReader(info.Stream))
            xaml = reader.ReadToEnd();

        // NOTE: XamlReader.Parse(string) uses a *restrictive* reader that rejects
        // WPF types such as ResourceDictionary. XamlReader.Load(XmlReader) uses the
        // trusted schema context and works for theme dictionaries.
        using var xmlReader = System.Xml.XmlReader.Create(new StringReader(xaml));
        return (ResourceDictionary)XamlReader.Load(xmlReader);
    }
}
