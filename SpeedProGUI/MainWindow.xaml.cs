using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;

// WinForms（托盘）与 WPF 类型歧义消歧
using Point = System.Windows.Point;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Size = System.Windows.Size;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;

namespace SpeedPro;

public enum EngineType { SpeedTestGo, LibreSpeed }
public enum NavPage { Test, Website, Monitor, Iperf3, Ethr, Pingularity, Tools, History, Settings }

public record HistoryEntry(
    string Time, string Engine, double Download, double Upload,
    double Ping, double Jitter, string Ip, string Isp, string Server, string Quality);

public partial class MainWindow : Window
{
    // ── State ───────────────────────────────────────────────────────────────
    private EngineType _engine = EngineType.LibreSpeed;  // LibreSpeed 更稳定，speedtest-go 部分服务器上传失败
    private NavPage _currentPage = Environment.GetCommandLineArgs().Any(a => a.Equals("ethr", StringComparison.OrdinalIgnoreCase)) ? NavPage.Ethr : NavPage.Test;
    private bool _isRunning;
    private CancellationTokenSource? _cts;

    private readonly List<double> _dlSamples = new();
    private readonly List<double> _ulSamples = new();
    private readonly ObservableCollection<string> _logs = new();
    private readonly ObservableCollection<HistoryEntry> _history = new();

    // Ethr state
    private Process? _ethrServer;
    private int _ethrServerPort = 8888;
    private double _ethrThroughput = 0;
    private bool _ulFailed;  // true when ul_speed == -1 (upload test failed)
    private bool _ethrTesting;
    // Theme state: only two themes, cycled by clicking the title-bar button
    private string _currentTheme = "DarkTech";
    private static readonly string[] ThemeCycle = { "DarkTech", "ClassicLight" };

    // Iperf3 state
    private Process? _iperf3Server;
    private int _iperf3ServerPort = 5201;
    private bool _iperf3Testing;

    // Pingularity state
    private Process? _pingularity;
    private readonly string _pingDbPath;

    // Chart buffers
    private int _chartPoints = 0;
    private const int ChartMaxPoints = 150;
    private double[] _dlChartBuf = new double[ChartMaxPoints];
    private double[] _ulChartBuf = new double[ChartMaxPoints];

    // Gauge
    private double _gaugeTarget = 0;
    private double _gaugeCurrent = 0;

    public MainWindow()
    {
        InitializeComponent();
        // 中文 Windows 上 tracert 输出为 GBK——注册代码页编码提供程序以正确解码
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
        HistoryGrid.ItemsSource = _history;
        NativeMethods.SetProcessDPIAware();

        SysHostname.Text = "Host: " + Environment.MachineName;
        SysOS.Text       = "OS: "   + Environment.OSVersion.VersionString;
        SysArch.Text     = "Arch: " + RuntimeInformation.ProcessArchitecture;

        _pingDbPath = global::System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "pingularity.db");

        DrawGauge(0);
        DrawEmptyChart(DLChartCanvas, "Waiting for data…");
        DrawEmptyChart(ULChartCanvas, "Waiting for data…");
        ApplyLanguage();
        LoadConfig();
        LoadHistory();
        LoadMonitor();
        InitTrayIcon();
        HistoryChartCanvas.SizeChanged += (s, e) => { if (HistoryGrid != null) ApplyHistoryFilter(); };
        MonitorChartCanvas.SizeChanged += (s, e) => DrawMonitorChart();
        _ = CheckForUpdatesAsync(manual: false);
        _ = AnimationLoop();
        _ = TempShot();
    }

    // ── TEMP: in-app screenshot for layout diagnosis ────────────────────────
    private async Task TempShot()
    {
        var args = Environment.GetCommandLineArgs();

        // Optional startup page= (test|iperf3|ethr|ping|history|settings) — works with or without "shot"
        var pArg = args.FirstOrDefault(a => a.StartsWith("page=", StringComparison.OrdinalIgnoreCase));
        if (pArg != null)
        {
            var tag = pArg[5..].ToLowerInvariant() switch
            {
                "website"  => "website",
                "monitor"  => "monitor",
                "tools"    => "tools",
                "iperf3"   => "iperf3",
                "ethr"     => "ethr",
                "ping"     => "pingularity",
                "history"  => "history",
                "settings" => "settings",
                _          => "test"
            };
            _currentPage = tag switch
            {
                "website" => NavPage.Website, "monitor" => NavPage.Monitor,
                "tools" => NavPage.Tools,
                "iperf3" => NavPage.Iperf3, "ethr" => NavPage.Ethr,
                "pingularity" => NavPage.Pingularity,
                "history" => NavPage.History, "settings" => NavPage.Settings, _ => NavPage.Test
            };
            SetNavActive(tag);
            ShowPage(_currentPage);
        }

        if (!args.Any(a => a.Equals("shot", StringComparison.OrdinalIgnoreCase))) return;
        var wArg = args.FirstOrDefault(a => a.StartsWith("w=", StringComparison.OrdinalIgnoreCase));
        var hArg = args.FirstOrDefault(a => a.StartsWith("h=", StringComparison.OrdinalIgnoreCase));
        if (wArg != null) Width = int.Parse(wArg[2..]);
        if (hArg != null) Height = int.Parse(hArg[2..]);

        // Optional lang=en|zh
        var langArg = args.FirstOrDefault(a => a.StartsWith("lang=", StringComparison.OrdinalIgnoreCase));
        if (langArg != null && !Loc.Lang.Equals(langArg[5..], StringComparison.OrdinalIgnoreCase))
        {
            Loc.Toggle();
            ApplyLanguage();
        }
        // Optional theme=dark|light
        var themeArg = args.FirstOrDefault(a => a.StartsWith("theme=", StringComparison.OrdinalIgnoreCase));
        if (themeArg != null)
            SetTheme(themeArg[6..].StartsWith("light", StringComparison.OrdinalIgnoreCase) ? "ClassicLight" : "DarkTech");

        await Task.Delay(1800);

        // Optional autorun=website — trigger a test before capturing (layout regression shots)
        var runArg = args.FirstOrDefault(a => a.StartsWith("run=", StringComparison.OrdinalIgnoreCase));
        if (runArg != null && runArg[4..].Equals("website", StringComparison.OrdinalIgnoreCase))
        {
            WebsiteRun_Click(null, null!);
            while (_websiteTesting) await Task.Delay(500);
            await Task.Delay(600);
        }

        var rtb = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(this);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        var outArg = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.OrdinalIgnoreCase));
        var outPath = outArg != null ? outArg[4..] : @"C:\Users\narci\AppData\Local\Temp\opencode\shot.png";
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
        using var fs = System.IO.File.Create(outPath);
        enc.Save(fs);
        System.Diagnostics.Debug.WriteLine("SHOT SAVED");
        Close();
    }

    // ── Animation ───────────────────────────────────────────────────────────
    private async Task AnimationLoop()
    {
        while (true)
        {
            await Task.Delay(30);
            if (Math.Abs(_gaugeCurrent - _gaugeTarget) > 0.01)
            {
                _gaugeCurrent += (_gaugeTarget - _gaugeCurrent) * 0.12;
                DrawGauge(_gaugeCurrent);
            }
            if (_dlSamples.Count > 0)
            {
                DrawDLChart();
                DrawULChart();
            }
        }
    }

    // ── Navigation ──────────────────────────────────────────────────────────
    // Maps RadioButton x:Name → page tag string
    private static readonly Dictionary<string, string> _navNames = new()
    {
        { nameof(NavTest),    "test" },
        { nameof(NavIperf3),  "iperf3" },
        { nameof(NavEthr),    "ethr" },
        { nameof(NavWebsite), "website" },
        { nameof(NavMonitor), "monitor" },
        { nameof(NavTools),   "tools" },
        { nameof(NavPing),    "pingularity" },
        { nameof(NavHistory), "history" },
        { nameof(NavSettings),"settings" },
    };

    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton btn) return;
        var tag = _navNames.TryGetValue(btn.Name, out var t) ? t : "";
        _currentPage = tag switch
        {
            "test"       => NavPage.Test,
            "website"    => NavPage.Website,
            "monitor"    => NavPage.Monitor,
            "iperf3"     => NavPage.Iperf3,
            "ethr"       => NavPage.Ethr,
            "pingularity"=> NavPage.Pingularity,
            "tools"      => NavPage.Tools,
            "history"    => NavPage.History,
            "settings"   => NavPage.Settings,
            _            => _currentPage
        };
        SetNavActive(tag);
        ShowPage(_currentPage);
    }

    private void SetNavActive(string tag)
    {
        NavTest.IsChecked     = tag == "test";
        NavWebsite.IsChecked  = tag == "website";
        NavMonitor.IsChecked  = tag == "monitor";
        NavTools.IsChecked    = tag == "tools";
        NavIperf3.IsChecked   = tag == "iperf3";
        NavEthr.IsChecked     = tag == "ethr";
        NavPing.IsChecked     = tag == "pingularity";
        NavHistory.IsChecked  = tag == "history";
        NavSettings.IsChecked = tag == "settings";
    }

    private void ShowPage(NavPage page)
    {
        PanelTest.Visibility         = page == NavPage.Test          ? Visibility.Visible : Visibility.Collapsed;
        PanelWebsite.Visibility      = page == NavPage.Website       ? Visibility.Visible : Visibility.Collapsed;
        PanelMonitor.Visibility      = page == NavPage.Monitor       ? Visibility.Visible : Visibility.Collapsed;
        PanelTools.Visibility        = page == NavPage.Tools         ? Visibility.Visible : Visibility.Collapsed;
        PanelIperf3.Visibility       = page == NavPage.Iperf3        ? Visibility.Visible : Visibility.Collapsed;
        PanelEthr.Visibility         = page == NavPage.Ethr          ? Visibility.Visible : Visibility.Collapsed;
        PanelPingularity.Visibility  = page == NavPage.Pingularity   ? Visibility.Visible : Visibility.Collapsed;
        PanelHistory.Visibility      = page == NavPage.History       ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility     = page == NavPage.Settings      ? Visibility.Visible : Visibility.Collapsed;
        // The log + summary row only belongs to the broadband test page
        BottomRow.Visibility         = page == NavPage.Test          ? Visibility.Visible : Visibility.Collapsed;
        if (page == NavPage.History) ApplyHistoryFilter();
        if (page == NavPage.Monitor) DrawMonitorChart();
        HeaderTitle.Text = page switch
        {
            NavPage.Website    => Loc.Get("title.website"),
            NavPage.Monitor    => Loc.Get("title.monitor"),
            NavPage.Tools      => Loc.Get("title.tools"),
            NavPage.Iperf3     => Loc.Get("title.iperf3"),
            NavPage.Ethr       => Loc.Get("title.ethr"),
            NavPage.Pingularity=> Loc.Get("title.ping"),
            NavPage.History    => Loc.Get("title.history"),
            NavPage.Settings   => Loc.Get("title.settings"),
            _                  => Loc.Get("title.test")
        };
    }

    private void EngineChanged(object? sender, RoutedEventArgs e)
        => _engine = EngineLibreSpeed.IsChecked == true ? EngineType.LibreSpeed : EngineType.SpeedTestGo;

    // ── Title bar buttons ──────────────────────────────────────────────────
    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object? sender, RoutedEventArgs e)  => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object? sender, RoutedEventArgs e)     => Close();

    // ── Gauge Drawing ───────────────────────────────────────────────────────
    private void DrawGauge(double mbps)
    {
        GaugeCanvas.Children.Clear();
        double w = Math.Max(GaugeCanvas.ActualWidth, 200);
        double h = Math.Max(GaugeCanvas.ActualHeight, 200);
        int cx = (int)(w / 2), cy = (int)(h / 2) + 8;
        float r = (float)(w / 2 - 36), thick = 14f;
        float startA = 135f, sweepA = 270f;

        // Background track
        AddArc(GaugeCanvas, cx, cy, r, thick + 6, startA, sweepA, "#1A1A2E");
        // Track fill
        AddArc(GaugeCanvas, cx, cy, r, thick, startA, sweepA, "#252540");

        double ratio = Math.Max(0, Math.Min(1.0, mbps / 600.0));
        float valSweep = sweepA * (float)ratio;
        if (valSweep > 0.5f)
        {
            // Glow
            AddArc(GaugeCanvas, cx, cy, r, thick + 10, startA, valSweep, "#00D4FF0A");
            // Inner accent
            AddArc(GaugeCanvas, cx, cy, r, thick + 4, startA, valSweep, "#00D4FF1C");
            // Main arc
            string color = ratio < 0.25 ? "#4ADE80"
                         : ratio < 0.55 ? "#62CDFF"
                         : ratio < 0.80 ? "#FBBF24"
                                        : "#F87171";
            AddArc(GaugeCanvas, cx, cy, r, thick, startA, valSweep, color);
        }

        // Tick marks
        for (int i = 0; i <= 12; i++)
        {
            float a = startA + (sweepA * i / 12.0f);
            double rad = (a - 90) * Math.PI / 180;
            bool major = i % 3 == 0;
            double tickLen = major ? 14 : 7;
            double innerR = r - 24;
            double outerR = r - 24 - tickLen;
            double x1 = cx + innerR * Math.Cos(rad);
            double y1 = cy + innerR * Math.Sin(rad);
            double x2 = cx + outerR * Math.Cos(rad);
            double y2 = cy + outerR * Math.Sin(rad);
            GaugeCanvas.Children.Add(new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = major ? (Brush)new BrushConverter().ConvertFrom("#4A4A6A")!
                               : (Brush)new BrushConverter().ConvertFrom("#2A2A40")!,
                StrokeThickness = major ? 2 : 1
            });
            if (major)
            {
                double lr = r - 42;
                double lx = cx + lr * Math.Cos(rad);
                double ly = cy + lr * Math.Sin(rad);
                var lbl = new TextBlock
                {
                    Text = (i * 50).ToString(), FontSize = 9,
                    Foreground = (Brush)new BrushConverter().ConvertFrom("#3A3A58")!
                };
                Canvas.SetLeft(lbl, lx - 12);
                Canvas.SetTop(lbl, ly - 7);
                GaugeCanvas.Children.Add(lbl);
            }
        }
        GaugeValue.Text = mbps.ToString("F2");
    }

    private static void AddArc(Canvas canvas, int cx, int cy, float r, float thick, float start, float sweep, string color)
    {
        if (sweep < 0.5f) return;
        double rs = (start - 90) * Math.PI / 180;
        double re = (start + sweep - 90) * Math.PI / 180;
        double x1 = cx + r * (float)Math.Cos(rs);
        double y1 = cy + r * (float)Math.Sin(rs);
        double x2 = cx + r * (float)Math.Cos(re);
        double y2 = cy + r * (float)Math.Sin(re);
        bool large = sweep > 180;
        var geo = new PathGeometry();
        var fig = new PathFigure(new Point(x1, y1), [], large);
        fig.Segments.Add(new ArcSegment(new Point(x2, y2), new Size(r, r), 0, large, SweepDirection.Clockwise, true));
        geo.Figures.Add(fig);
        canvas.Children.Add(new Path { Data = geo, Stroke = (Brush)new BrushConverter().ConvertFrom(color)!, StrokeThickness = thick, Fill = Brushes.Transparent });
    }

    // ── Charts ──────────────────────────────────────────────────────────────
    private void DrawEmptyChart(Canvas canvas, string msg)
    {
        canvas.Children.Clear();
        canvas.Children.Add(new TextBlock
        {
            Text = msg, FontSize = 11,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#353550")!,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
    }

    private void DrawDLChart() => DrawSpeedChart(DLChartCanvas, _dlChartBuf, _chartPoints, "#62CDFF", DLChartPeak);
    private void DrawULChart() => DrawSpeedChart(ULChartCanvas, _ulChartBuf, _chartPoints, "#A78BFA", ULChartPeak);

    private void DrawSpeedChart(Canvas canvas, double[] buf, int count, string colorStr, TextBlock maxLabel)
    {
        canvas.Children.Clear();
        if (count < 1) { DrawEmptyChart(canvas, "Waiting for data…"); return; }
        double w = Math.Max(canvas.ActualWidth, 300);
        double h = Math.Max(canvas.ActualHeight, 110);
        canvas.Width = w; canvas.Height = h;
        double max = buf.Take(count).Max();
        if (max <= 0) max = 1;
        maxLabel.Text = Loc.Get("chart.peak") + ": " + max.ToString("F0") + " " + Loc.Get("unit.mbps");

        // Grid lines
        for (int i = 0; i <= 4; i++)
        {
            double y = h * i / 4;
            canvas.Children.Add(new Line { X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromArgb(14, 35, 40, 65)), StrokeThickness = 1 });
        }
        // Y-axis labels
        for (int i = 0; i <= 4; i++)
        {
            double val = max * (1.0 - i / 4.0);
            double y = h * i / 4;
            var lbl = new TextBlock { Text = val.ToString("F0"), FontSize = 8,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#3A3A58")!,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Canvas.SetLeft(lbl, 3); Canvas.SetTop(lbl, y - 6);
            canvas.Children.Add(lbl);
        }

        // Fill
        var pts = new List<Point>();
        for (int i = 0; i < count; i++)
        {
            double x = count > 1 ? (double)i / (count - 1) * (w - 6) + 3 : w / 2.0;
            double y = h - (buf[i] / max) * (h - 12) - 6;
            pts.Add(new Point(x, y));
        }
        var fillPts = new List<Point> { new(0, h) };
        fillPts.AddRange(pts);
        fillPts.Add(new Point(w, h));
        var fillFig = new PathFigure(fillPts[0], [], false);
        for (int i = 1; i < fillPts.Count; i++) fillFig.Segments.Add(new LineSegment(fillPts[i], true));
        var fillGeo = new PathGeometry(); fillGeo.Figures.Add(fillFig);
        var c = (SolidColorBrush)(Brush)new BrushConverter().ConvertFrom(colorStr)!;
        canvas.Children.Add(new Path { Data = fillGeo,
            Fill = new SolidColorBrush(Color.FromArgb(18, c.Color.R, c.Color.G, c.Color.B)),
            Stroke = Brushes.Transparent });

        // Line
        var lineFig = new PathFigure(pts[0], [], false);
        for (int i = 1; i < pts.Count; i++) lineFig.Segments.Add(new LineSegment(pts[i], true));
        var lineGeo = new PathGeometry(); lineGeo.Figures.Add(lineFig);
        canvas.Children.Add(new Path { Data = lineGeo, Stroke = c, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round });
    }

    // ── Broadband Test ──────────────────────────────────────────────────────
    private async void StartTest_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _isRunning = true;
        SetTestBtnState(false);
        _dlSamples.Clear(); _ulSamples.Clear();
        Array.Clear(_dlChartBuf); Array.Clear(_ulChartBuf);
        _chartPoints = 0;
        _gaugeTarget = 0; _gaugeCurrent = 0;
        SetProgress(0);
        SetLiveStatus("yellow", Loc.Get("status.testing"));
        _cts = new CancellationTokenSource();
        try
        {
            if (_engine == EngineType.SpeedTestGo)
                await RunSpeedTestGoAsync(_cts.Token);
            else
                await RunLibreSpeedAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            AddLog("Error: " + ex.Message);
            SetLiveStatus("red", Loc.Get("status.error"));
        }
        finally
        {
            _isRunning = false;
            SetTestBtnState(true);
            SetProgress(0);
            if (LiveStatusText.Text == Loc.Get("status.testing"))
            {
                SetLiveStatus("green", Loc.Get("status.done"));
                StatusLeft.Text = Loc.Get("status.testcomplete");
            }
        }
    }

    private void SetTestBtnState(bool enabled)
    {
        StartBtn.IsEnabled = enabled;
        StartBtn.Content   = _isRunning ? Loc.Get("btn.stop") : Loc.Get("btn.start");
        StartBtn.Background = (Brush)new BrushConverter().ConvertFrom(_isRunning ? "#F87171" : "#62CDFF");
    }

    /// <summary>Lists available LibreSpeed servers (librespeed-cli --list) into the TEST LOG.</summary>
    private async void ServerList_Click(object? sender, RoutedEventArgs e)
    {
        AddLog("--- LibreSpeed servers (librespeed-cli --list) ---");
        try
        {
            var psi = new ProcessStartInfo(GetExePath("librespeed-cli"), "--list")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var proc = Process.Start(psi)!;
            string output = await proc.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await proc.WaitForExitAsync();
            foreach (var line in output.Split('\n').Where(l => l.Trim().Length > 0).Take(40))
                AddLog("  " + line.TrimEnd());
            AddLog("Tip: fill a server ID above into the Server box to pin a node (empty = auto).");
        }
        catch (Exception ex)
        {
            AddLog("Server list failed: " + ex.Message);
        }
    }

    private void SetLiveStatus(string color, string text)
    {
        LiveDot.Fill = color switch { "green" => (Brush)new BrushConverter().ConvertFrom("#4ADE80"),
                                     "yellow"=> (Brush)new BrushConverter().ConvertFrom("#FBBF24"),
                                     "red"   => (Brush)new BrushConverter().ConvertFrom("#F87171"),
                                     _       => (Brush)new BrushConverter().ConvertFrom("#5A6070") };
        LiveStatusText.Text = text;
    }

    private async Task RunSpeedTestGoAsync(CancellationToken ct)
    {
        AddLog("--- speedtest-go (Ookla servers) ---");
        StatusLeft.Text = Loc.Get("status.finding"); SetProgress(5);
        SetProgressStage("stage.finding");

        // Step 1: find a suitable server (prefer one with working upload)
        string? targetId = await FindServerAsync(ServerInput.Text.Trim(), ct);

        // Step 2: run the test — pass system proxy if present
        StatusLeft.Text = Loc.Get("status.measuring"); SetProgress(15);
        SetProgressStage("stage.ping");
        AddLog("Running test…");
        var proxyArg = GetSystemProxyArg();
        var args = "--json" + (string.IsNullOrEmpty(targetId) ? "" : $" -s {targetId}") + proxyArg;
        var psi = new ProcessStartInfo(GetExePath("speedtest-go"), args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

        using var proc = Process.Start(psi)!;
        var sw = Stopwatch.StartNew();

        // Read streams in parallel with process
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var exitTask = proc.WaitForExitAsync(ct);

        while (!proc.HasExited && sw.Elapsed.TotalSeconds < 55 && !ct.IsCancellationRequested)
        {
            await Task.Delay(350, ct);
            double elapsed = sw.Elapsed.TotalSeconds;
            int pct = elapsed < 8 ? 15 + (int)(elapsed / 8 * 12)
                      : elapsed < 25 ? 27 + (int)((elapsed - 8) / 17 * 38)
                      : 65 + (int)((elapsed - 25) / 20 * 28);
            SetProgress(pct);
            StatusLeft.Text = elapsed < 8 ? Loc.Get("status.ping") : elapsed < 25 ? Loc.Get("status.dl") : Loc.Get("status.ul");
            SetProgressStage(elapsed < 8 ? "stage.ping" : elapsed < 25 ? "stage.download" : "stage.upload",
                $"{pct}% · {elapsed:F1}s");
            AddLog($"  [{elapsed:F1}s] {pct}% — {StatusLeft.Text}");
        }
        if (!proc.HasExited)
        {
            try { proc.Kill(); } catch { }
            await Task.Delay(1000, ct);
        }

        string json = await stdoutTask;
        string stderr = await stderrTask;
        await exitTask;

        if (!string.IsNullOrWhiteSpace(stderr))
            AddLog("[stderr] " + stderr.Trim().Replace("\n", " ").Replace("\r", " "));

        SetProgress(95);
        if (string.IsNullOrWhiteSpace(json)) throw new Exception("No output from speedtest-go (exit " + proc.ExitCode + ")");
        ParseSpeedTestGo(json, targetId);
        SetProgress(100); StatusLeft.Text = Loc.Get("status.done"); AddLog("Done");
        SetProgressStage("stage.done");
    }

    /// <summary>
    /// Find a suitable speedtest-go server by search string.
    /// Runs a quick no-download probe to find servers with working upload.
    /// Prefers the server with highest dl_speed among working-upload candidates.
    /// Falls back to auto-selected server if no match found.
    /// </summary>
    private async Task<string?> FindServerAsync(string query, CancellationToken ct)
    {
        // If no query, try to find a good server by probing nearby countries
        if (string.IsNullOrWhiteSpace(query))
        {
            // Try common nearby cities/countries
            foreach (var candidate in new[] { "Singapore", "Tokyo", "HongKong", "Seoul" })
            {
                var id = await FindBestServerInQueryAsync(candidate, ct);
                if (id != null) return id;
            }
            return null; // let speedtest-go auto-select
        }

        return await FindBestServerInQueryAsync(query, ct);
    }

    /// <summary>
    /// Probe servers matching query using --no-download to check upload health,
    /// then return the best working server's ID.
    /// </summary>
    private async Task<string?> FindBestServerInQueryAsync(string query, CancellationToken ct)
    {
        // Step 1: probe upload health (no download, fast)
        var probePsi = new ProcessStartInfo(GetExePath("speedtest-go"),
            $"--search \"{query}\" --json --no-download" + GetSystemProxyArg())
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        string json;
        try
        {
            using (var proc = Process.Start(probePsi)!)
            {
                json = await proc.StandardOutput.ReadToEndAsync();
                await proc.WaitForExitAsync(ct);
            }
        }
        catch { return null; }

        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var servers = doc.RootElement.GetProperty("servers");
            if (servers.ValueKind != JsonValueKind.Array || servers.GetArrayLength() == 0)
                return null;

            // Find best server with working upload (ul_speed > 0 means upload test ran successfully)
            string? bestId = null;
            double bestUl = -1;
            foreach (var srv in servers.EnumerateArray())
            {
                if (srv.TryGetProperty("ul_speed", out var u) && u.TryGetDouble(out var uv))
                {
                    if (uv > 0 && uv > bestUl)
                    {
                        bestUl = uv;
                        bestId = srv.GetProperty("id").GetString();
                    }
                }
            }
            // Fallback: any server with ul_speed >= 0 (upload not failed)
            if (bestId == null)
            {
                foreach (var srv in servers.EnumerateArray())
                {
                    if (srv.TryGetProperty("ul_speed", out var u2) && u2.TryGetDouble(out var uv2) && uv2 >= 0)
                    {
                        bestId = srv.GetProperty("id").GetString();
                        break;
                    }
                }
            }
            return bestId;
        }
        catch { return null; }
    }

    private async Task RunLibreSpeedAsync(CancellationToken ct)
    {
        AddLog("--- LibreSpeed CLI ---");
        StatusLeft.Text = Loc.Get("status.testing"); SetProgress(10);
        SetProgressStage("stage.ping");
        int dur = (int)SettingDuration.Value;
        int totalTimeoutSec = Math.Max(dur + 45, 75);
        // Server selection: LibreSpeed 节点 ID/URL（空 = 自动选择）；speedtest-go 用作搜索关键字
        string serverArg = "";
        string serverSel = ServerInput.Text.Trim();
        if (serverSel.Length > 0) serverArg = $" --server={serverSel}";
        var psi = new ProcessStartInfo(GetExePath("librespeed-cli"),
            $"--json --duration={dur} --concurrent={(int)SettingThreads.Value}{serverArg}")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // Inject system proxy into child process environment
        var proxyArg = GetSystemProxyArg();
        if (!string.IsNullOrEmpty(proxyArg))
        {
            var proxyUrl = proxyArg.Replace(" --proxy=", "");
            psi.Environment["HTTP_PROXY"] = proxyUrl;
            psi.Environment["HTTPS_PROXY"] = proxyUrl;
            AddLog("⚠ Proxy detected but LibreSpeed CLI ignores proxy env — test uses direct connection.");
        }

        using var proc = Process.Start(psi)!;
        var sw = Stopwatch.StartNew();

        // Read streams concurrently with process — never blocks the UI
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // Progress polling — pure time check, no token, safe on UI thread
        var progressTask = PollProgress(sw, dur, totalTimeoutSec);

        // Simple polling loop: wait for process exit OR timeout
        while (!proc.HasExited)
        {
            if (sw.Elapsed.TotalSeconds >= totalTimeoutSec)
            {
                try { proc.Kill(); } catch { }
                await Task.Delay(1500, ct);
                AddLog($"  TIMEOUT after {sw.Elapsed.TotalSeconds:F0}s, killed");
                break;
            }
            await Task.Delay(500, ct);
        }

        string json = await stdoutTask;
        string stderr = await stderrTask;
        await progressTask;

        if (!string.IsNullOrWhiteSpace(stderr))
            AddLog("[stderr] " + stderr.Trim().Replace("\n", " ").Replace("\r", " "));

        SetProgress(95);

        if (proc.ExitCode != 0)
            throw new Exception($"librespeed-cli failed (exit {proc.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? "no output" : stderr.Trim())}");
        if (string.IsNullOrWhiteSpace(json))
            throw new Exception("librespeed-cli produced no output");

        await ParseLibreSpeedAsync(json);
        SetProgress(100); StatusLeft.Text = Loc.Get("status.done"); AddLog("Done");
        SetProgressStage("stage.done");
    }

    // ── Parsing (BUG-FIXED) ─────────────────────────────────────────────────

    /// speedtest-go JSON:
    ///   dl_speed  = 0.01 Mbps units (dl_speed / 100000 = Mbps)
    ///   ul_speed  = same (0.01 Mbps units, -1 when upload failed)
    ///   latency   = nanoseconds
    ///   jitter    = nanoseconds
    /// Guard:    clamp negative values to 0
    private void ParseSpeedTestGo(string json, string? serverId)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string ip = "--", isp = "--", country = "--";
        if (root.TryGetProperty("user_info", out var ui))
        {
            ip      = ui.TryGetProperty("IP",       out var i) ? (i.GetString() ?? "--") : "--";
            isp     = ui.TryGetProperty("Isp",      out var isEl) ? (isEl.GetString() ?? "--") : "--";
            country = ui.TryGetProperty("Country",  out var co) ? (co.GetString() ?? "--") : "--";
        }
        double dl = 0, ul = 0, ping = 0, jitter = 0;
        if (root.TryGetProperty("servers", out var servers) &&
            servers.ValueKind == JsonValueKind.Array && servers.GetArrayLength() > 0)
        {
            var srv = servers[0];
            string srvName = srv.TryGetProperty("name", out var n) ? (n.GetString() ?? "?") : "?";

            // BUG FIX #1: ul_speed can be -1 when upload fails; clamp to 0
            double dlKbps = srv.TryGetProperty("dl_speed", out var d) && d.TryGetDouble(out var dv) ? dv : 0;
            double ulKbps = 0;
            _ulFailed = false;
            if (srv.TryGetProperty("ul_speed", out var u) && u.TryGetDouble(out var uv))
            {
                ulKbps = uv;
                _ulFailed = uv < 0;        // track upload failure before clamping
            }
            dlKbps = Math.Max(0, dlKbps);   // never report negative
            ulKbps = Math.Max(0, ulKbps);

            // FIX: dl_speed/ul_speed are in units of 0.01 Mbps (1/100000 of Mbps)
            //   dl_speed / 100000 = Mbps
            dl = dlKbps / 100000.0;
            ul = ulKbps / 100000.0;

            // nanoseconds → milliseconds
            double pingNs = srv.TryGetProperty("latency",  out var l) && l.TryGetDouble(out var lv) ? lv : 0;
            double jitterNs = srv.TryGetProperty("jitter", out var j) && j.TryGetDouble(out var jv) ? jv : 0;
            ping  = Math.Max(0, pingNs  / 1_000_000.0);
            jitter = Math.Max(0, jitterNs / 1_000_000.0);

            _dlSamples.Add(dl); _ulSamples.Add(ul);
            _dlChartBuf[_chartPoints % ChartMaxPoints] = dl;
            _ulChartBuf[_chartPoints % ChartMaxPoints] = ul;
            _chartPoints++;
            UpdateResultUI(dl, ul, ping, jitter, ip, isp, country, srvName);
        }
        AddLog($"  DL:{dl:F2} UL:{(_ulFailed ? "N/A" : ul.ToString("F2"))} Ping:{ping:F1}ms Jitter:{jitter:F2}ms");
        SaveHistory("speedtest-go", dl, ul, ping, jitter, ip, isp, serverId ?? "-", GetQualityLabel(CalculateScore(dl, ul, ping, jitter)));
    }

    /// librespeed-cli JSON: output is a JSON array [{...}]; download/upload are already in Mbps
    private async Task ParseLibreSpeedAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // Handle JSON array format: [{"download":...,"upload":...}, ...]
        JsonElement elem;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            elem = root[0];
        else
            elem = root;
        double dl = elem.TryGetProperty("download", out var d) ? d.GetDouble() : 0;
        double ul = elem.TryGetProperty("upload",   out var u) ? u.GetDouble() : 0;
        dl = Math.Max(0, dl); ul = Math.Max(0, ul); // safety clamp
        double ping = 0, jitter = 0;
        // ping: may be a plain number or an object with .average
        if (elem.TryGetProperty("ping", out var p))
            ping = Math.Max(0, p.ValueKind == JsonValueKind.Number ? p.GetDouble()
                : (p.TryGetProperty("average", out var a) ? a.GetDouble() : 0));
        if (elem.TryGetProperty("jitter", out var j))
            jitter = Math.Max(0, j.GetDouble());

        string iStr = elem.TryGetProperty("client", out var cli) &&
                      cli.TryGetProperty("ip", out var iP) && !string.IsNullOrWhiteSpace(iP.GetString()) ? (iP.GetString() ?? "--") :
                      (elem.TryGetProperty("ip", out var iP2) ? (iP2.GetString() ?? "--") : "--");
        string isStr = elem.TryGetProperty("client", out var cli2) &&
                       cli2.TryGetProperty("org", out var iS) && !string.IsNullOrWhiteSpace(iS.GetString()) ? (iS.GetString() ?? "--") : "--";
        string coStr = elem.TryGetProperty("client", out var cli3) &&
                       cli3.TryGetProperty("country", out var c) && !string.IsNullOrWhiteSpace(c.GetString()) ? (c.GetString() ?? "--") : "--";
        // Fallback: try to get ISP/country from the server's sponsor field if available
        // Note: do NOT fall back to server name as ISP — that's misleading
        if (isStr == "--" && elem.TryGetProperty("server", out var srv))
        {
            // sponsor might contain ISP-like info, but don't use server name
            if (srv.TryGetProperty("sponsor", out var sp) && !string.IsNullOrWhiteSpace(sp.GetString()))
                isStr = sp.GetString()!;
        }
        // Enrich with IP geolocation if ISP or country is still missing
        if (isStr == "--" || coStr == "--")
        {
            var geo = await LookupIpGeolocationAsync(iStr);
            if (isStr == "--" && !string.IsNullOrWhiteSpace(geo.isp))
                isStr = geo.isp;
            if (coStr == "--" && !string.IsNullOrWhiteSpace(geo.country))
                coStr = geo.country;
        }

        _dlSamples.Add(dl); _ulSamples.Add(ul);
        _dlChartBuf[_chartPoints % ChartMaxPoints] = dl;
        _ulChartBuf[_chartPoints % ChartMaxPoints] = ul;
        _chartPoints++;
        UpdateResultUI(dl, ul, ping, jitter, iStr, isStr, coStr, "LibreSpeed");
        AddLog($"  DL:{dl:F2} UL:{ul:F2} Ping:{ping:F1}ms Jitter:{jitter:F2}ms");
        SaveHistory("librespeed", dl, ul, ping, jitter, iStr, isStr, "LibreSpeed", GetQualityLabel(CalculateScore(dl, ul, ping, jitter)));
    }

    private void UpdateResultUI(double dl, double ul, double ping, double jitter,
        string ip, string isp, string country, string server)
    {
        _gaugeTarget = dl;
        MetricDL.Text      = dl.ToString("F2");
        MetricUL.Text      = _ulFailed ? "N/A" : ul.ToString("F2");
        MetricPing.Text    = ping.ToString("F1");
        MetricJitter.Text  = jitter.ToString("F2");
        InfoIP.Text     = ip;
        InfoISP.Text    = isp;
        InfoCountry.Text = country;
        InfoServer.Text = server;
        LiveIpText.Text    = "IP: " + ip;
        LiveIspText.Text   = "ISP: " + isp;
        QBarDL.Width     = Math.Min(160, dl / 600.0 * 160);
        QBarUL.Width     = Math.Min(160, ul / 300.0 * 160);
        QBarPing.Width   = Math.Min(160, Math.Max(0, 1 - ping / 200.0) * 160);
        QBarJitter.Width = Math.Min(160, Math.Max(0, 1 - jitter / 50.0) * 160);
        DLBar.Width     = Math.Min(160, dl / 600.0 * 160);
        ULBar.Width     = _ulFailed ? 0 : Math.Min(160, ul / 300.0 * 160);
        double score = CalculateScore(dl, ul, ping, jitter);
        string label = GetQualityLabel(score);
        string color = score >= 80 ? "#4ADE80" : score >= 60 ? "#62CDFF" : score >= 40 ? "#FBBF24" : "#F87171";
        ScoreRingText.Text = score.ToString("F0");
        ScoreRingBorder.BorderBrush = (Brush)new BrushConverter().ConvertFrom(color)!;
        ScoreRingText.Foreground = (Brush)new BrushConverter().ConvertFrom(color)!;
        QualityLabel.Text = label;
        QualityLabel.Foreground = (Brush)new BrushConverter().ConvertFrom(color)!;
        SummaryDL.Text      = dl.ToString("F2") + " Mbps";
        SummaryUL.Text      = _ulFailed ? "N/A" : ul.ToString("F2") + " Mbps";
        SummaryPing.Text    = ping.ToString("F1") + " ms";
        SummaryJitter.Text  = jitter.ToString("F2") + " ms";
    }

    private static double CalculateScore(double dl, double ul, double ping, double jitter)
    {
        double score = 0, count = 0;
        if (dl > 0)  { score += Math.Min(40, dl / 100.0 * 40); count++; }
        if (ul > 0)  { score += Math.Min(20, ul / 50.0 * 20);  count++; }
        if (ping > 0 && ping < 500) { score += Math.Max(0, 25 * (1 - ping / 200.0)); count++; }
        if (jitter > 0 && jitter < 50) { score += Math.Max(0, 15 * (1 - jitter / 25.0)); count++; }
        return count > 0 ? score / count : 0;
    }

    private static string GetQualityLabel(double s) => s switch
    {
        >= 80 => Loc.Get("quality.outstanding"),
        >= 60 => Loc.Get("quality.excellent"),
        >= 40 => Loc.Get("quality.good"),
        >= 20 => Loc.Get("quality.fair"),
        _      => Loc.Get("quality.poor")
    };

    private void SaveHistory(string engine, double dl, double ul, double ping, double jitter, string ip, string isp, string server, string quality)
    {
        _history.Insert(0, new HistoryEntry(DateTime.Now.ToString("MM-dd HH:mm:ss"), engine, dl, ul, ping, jitter, ip, isp, server, quality));
        PersistHistory();
        if (HistoryGrid != null) ApplyHistoryFilter();
    }

    // ── History persistence (rolling JSON in %APPDATA%\SpeedPro\history.json) ──
    private const int HistoryMaxEntries = 500;

    private string HistoryPath => global::System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpeedPro", "history.json");

    private void LoadHistory()
    {
        try
        {
            if (!global::System.IO.File.Exists(HistoryPath)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<HistoryEntry>>(
                global::System.IO.File.ReadAllText(HistoryPath));
            if (list == null) return;
            foreach (var h in list.Take(HistoryMaxEntries)) _history.Add(h);
        }
        catch { }
    }

    private void PersistHistory()
    {
        try
        {
            while (_history.Count > HistoryMaxEntries) _history.RemoveAt(_history.Count - 1);
            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(HistoryPath)!);
            global::System.IO.File.WriteAllText(HistoryPath,
                System.Text.Json.JsonSerializer.Serialize(_history.ToList()));
        }
        catch { }
    }

    /// <summary>Refreshes the history grid + trend chart for the current engine filter.</summary>
    private void ApplyHistoryFilter()
    {
        if (HistoryGrid == null || HistoryChartCanvas == null) return;
        string filter = (HistoryFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All Engines";
        IEnumerable<HistoryEntry> filtered = filter == "All Engines"
            ? _history
            : _history.Where(h => h.Engine == filter);
        HistoryGrid.ItemsSource = new ObservableCollection<HistoryEntry>(filtered);
        DrawHistoryChart(filtered);
    }

    private void DrawHistoryChart(IEnumerable<HistoryEntry> source)
    {
        var canvas = HistoryChartCanvas;
        canvas.Children.Clear();
        var items = source.Reverse().TakeLast(120).ToList();   // chronological, newest 120
        HistoryTrendTitle.Text = $"TREND · {_history.Count} tests · showing {items.Count}";
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;   // not laid out yet — SizeChanged will redraw
        if (items.Count < 2)
        {
            var tb = new TextBlock
            {
                Text = "Run at least 2 tests to see the trend",
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#353550")!
            };
            canvas.Children.Add(tb);
            tb.Measure(new Size(w, h));
            Canvas.SetLeft(tb, Math.Max(0, (w - tb.ActualWidth) / 2));
            Canvas.SetTop(tb, Math.Max(0, (h - tb.ActualHeight) / 2));
            return;
        }
        double maxSpeed = Math.Max(items.Max(x => x.Download), items.Max(x => x.Upload));
        if (maxSpeed <= 0) maxSpeed = 1;
        double maxPing = Math.Max(items.Max(x => x.Ping), 1);

        for (int i = 0; i <= 4; i++)
            canvas.Children.Add(new Line
            {
                X1 = 0, Y1 = h * i / 4, X2 = w, Y2 = h * i / 4,
                Stroke = new SolidColorBrush(Color.FromArgb(20, 120, 130, 160)), StrokeThickness = 1
            });

        Point Plot(int i, double v, double max) =>
            new(3 + i * (w - 6) / (items.Count - 1), h - 8 - (v / max) * (h - 20));

        AddPolyline(canvas, items.Select((x, i) => Plot(i, x.Download, maxSpeed)).ToList(), "#62CDFF", 2);
        AddPolyline(canvas, items.Select((x, i) => Plot(i, x.Upload, maxSpeed)).ToList(), "#A78BFA", 2);
        AddPolyline(canvas, items.Select((x, i) => Plot(i, x.Ping, maxPing)).ToList(), "#FFD740", 1.5);

        canvas.Children.Add(new TextBlock
        {
            Text = maxSpeed.ToString("F0") + " Mbps", FontSize = 9,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#565C74")!
        });
        var pingLbl = new TextBlock
        {
            Text = maxPing.ToString("F0") + " ms", FontSize = 9,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#565C74")!
        };
        canvas.Children.Add(pingLbl);
        pingLbl.Measure(new Size(w, h));
        Canvas.SetLeft(pingLbl, w - pingLbl.ActualWidth - 6);
        Canvas.SetTop(pingLbl, 4);
    }

    private static void AddPolyline(Canvas canvas, List<Point> pts, string color, double thick)
    {
        if (pts.Count < 2) return;
        var fig = new PathFigure(pts[0], [], false);
        for (int i = 1; i < pts.Count; i++) fig.Segments.Add(new LineSegment(pts[i], true));
        var geo = new PathGeometry(); geo.Figures.Add(fig);
        canvas.Children.Add(new Path
        {
            Data = geo,
            Stroke = (Brush)new BrushConverter().ConvertFrom(color)!,
            StrokeThickness = thick,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void SetProgress(int pct)
    {
        pct = Math.Clamp(pct, 0, 100);

        // Progress bar inside the TEST LOG header
        double cw = ProgressCardOuter.ActualWidth - 2;
        if (cw <= 0) cw = 240;
        ProgressCardFill.Width = Math.Max(0, cw * pct / 100.0);
        if (ProgressPct != null) ProgressPct.Text = pct + "%";
    }

    /// <summary>Updates the progress card stage label (localized).</summary>
    private void SetProgressStage(string key, string? detail = null)
    {
        if (ProgressStage != null) ProgressStage.Text = Loc.Get(key);
        if (ProgressDetail != null) ProgressDetail.Text = detail ?? "";
    }

    // ── Config (proxy setting persists to %APPDATA%\SpeedPro\config.json) ──
    private bool _loadingConfig;

    private string ConfigPath => global::System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpeedPro", "config.json");

    private void LoadConfig()
    {
        try
        {
            if (global::System.IO.File.Exists(ConfigPath))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(global::System.IO.File.ReadAllText(ConfigPath));
                _loadingConfig = true;
                if (doc.RootElement.TryGetProperty("proxy", out var p))
                    SettingProxy.Text = p.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("tray", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.True)
                    SetTrayCheck.IsChecked = true;
                if (doc.RootElement.TryGetProperty("autoTheme", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.True)
                    SetAutoThemeCheck.IsChecked = true;
                // 恢复窗口位置/尺寸（构造阶段窗口尚未显示，安全）
                if (doc.RootElement.TryGetProperty("winW", out var wE) &&
                    doc.RootElement.TryGetProperty("winH", out var hE) &&
                    doc.RootElement.TryGetProperty("winL", out var lE) &&
                    doc.RootElement.TryGetProperty("winT", out var tE))
                {
                    _ = double.TryParse(wE.ToString(), out _winW);
                    _ = double.TryParse(hE.ToString(), out _winH);
                    _ = double.TryParse(lE.ToString(), out _winL);
                    _ = double.TryParse(tE.ToString(), out _winT);
                    RestoreWindowBounds();
                }
                _loadingConfig = false;
                if (SetAutoThemeCheck.IsChecked == true) ApplySystemTheme();
            }
            // 开机自启的真相来源是注册表
            SetAutoStartCheck.IsChecked = global::Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")
                ?.GetValue("SpeedPro") != null;
            UpdateProxyState();
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            RememberWindowBounds();
            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(ConfigPath)!);
            string val = SettingProxy.Text.Trim().Replace("\\", "\\\\").Replace("\"", "\\\"");
            string tray = SetTrayCheck.IsChecked == true ? "true" : "false";
            string autoTheme = SetAutoThemeCheck.IsChecked == true ? "true" : "false";
            global::System.IO.File.WriteAllText(ConfigPath,
                "{\"proxy\":\"" + val + "\",\"tray\":" + tray + ",\"autoTheme\":" + autoTheme +
                ",\"winL\":" + _winL.ToString("F0") + ",\"winT\":" + _winT.ToString("F0") +
                ",\"winW\":" + _winW.ToString("F0") + ",\"winH\":" + _winH.ToString("F0") + "}");
        }
        catch { }
    }

    private void TrayCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingConfig) return;
        SaveConfig();
    }

    private void AutoStart_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingConfig) return;
        try
        {
            using var key = global::Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (SetAutoStartCheck.IsChecked == true)
            {
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exe.Length > 0) key.SetValue("SpeedPro", "\"" + exe + "\"");
            }
            else key.DeleteValue("SpeedPro", false);
        }
        catch (Exception ex) { AddLog("Autostart update failed: " + ex.Message); }
    }

    private void UpdateProxyState()
    {
        if (SettingProxyState == null) return;
        bool has = SettingProxy.Text.Trim().Length > 0;
        SettingProxyState.Text = Loc.Get(has ? "set.proxy.state.on" : "set.proxy.state.off");
        SettingProxyState.Foreground = has
            ? (Brush)new BrushConverter().ConvertFrom("#4ADE80")!
            : (Brush)new BrushConverter().ConvertFrom("#6F7686")!;
    }

    private void SettingProxy_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingConfig) return;
        UpdateProxyState();
        SaveConfig();
    }

    // ── Iperf3 ──────────────────────────────────────────────────────────────
    private void Iperf3Server_Click(object? sender, RoutedEventArgs e)
    {
        if (_iperf3Server != null && !_iperf3Server.HasExited)
        {
            _iperf3Server.Kill(); _iperf3Server = null;
            Iperf3ServerStatus.Text = "Server: stopped";
            Iperf3ServerStatus.Foreground = (Brush)new BrushConverter().ConvertFrom("#6F7686")!;
            Iperf3ServerBtn.Content = "Start Server";
            Iperf3ServerBtn.Background = (Brush)new BrushConverter().ConvertFrom("#62CDFF");
            AddLog("Iperf3 server stopped");
            return;
        }
        _iperf3ServerPort = int.TryParse(Iperf3PortInput.Text, out var p) ? p : 5201;
        var psi = new ProcessStartInfo(GetExePath("iperf3.exe"), "-s -p " + _iperf3ServerPort)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        _iperf3Server = Process.Start(psi);
        _iperf3Server.OutputDataReceived += (s, ev) =>
        {
            if (ev.Data != null) Dispatcher.Invoke(() =>
            {
                Iperf3LogBox.AppendText(ev.Data + "\n");
                AddLog("[iperf3-srv] " + ev.Data);
            });
        };
        _iperf3Server.ErrorDataReceived += (s, ev) =>
        {
            if (ev.Data != null) Dispatcher.Invoke(() => Iperf3LogBox.AppendText("[ERR] " + ev.Data + "\n"));
        };
        _iperf3Server.BeginOutputReadLine();
        _iperf3Server.BeginErrorReadLine();
        Iperf3ServerStatus.Text = "Running on port " + _iperf3ServerPort;
        Iperf3ServerStatus.Foreground = (Brush)new BrushConverter().ConvertFrom("#4ADE80")!;
        Iperf3ServerBtn.Content = "Stop Server";
        Iperf3ServerBtn.Background = (Brush)new BrushConverter().ConvertFrom("#F87171");
        AddLog("Iperf3 server started on port " + _iperf3ServerPort);
    }

    private async void Iperf3Run_Click(object? sender, RoutedEventArgs e)
    {
        if (_iperf3Testing) return;
        string host = Iperf3HostClient.Text.Trim();
        if (host.Length == 0) { AddLog("Iperf3: please enter a target Host."); return; }
        if (!int.TryParse(Iperf3PortClient.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            AddLog("Iperf3: invalid Port (must be 1-65535).");
            return;
        }
        int threads = int.TryParse(Iperf3Threads.Text, out var t) ? Math.Max(1, t) : 1;
        // BUG FIX: use the NEW textbox name (Iperf3Duration2) not the removed one
        int duration = int.TryParse(Iperf3Duration2.Text, out var d) ? Math.Max(1, d) : 5;
        bool udp = Iperf3Proto.SelectedIndex == 1;
        int bufsize = int.TryParse(Iperf3BufSize.Text, out var b) ? Math.Max(1, b) : 128;

        // Client mode connects to host:port directly — a local server is NOT required.
        // (Start the local server only when you want OTHER machines to test against this PC.)
        _iperf3Testing = true;
        Iperf3RunBtn.IsEnabled = false;
        StatusLeft.Text = Loc.Get("status.iperf");
        SetProgress(10);
        try
        {
            AddLog($"--- Iperf3 {(udp ? "UDP" : "TCP")} {host}:{port} streams={threads} dur={duration}s bufsz={bufsize}KB ---");

            var args = new StringBuilder();
            args.Append("-c ").Append(host).Append(" -p ").Append(port).Append(" -t ").Append(duration);
            args.Append(" -P ").Append(threads).Append(" --json");
            if (udp) args.Append(" -u");
            args.Append(" -l ").Append(bufsize);

            var psi = new ProcessStartInfo(GetExePath("iperf3.exe"), args.ToString())
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

            using var proc = Process.Start(psi)!;
            var sw = Stopwatch.StartNew();
            // Drain stdout/stderr WHILE waiting — iperf3's JSON output can fill the pipe
            // buffer and block the client from exiting until we read it.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            while (!proc.HasExited && sw.Elapsed.TotalSeconds < duration + 10) await Task.Delay(300);
            string json = await stdoutTask;
            string errors = await stderrTask;
            await proc.WaitForExitAsync();
            Iperf3LogBox.AppendText(json);
            if (!string.IsNullOrWhiteSpace(errors)) Iperf3LogBox.AppendText("[ERR] " + errors);
            SetProgress(100);
            double throughput = ParseIperf3Json(json);
            // 点对点吞吐也入历史（历史页过滤器本就有 iperf3 项）
            SaveHistory("iperf3", throughput, 0, 0, 0, host, udp ? "UDP" : "TCP",
                host + ":" + port, GetQualityLabel(CalculateScore(throughput, 0, 0, 0)));
            StatusLeft.Text = Loc.Get("status.iperfdone");
            AddLog("Iperf3 test finished");
        }
        catch (Exception ex)
        {
            AddLog("Iperf3 error: " + ex.Message);
            SetProgress(0);
        }
        finally
        {
            _iperf3Testing = false;
            Iperf3RunBtn.IsEnabled = true;
        }
    }

    private double ParseIperf3Json(string json)
    {
        double throughputMbps = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var end    = doc.RootElement.GetProperty("end");
            // TCP reports sum_sent/sum_received; UDP reports only "sum" — fall back so UDP parses too
            if (!end.TryGetProperty("sum_sent", out var sumSent))
                end.TryGetProperty("sum", out sumSent);
            double throughputBps = sumSent.TryGetProperty("bits_per_second", out var t) ? t.GetDouble() : 0;
            throughputMbps = Math.Max(0, throughputBps / 1e6);
            double durationSec   = sumSent.TryGetProperty("seconds", out var dur) ? dur.GetDouble() : 0;
            long   totalBytes    = sumSent.TryGetProperty("bytes",    out var bytes) ? bytes.GetInt64() : 0;
            var cpu  = end.GetProperty("cpu_utilization_percent");
            double hostTotal = cpu.TryGetProperty("host_total", out var ht) ? ht.GetDouble() : 0;

            Iperf3Throughput.Text = throughputMbps > 0 ? throughputMbps.ToString("F1") : "--";
            Iperf3Throughput.Foreground = (Brush)new BrushConverter().ConvertFrom(
                throughputMbps > 1000 ? "#4ADE80" : throughputMbps > 100 ? "#62CDFF" : "#FBBF24");
            Iperf3ResultDuration.Text = durationSec > 0 ? durationSec.ToString("F1") : "--";
            Iperf3Bytes.Text   = totalBytes > 0 ? (totalBytes / 1_000_000).ToString("F1") + " MB" : "--";
            Iperf3Cpu.Text     = hostTotal > 0 ? hostTotal.ToString("F1") + "%" : "--";

            var start = doc.RootElement.GetProperty("start");
            var ts = start.GetProperty("test_start");
            string proto = ts.TryGetProperty("protocol", out var p) ? (p.GetString() ?? "TCP") : "TCP";
            int streams  = ts.TryGetProperty("num_streams", out var ns) ? ns.GetInt32() : 1;
            Iperf3ProtoLabel.Text = proto;
            Iperf3StreamsLabel.Text = streams.ToString();

            AddLog($"  Throughput: {throughputMbps:F1} Mbps | {streams} streams | CPU: {hostTotal:F1}%");
        }
        catch (Exception ex)
        {
            AddLog("Parse error: " + ex.Message);
            Iperf3Throughput.Text = "--";
            Iperf3ResultDuration.Text = "--";
            Iperf3Bytes.Text = "--";
            Iperf3Cpu.Text = "--";
        }
        return throughputMbps;
    }

    private void Iperf3ClearLog_Click(object? sender, RoutedEventArgs e) => Iperf3LogBox.Text = "";

    // ── Ethr ────────────────────────────────────────────────────────────────
    private void EthrServer_Click(object? sender, RoutedEventArgs e)
    {
        if (_ethrServer != null && !_ethrServer.HasExited)
        {
            _ethrServer.Kill(); _ethrServer = null;
            EthrServerStatus.Text = "Server: stopped";
            EthrServerStatus.Foreground = (Brush)new BrushConverter().ConvertFrom("#6F7686")!;
            EthrServerBtn.Content = "Start Server";
            EthrServerBtn.Background = (Brush)new BrushConverter().ConvertFrom("#62CDFF");
            AddLog("Ethr server stopped");
            return;
        }
        _ethrServerPort = int.TryParse(EthrPortInput.Text, out var p) ? p : 8888;
        var psi = new ProcessStartInfo(GetExePath("ethr.exe"), "-s -port " + _ethrServerPort + " -no")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        _ethrServer = Process.Start(psi);
        _ethrServer.OutputDataReceived += (s, ev) =>
        {
            if (ev.Data != null) Dispatcher.Invoke(() => { EthrLogBox.AppendText(ev.Data + "\n"); AddLog("[srv] " + ev.Data); });
        };
        _ethrServer.ErrorDataReceived += (s, ev) =>
        {
            if (ev.Data != null) Dispatcher.Invoke(() => EthrLogBox.AppendText("[ERR] " + ev.Data + "\n"));
        };
        _ethrServer.BeginOutputReadLine();
        _ethrServer.BeginErrorReadLine();
        EthrServerStatus.Text = "Running on port " + _ethrServerPort;
        EthrServerStatus.Foreground = (Brush)new BrushConverter().ConvertFrom("#4ADE80")!;
        EthrServerBtn.Content = "Stop Server";
        EthrServerBtn.Background = (Brush)new BrushConverter().ConvertFrom("#F87171");
        AddLog("Ethr server started on port " + _ethrServerPort);
    }

    private async void EthrRun_Click(object? sender, RoutedEventArgs e)
    {
        if (_ethrTesting) return;
        string host = EthrClientHost.Text.Trim();
        if (host.Length == 0) { AddLog("Ethr: please enter a target Host."); return; }
        if (!int.TryParse(EthrClientPort.Text.Trim(), out var port) || port < 1 || port > 65535)
        {
            AddLog("Ethr: invalid Port (must be 1-65535).");
            return;
        }
        int threads = int.TryParse(EthrThreads.Text, out var t) ? Math.Max(1, t) : 4;
        string durRaw = EthrDuration.Text.Trim().TrimEnd('s');
        if (!int.TryParse(durRaw, out var duration) || duration < 1)
        {
            AddLog("Ethr: invalid Duration (seconds).");
            return;
        }
        bool udp = EthrProtocol.SelectedIndex == 1;

        // Client mode connects to host:port directly — a local server is NOT required.
        _ethrTesting = true;
        EthrRunBtn.IsEnabled = false;
        StatusLeft.Text = Loc.Get("status.ethr");
        SetProgress(10);
        try
        {
            AddLog($"--- Ethr {(udp ? "UDP" : "TCP")} {host}:{port} {threads}t {duration}s ---");
            var psi = new ProcessStartInfo(GetExePath("ethr.exe"),
                $"-c {host} -t b -d {duration}s -n {threads} -p {(udp ? "udp" : "tcp")} -port {port} -no")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

            using var proc = Process.Start(psi)!;
            var sw = Stopwatch.StartNew();
            // Drain stdout/stderr WHILE waiting so a full pipe can't stall the client.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            while (!proc.HasExited && sw.Elapsed.TotalSeconds < duration + 40) await Task.Delay(300);
            string output = await stdoutTask;
            string errors = await stderrTask;
            await proc.WaitForExitAsync();
            EthrLogBox.AppendText(output);
            if (!string.IsNullOrWhiteSpace(errors)) EthrLogBox.AppendText("[ERR] " + errors);
            SetProgress(100);
            double throughput = ParseEthrOutput(output + errors);
            SaveHistory("ethr", throughput, 0, 0, 0, host, udp ? "UDP" : "TCP",
                host + ":" + port, GetQualityLabel(CalculateScore(throughput, 0, 0, 0)));
            StatusLeft.Text = Loc.Get("status.ethrdone");
            AddLog("Ethr test finished");
        }
        catch (Exception ex)
        {
            AddLog("Ethr error: " + ex.Message);
            SetProgress(0);
        }
        finally
        {
            _ethrTesting = false;
            EthrRunBtn.IsEnabled = true;
        }
    }

    // ethr -no rate line: "[  SUM]     TCP      001-002 sec      5.7G" or
    // "[ 6604]     UDP      000-001 sec     12.4G     96.6K" (optional Pkts/s column ignored)
    private static readonly System.Text.RegularExpressions.Regex EthrRateLine = new(
        @"^\[\s*(SUM|\d+)\]\s+(?:TCP|UDP)\s+\d+-(\d+)\s+sec\s+([\d.]+)\s*([KMG]?)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static double ParseEthrRate(string num, string unit) =>
        double.TryParse(num, out var v) ? v * (unit switch { "G" => 1e9, "M" => 1e6, "K" => 1e3, _ => 1 }) : 0;

    private double ParseEthrOutput(string output)
    {
        double throughput = 0;
        int sent = 0, received = 0, lost = 0;

        // Rate lines: multi-stream runs include "[ SUM]" per-second aggregates; single-stream
        // and UDP runs only print per-connection lines. Bucket rates per second; prefer the
        // authoritative SUM, otherwise sum the individual streams of that second.
        var streamSums = new Dictionary<int, double>();
        var sumRates = new Dictionary<int, double>();
        foreach (var line in output.Split('\n'))
        {
            var m = EthrRateLine.Match(line.Trim());
            if (!m.Success) continue;
            double bps = ParseEthrRate(m.Groups[3].Value, m.Groups[4].Value);
            int endSec = int.TryParse(m.Groups[2].Value, out var s) ? s : 0;
            if (m.Groups[1].Value == "SUM")
                sumRates[endSec] = bps;
            else
                streamSums[endSec] = streamSums.GetValueOrDefault(endSec) + bps;
        }
        foreach (int sec in sumRates.Keys.Union(streamSums.Keys))
        {
            double total = sumRates.TryGetValue(sec, out var sv) ? sv : streamSums.GetValueOrDefault(sec);
            throughput = Math.Max(throughput, total / 1e6);
        }
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains("Sent =")) continue;
            var nums = line.Split(new[] { '=', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => int.TryParse(s, out _)).Select(int.Parse).ToArray();
            if (nums.Length >= 3) { sent = nums[0]; received = nums[1]; lost = nums[2]; }
        }
        _ethrThroughput = throughput;
        EthrThroughput.Text = throughput > 0 ? throughput.ToString("F1") : "--";
        EthrThroughput.Foreground = (Brush)new BrushConverter().ConvertFrom(
            throughput > 100 ? "#4ADE80" : throughput > 10 ? "#62CDFF" : "#FBBF24");
        EthrConnSent.Text    = "Sent: "     + sent;
        EthrConnRecv.Text    = "Received: " + received;
        EthrConnLost.Text    = "Lost: "     + lost;
        double lossRate = sent > 0 ? (double)lost / sent * 100 : 0;
        EthrLossRate.Text    = "Loss rate: " + lossRate.ToString("F2") + "%";
        EthrLossRate.Foreground = (Brush)new BrushConverter().ConvertFrom(lossRate > 1 ? "#F87171" : "#6F7686");
        EthrConnections.Text = "Connections: " + received;
        EthrSummary.Text = throughput > 0
            ? (sent > 0
                ? throughput.ToString("F1") + " Mbps | " + received + " conns | " + lossRate.ToString("F1") + "% loss"
                : throughput.ToString("F1") + " Mbps")
            : "No throughput data";
        return throughput;
    }

    private void EthrClearLog_Click(object? sender, RoutedEventArgs e) => EthrLogBox.Text = "";

    // ── Website Speed Test ──────────────────────────────────────────────────
    private bool _websiteTesting;

    private sealed record WebSample(
        int StatusCode, string Server, string ContentType, long BodyBytes,
        double DnsMs, double TcpMs, double TlsMs, double TtfbMs, double ContentMs, double TotalMs,
        string FinalUrl, string Ip, int Redirects, string Via);

    private async void WebsiteRun_Click(object? sender, RoutedEventArgs e)
    {
        if (_websiteTesting) return;
        string input = WebsiteUrlInput.Text.Trim();
        if (input.Length == 0) { AddLog("Website: please enter a URL."); return; }
        string scheme = WebsiteScheme.SelectedIndex == 1 ? "http" : "https";

        // 批量：分号/逗号/空格/换行分隔多个网址（最多 8 个）；单个网址走 3 采样完整流程
        var targets = new List<Uri>();
        foreach (var raw in input.Split(new[] { ';', ',', '\n', '\r', '，', '；', '、', ' ' },
                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string t = raw;
            if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                t = scheme + "://" + t;
            if (Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
                targets.Add(u);
            else
                AddLog("Website: skipped invalid URL '" + raw + "'.");
        }
        targets = targets.Distinct().ToList();
        if (targets.Count == 0)
        {
            SetWebsiteStatus("No valid URL.", "#F87171");
            AddLog("Website: no valid URL.");
            return;
        }

        // Optional proxy (Settings → Proxy Server), e.g. http://127.0.0.1:7890
        Uri? proxy = null;
        string proxyText = SettingProxy.Text.Trim();
        if (proxyText.Length > 0)
        {
            if (!proxyText.Contains("://", StringComparison.Ordinal)) proxyText = "http://" + proxyText;
            if (!Uri.TryCreate(proxyText, UriKind.Absolute, out proxy) || proxy.Scheme != "http" || proxy.Host.Length == 0)
            {
                SetWebsiteStatus("Invalid proxy URL — use http://host:port (e.g. http://127.0.0.1:7890)", "#F87171");
                AddLog("Website: invalid proxy URL '" + proxyText + "'.");
                return;
            }
        }

        _websiteTesting = true;
        WebsiteRunBtn.IsEnabled = false;
        WebsiteLogBox.Text = "";
        ResetWebsiteUI();
        var startUri = targets[0];
        string viaText = proxy != null ? " via proxy " + proxy.Authority : "";
        SetWebsiteStatus("Testing " + startUri.Authority + viaText + " …", "#FBBF24");
        try
        {
            if (targets.Count == 1)
            {
                const int samples = 3;
                var results = new List<WebSample>(samples);
                for (int i = 1; i <= samples; i++)
                {
                    SetWebsiteStatus($"Testing {startUri.Authority} … sample {i}/{samples}", "#FBBF24");
                    var s = await RunWebsiteSampleAsync(startUri, proxy);
                    results.Add(s);
                    ShowWebsiteSample(i, samples, s);
                }
                double avgTtfb = results.Average(r => r.TtfbMs);
                double avgSpeed = results.Average(r => r.BodyBytes / Math.Max(0.001, r.ContentMs / 1000.0));
                WebsiteSpeed.Text = FormatSpeed(avgSpeed);
                WebsiteSpeedMbps.Text = "≈ " + (avgSpeed * 8 / 1_000_000.0).ToString("F1") + " Mbps";
                WebsiteTtfb.Text = avgTtfb.ToString("F1");
                SetWebsiteStatus("Complete · " + samples + " samples" + viaText +
                                 " · " + results[^1].FinalUrl, "#4ADE80");
                AddLog($"Website test done: {startUri} — avg {FormatSpeed(avgSpeed)}, TTFB {avgTtfb:F1} ms");
            }
            else
            {
                // ── Batch mode: 1 sample per site, comparison table ──
                BatchCard.Visibility = Visibility.Visible;
                _batchRows.Clear();
                BatchGrid.ItemsSource = _batchRows;
                int ok = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    SetWebsiteStatus($"Batch {i + 1}/{targets.Count}: {targets[i].Authority} …", "#FBBF24");
                    try
                    {
                        var s = await RunWebsiteSampleAsync(targets[i], proxy);
                        UpdateWebsiteUI(s);
                        double sbps = s.BodyBytes / Math.Max(0.001, s.ContentMs / 1000.0);
                        _batchRows.Add(new BatchRow(
                            s.FinalUrl, s.StatusCode.ToString(),
                            FormatSpeed(sbps) + " (" + (sbps * 8 / 1_000_000.0).ToString("F1") + " Mbps)",
                            s.TtfbMs.ToString("F1") + " ms", FormatBytes(s.BodyBytes),
                            s.TotalMs.ToString("F0") + " ms",
                            s.Via.Length > 0 ? "proxy " + s.Via : "direct"));
                        WebsiteLogBox.AppendText(
                            $"[batch {i + 1}/{targets.Count}] {s.StatusCode} {s.FinalUrl}" +
                            $" | {FormatSpeed(sbps)} | TTFB {s.TtfbMs:F1} ms" +
                            (s.Via.Length > 0 ? $" · via proxy {s.Via}" : "") + "\r\n");
                        ok++;
                    }
                    catch (Exception bex)
                    {
                        _batchRows.Add(new BatchRow(targets[i].ToString(), "FAILED", "-", "-", "-", "-",
                            proxy != null ? "proxy " + proxy.Authority : "direct"));
                        WebsiteLogBox.AppendText(
                            $"[batch {i + 1}/{targets.Count}] FAILED {targets[i]} — {bex.Message}\r\n");
                    }
                    WebsiteLogBox.ScrollToEnd();
                }
                SetWebsiteStatus($"Complete · batch {ok}/{targets.Count} sites" + viaText,
                    ok == targets.Count ? "#4ADE80" : "#FBBF24");
                AddLog($"Website batch test done: {ok}/{targets.Count} succeeded");
            }
        }
        catch (Exception ex)
        {
            SetWebsiteStatus("Failed: " + ex.Message, "#F87171");
            WebsiteLogBox.AppendText("[ERR] " + ex.Message + "\r\n");
            AddLog("Website test error: " + ex.Message);
        }
        finally
        {
            _websiteTesting = false;
            WebsiteRunBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// Runs one polite single-connection GET: DNS → TCP → (proxy CONNECT) → TLS → TTFB → body,
    /// following up to 5 redirects (http→https etc.). Connection: close; 25 MB cap.
    /// When a proxy is set, DNS/TCP target the proxy and a CONNECT tunnel carries the TLS.
    /// </summary>
    private static async Task<WebSample> RunWebsiteSampleAsync(Uri startUri, Uri? proxy, CancellationToken ct = default)
    {
        var uri = startUri;
        int redirects = 0;
        while (true)
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
            System.IO.Stream? stream = null;
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
                    catch (Exception ex) when (ex is System.IO.IOException or SocketException)
                    {
                        throw new Exception(
                            $"TLS handshake was forcibly reset for {uri.Host} — TCP connected fine, so the domain is likely blocked by your network (SNI reset) or refused by the edge. Try opening the site in a browser to confirm. If you have a proxy (Clash/v2rayN/…), set it in Settings → Proxy Server and retry. [{ex.InnerException?.Message ?? ex.Message}]");
                    }
                    tlsMs = sw.Elapsed.TotalMilliseconds;
                    stream = ssl;
                }

                // ── HTTP GET (Connection: close → read to EOF) ──
                string hostHeader = uri.IsDefaultPort ? uri.Host : uri.Authority;
                string req = $"GET {uri.PathAndQuery} HTTP/1.1\r\n" +
                             $"Host: {hostHeader}\r\n" +
                             "User-Agent: SpeedPro-WebsiteTest/3.7\r\n" +
                             "Accept: */*\r\n" +
                             "Accept-Encoding: gzip, deflate\r\n" +
                             "Connection: close\r\n\r\n";
                var reqBytes = Encoding.ASCII.GetBytes(req);
                var totalSw = Stopwatch.StartNew();
                await stream.WriteAsync(reqBytes, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);

                // Read until end of headers → TTFB = first byte received
                var headerBuf = new System.IO.MemoryStream();
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
                catch (System.IO.IOException io)
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
                catch (System.IO.IOException io)
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
    private static async Task<string> ReadProxyConnectResponseAsync(System.IO.Stream stream, CancellationToken ct)
    {
        var buf = new System.IO.MemoryStream();
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

    private void ShowWebsiteSample(int sample, int total, WebSample s)
    {
        double speedBps = s.BodyBytes / Math.Max(0.001, s.ContentMs / 1000.0);
        double speedMbps = speedBps * 8 / 1_000_000.0;
        WebsiteLogBox.AppendText(
            $"[sample {sample}/{total}] {(s.TlsMs > 0 ? "HTTPS" : "HTTP")} {s.StatusCode} {s.FinalUrl}" +
            $" | {FormatBytes(s.BodyBytes)} in {s.TotalMs:F0} ms | {FormatSpeed(speedBps)} ({speedMbps:F1} Mbps)" +
            $" | DNS {s.DnsMs:F1} · TCP {s.TcpMs:F1} · TLS {(s.TlsMs > 0 ? s.TlsMs.ToString("F1") : "—")} · TTFB {s.TtfbMs:F1} ms" +
            (s.Via.Length > 0 ? $" · via proxy {s.Via}" : "") + "\r\n");
        UpdateWebsiteUI(s);
    }

    /// <summary>Pushes one sample into the hero numbers, timeline bars and response info.</summary>
    private void UpdateWebsiteUI(WebSample s)
    {
        double speedBps = s.BodyBytes / Math.Max(0.001, s.ContentMs / 1000.0);
        // Timeline (last sample breakdown)
        double totalRef = Math.Max(1, s.TotalMs);
        WebValDns.Text = s.DnsMs.ToString("F1") + " ms";
        WebValTcp.Text = s.TcpMs.ToString("F1") + " ms";
        WebValTls.Text = s.TlsMs > 0 ? s.TlsMs.ToString("F1") + " ms" : "—";
        WebValTtfb.Text = s.TtfbMs.ToString("F1") + " ms";
        WebValContent.Text = s.ContentMs.ToString("F1") + " ms";
        WebValTotal.Text = s.TotalMs.ToString("F1") + " ms";
        WebBarDns.Width = Math.Clamp(s.DnsMs / totalRef * 200, 0, 200);
        WebBarTcp.Width = Math.Clamp(s.TcpMs / totalRef * 200, 0, 200);
        WebBarTls.Width = Math.Clamp(s.TlsMs / totalRef * 200, 0, 200);
        WebBarTtfb.Width = Math.Clamp(s.TtfbMs / totalRef * 200, 0, 200);
        WebBarContent.Width = Math.Clamp(s.ContentMs / totalRef * 200, 0, 200);

        WebInfoUrl.Text = s.FinalUrl;
        WebInfoStatus.Text = s.StatusCode.ToString();
        WebInfoIp.Text = s.Ip;
        WebInfoServer.Text = string.IsNullOrEmpty(s.Server) ? "—" : s.Server;
        WebInfoType.Text = string.IsNullOrEmpty(s.ContentType) ? "—" : s.ContentType;
        WebInfoSize.Text = FormatBytes(s.BodyBytes);
        WebInfoRedirects.Text = s.Redirects.ToString();
        WebsiteSpeed.Text = FormatSpeed(speedBps);
        WebsiteSpeedMbps.Text = "≈ " + (speedBps * 8 / 1_000_000.0).ToString("F1") + " Mbps";
        WebsiteTtfb.Text = s.TtfbMs.ToString("F1");
    }

    private sealed record BatchRow(string Url, string Status, string Speed, string Ttfb, string Size, string Total, string Connection);

    private readonly ObservableCollection<BatchRow> _batchRows = new();

    private void BatchGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == "Url") e.Column.Width = new DataGridLength(2, DataGridLengthUnitType.Star);
    }

    private void ResetWebsiteUI()
    {
        WebsiteSpeed.Text = "--";
        WebsiteSpeedMbps.Text = "";
        WebsiteTtfb.Text = "--";
        BatchCard.Visibility = Visibility.Collapsed;
        _batchRows.Clear();
        foreach (var el in new[] { WebValDns, WebValTcp, WebValTls, WebValTtfb, WebValContent, WebValTotal })
            el.Text = "--";
        foreach (var b in new[] { WebBarDns, WebBarTcp, WebBarTls, WebBarTtfb, WebBarContent })
            b.Width = 0;
        foreach (var el in new[] { WebInfoUrl, WebInfoStatus, WebInfoIp, WebInfoServer, WebInfoType, WebInfoSize, WebInfoRedirects })
            el.Text = "--";
    }

    private void SetWebsiteStatus(string text, string color)
    {
        WebsiteStatus.Text = text;
        WebsiteStatus.Foreground = (Brush)new BrushConverter().ConvertFrom(color)!;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_048_576 => (bytes / 1_048_576.0).ToString("F2") + " MB",
        >= 1024      => (bytes / 1024.0).ToString("F1") + " KB",
        _            => bytes + " B"
    };

    private static string FormatSpeed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1_048_576 => (bytesPerSec / 1_048_576.0).ToString("F2") + " MB/s",
        >= 1024      => (bytesPerSec / 1024.0).ToString("F1") + " KB/s",
        _            => bytesPerSec.ToString("F0") + " B/s"
    };

    private void WebsiteClearLog_Click(object? sender, RoutedEventArgs e) => WebsiteLogBox.Text = "";

    // ── Continuous Monitor ──────────────────────────────────────────────────
    private bool _monitorRunning;
    private CancellationTokenSource? _monitorCts;
    private DateTime _monitorStartedAt;
    private int _monitorFailures;
    private readonly ObservableCollection<MonitorSampleRec> _monitorSamples = new();

    private sealed record MonitorSampleRec(string Time, double SpeedMbps, double TtfbMs);

    private string MonitorPath => global::System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpeedPro", "monitor.json");

    private void LoadMonitor()
    {
        try
        {
            if (!global::System.IO.File.Exists(MonitorPath)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<MonitorSampleRec>>(
                global::System.IO.File.ReadAllText(MonitorPath));
            if (list == null) return;
            foreach (var m in list.TakeLast(240)) _monitorSamples.Add(m);
            DrawMonitorChart();
            UpdateMonitorStats();
        }
        catch { }
    }

    private void PersistMonitor()
    {
        try
        {
            global::System.IO.Directory.CreateDirectory(global::System.IO.Path.GetDirectoryName(MonitorPath)!);
            global::System.IO.File.WriteAllText(MonitorPath,
                System.Text.Json.JsonSerializer.Serialize(_monitorSamples.ToList()));
        }
        catch { }
    }

    private void AddMonitorLog(string msg)
    {
        MonitorLogBox.AppendText(msg + "\r\n");
        if (MonitorLogBox.Text.Length > 40000)
            MonitorLogBox.Text = MonitorLogBox.Text[20000..];
        MonitorLogBox.ScrollToEnd();
    }

    private void SetMonitorStatus(string text, string color)
    {
        MonitorStatus.Text = text;
        MonitorStatus.Foreground = (Brush)new BrushConverter().ConvertFrom(color)!;
    }

    private void UpdateMonitorStats()
    {
        MonitorStatSamples.Text = _monitorSamples.Count.ToString();
        MonitorStatTtfb.Text = _monitorSamples.Count > 0
            ? _monitorSamples.Average(m => m.TtfbMs).ToString("F1") + " ms"
            : "--";
        MonitorStatFail.Text = _monitorFailures.ToString();
        MonitorStatSince.Text = _monitorRunning ? _monitorStartedAt.ToString("HH:mm:ss") : "--";
    }

    private async void MonitorStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_monitorRunning)
        {
            SetMonitorStatus("Stopping… (finishes the in-flight check first)", "#FBBF24");
            _monitorCts?.Cancel();
            return;
        }
        string input = MonitorUrl.Text.Trim();
        if (input.Length == 0) { AddMonitorLog("[ERR] Please enter a target URL."); return; }
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            input = "https://" + input;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var target) ||
            (target.Scheme != "http" && target.Scheme != "https"))
        {
            AddMonitorLog("[ERR] Invalid URL '" + input + "'.");
            return;
        }

        Uri? proxy = null;
        string proxyText = SettingProxy.Text.Trim();
        if (proxyText.Length > 0)
        {
            if (!proxyText.Contains("://", StringComparison.Ordinal)) proxyText = "http://" + proxyText;
            Uri.TryCreate(proxyText, UriKind.Absolute, out proxy);
        }
        int intervalMin = (MonitorInterval.SelectedIndex switch
        { 0 => 5, 1 => 10, 2 => 15, 3 => 30, _ => 60 });

        _monitorCts = new CancellationTokenSource();
        _monitorRunning = true;
        _monitorFailures = 0;
        _monitorStartedAt = DateTime.Now;
        MonitorStartBtn.Content = "Stop Monitor";
        MonitorStartBtn.Background = (Brush)new BrushConverter().ConvertFrom("#F87171")!;
        UpdateMonitorStats();
        AddMonitorLog($"--- Monitor started: {target} every {intervalMin} min" +
                       (proxy != null ? $" via proxy {proxy.Authority}" : "") + " ---");
        try { await MonitorLoopAsync(target, proxy, intervalMin, _monitorCts.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            _monitorRunning = false;
            MonitorStartBtn.Content = "Start Monitor";
            MonitorStartBtn.Background = (Brush)new BrushConverter().ConvertFrom("#00E5FF")!;
            SetMonitorStatus($"Stopped · {_monitorSamples.Count} samples collected", "#6F7686");
            UpdateMonitorStats();
        }
    }

    private async Task MonitorLoopAsync(Uri target, Uri? proxy, int intervalMin, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SetMonitorStatus($"Checking {target.Authority} …", "#FBBF24");
            try
            {
                var s = await RunWebsiteSampleAsync(target, proxy, ct);
                double sbps = s.BodyBytes / Math.Max(0.001, s.ContentMs / 1000.0);
                var rec = new MonitorSampleRec(
                    DateTime.Now.ToString("MM-dd HH:mm"),
                    Math.Round(sbps / 1_048_576.0, 3),
                    Math.Round(s.TtfbMs, 1));
                _monitorSamples.Add(rec);
                if (_monitorSamples.Count > 240) _monitorSamples.RemoveAt(0);
                PersistMonitor();
                DrawMonitorChart();
                UpdateMonitorStats();
                MonitorHeroSpeed.Text = FormatSpeed(sbps);
                MonitorHeroTtfb.Text = s.TtfbMs.ToString("F1");
                SetMonitorStatus($"Last check {DateTime.Now:HH:mm:ss} · {s.StatusCode} · TTFB {s.TtfbMs:F1} ms" +
                                 (s.Via.Length > 0 ? $" · via proxy {s.Via}" : ""), "#4ADE80");
                AddMonitorLog($"[{DateTime.Now:HH:mm:ss}] OK {s.StatusCode} · {FormatSpeed(sbps)} · TTFB {s.TtfbMs:F1} ms" +
                              (s.Via.Length > 0 ? $" · via proxy {s.Via}" : ""));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _monitorFailures++;
                UpdateMonitorStats();
                SetMonitorStatus("Last check failed: " + ex.Message, "#F87171");
                AddMonitorLog($"[{DateTime.Now:HH:mm:ss}] ERR " + ex.Message);
            }

            var next = DateTime.Now.AddMinutes(intervalMin);
            while (DateTime.Now < next)
            {
                ct.ThrowIfCancellationRequested();
                SetMonitorStatus($"Running · every {intervalMin} min · next check at {next:HH:mm:ss} " +
                                 $"({(int)(next - DateTime.Now).TotalSeconds}s)", "#4ADE80");
                await Task.Delay(1000, ct);
            }
        }
    }

    private void DrawMonitorChart()
    {
        var canvas = MonitorChartCanvas;
        if (canvas == null) return;
        canvas.Children.Clear();
        var items = _monitorSamples.TakeLast(120).ToList();
        MonitorTrendTitle.Text = $"MONITOR TREND · {items.Count} samples";
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 10 || h < 10) return;
        if (items.Count < 2)
        {
            var tb = new TextBlock
            {
                Text = "Start the monitor to collect samples",
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#353550")!
            };
            canvas.Children.Add(tb);
            tb.Measure(new Size(w, h));
            Canvas.SetLeft(tb, Math.Max(0, (w - tb.ActualWidth) / 2));
            Canvas.SetTop(tb, Math.Max(0, (h - tb.ActualHeight) / 2));
            return;
        }
        for (int i = 0; i <= 4; i++)
            canvas.Children.Add(new Line
            {
                X1 = 0, Y1 = h * i / 4, X2 = w, Y2 = h * i / 4,
                Stroke = new SolidColorBrush(Color.FromArgb(20, 120, 130, 160)), StrokeThickness = 1
            });
        double maxSpeed = Math.Max(items.Max(m => m.SpeedMbps), 0.05);
        double maxTtfb = Math.Max(items.Max(m => m.TtfbMs), 1);
        Point Plot(int i, double v, double max) =>
            new(3 + i * (w - 6) / (items.Count - 1), h - 8 - (v / max) * (h - 20));
        AddPolyline(canvas, items.Select((m, i) => Plot(i, m.SpeedMbps, maxSpeed)).ToList(), "#62CDFF", 2);
        AddPolyline(canvas, items.Select((m, i) => Plot(i, m.TtfbMs, maxTtfb)).ToList(), "#FFD740", 1.5);
        var lblL = new TextBlock { Text = maxSpeed.ToString("F2") + " Mbps", FontSize = 9,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#565C74")! };
        canvas.Children.Add(lblL);
        var lblR = new TextBlock { Text = maxTtfb.ToString("F0") + " ms", FontSize = 9,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#565C74")! };
        canvas.Children.Add(lblR);
        lblR.Measure(new Size(w, h));
        Canvas.SetLeft(lblR, w - lblR.ActualWidth - 6);
        Canvas.SetTop(lblR, 4);
    }

    // ── Pingularity ─────────────────────────────────────────────────────────
    private void PingularityStart_Click(object? sender, RoutedEventArgs e)
    {
        if (_pingularity != null && !_pingularity.HasExited)
        {
            _pingularity.Kill(); _pingularity = null;
            PingularityStatus.Text = "Service: stopped";
            PingularityStatus.Foreground = (Brush)new BrushConverter().ConvertFrom("#6F7686")!;
            PingularityStartBtn.Content = "Start Service";
            PingularityStartBtn.Background = (Brush)new BrushConverter().ConvertFrom("#62CDFF");
            AddLog("Pingularity stopped");
            return;
        }
        int port = int.TryParse(PingularityPort.Text, out var p) ? p : 19000;
        var psi = new ProcessStartInfo(GetExePath("pingularity.exe"),
            $"--quick-setup skip --listen 127.0.0.1:{port} --db \"{_pingDbPath}\"")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        _pingularity = Process.Start(psi);
        PingularityStatus.Text = $"Service running on :{port}";
        PingularityStatus.Foreground = (Brush)new BrushConverter().ConvertFrom("#4ADE80")!;
        PingularityStartBtn.Content = "Stop Service";
        PingularityStartBtn.Background = (Brush)new BrushConverter().ConvertFrom("#F87171");
        AddLog("Pingularity started on port " + port);
        _ = CheckPingularityDashboardAsync(port);
    }

    private async Task CheckPingularityDashboardAsync(int port)
    {
        await Task.Delay(2000);
        try
        {
            var client = new System.Net.Http.HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var resp = await client.GetAsync($"http://127.0.0.1:{port}/readyz");
            if (resp.IsSuccessStatusCode)
            {
                Dispatcher.Invoke(() =>
                {
                    PingularityStatus.Text = $"Service running on :{port} (healthy)";
                    PingularityDashUrl.Text = "http://127.0.0.1:" + port;
                });
                await InitPingDashboardAsync($"http://127.0.0.1:{port}");
            }
        }
        catch { /* daemon may still be starting */ }
    }

    // ── Embedded Pingularity dashboard (WebView2) ──────────────────────────
    private Microsoft.Web.WebView2.Wpf.WebView2? _pingWebView;

    private async Task InitPingDashboardAsync(string url)
    {
        try
        {
            if (_pingWebView == null)
            {
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    userDataFolder: global::System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpeedPro", "WebView2"));
                _pingWebView = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
                PingDashboardHost.Children.Clear();
                PingDashboardHost.Children.Add(_pingWebView);
                await _pingWebView.EnsureCoreWebView2Async(env);
            }
            PingDashboardPlaceholder.Visibility = Visibility.Collapsed;
            _pingWebView.Visibility = Visibility.Visible;
            _pingWebView.CoreWebView2.Navigate(url);
            AddLog("Pingularity dashboard embedded (WebView2).");
        }
        catch (Exception ex)
        {
            AddLog("WebView2 unavailable, use Open Dashboard instead: " + ex.Message);
        }
    }

    private async void PingDashboardReconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_pingularity == null || _pingularity.HasExited)
        {
            AddLog("Pingularity not running. Start the service first.");
            return;
        }
        int port = int.TryParse(PingularityPort.Text, out var p) ? p : 19000;
        await InitPingDashboardAsync("http://127.0.0.1:" + port);
    }

    private void PingularityOpenDashboard_Click(object? sender, RoutedEventArgs e)
    {
        if (_pingularity == null || _pingularity.HasExited)
        {
            AddLog("Pingularity not running. Start service first.");
            return;
        }
        int port = int.TryParse(PingularityPort.Text, out var p) ? p : 19000;
        System.Diagnostics.Process.Start("explorer.exe", $"http://127.0.0.1:{port}");
    }

    private void PingularityClearLog_Click(object? sender, RoutedEventArgs e) => PingularityLogBox.Text = "";

    // ── Network Tools: traceroute + DNS benchmark ───────────────────────────
    private bool _traceRunning;
    private Process? _traceProc;
    private readonly List<(int hop, double? bestMs, string ep)> _traceHops = new();
    private bool _dnsRunning;
    private readonly ObservableCollection<DnsRow> _dnsRows = new();

    private sealed record DnsRow(string Server, string Best, string Avg, string Result);

    private static readonly (string Name, string Ip)[] DnsServers =
    {
        ("AliDNS 223.5.5.5", "223.5.5.5"),
        ("DNSPod 119.29.29.29", "119.29.29.29"),
        ("114DNS 114.114.114.114", "114.114.114.114"),
        ("Baidu 180.76.76.76", "180.76.76.76"),
        ("Google 8.8.8.8", "8.8.8.8"),
        ("Cloudflare 1.1.1.1", "1.1.1.1"),
    };

    private void SetTraceStatus(string text, string color)
    {
        TraceStatus.Text = text;
        TraceStatus.Foreground = (Brush)new BrushConverter().ConvertFrom(color)!;
    }

    /// <summary>Parses one tracert line: hop number, best latency (any locale unit), endpoint.</summary>
    private static (int hop, double? bestMs, string ep)? ParseTracertLine(string line)
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

    private void DrawTracerouteRows()
    {
        var canvas = TracerouteCanvas;
        if (canvas == null) return;
        canvas.Children.Clear();
        double w = canvas.ActualWidth; if (w < 10) w = 600;
        double rowH = 22;
        canvas.Height = Math.Max(240, _traceHops.Count * rowH + 8);
        var withLat = _traceHops.Where(h => h.bestMs != null).ToList();
        double maxLat = withLat.Count > 0 ? withLat.Max(h => h.bestMs!.Value) : 1;
        double barX = w * 0.42, barMax = w * 0.40;
        for (int i = 0; i < _traceHops.Count; i++)
        {
            var (hop, ms, ep) = _traceHops[i];
            double y = i * rowH + 3;
            var label = new TextBlock
            {
                Text = $"{hop,2}  {ep}",
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#8A90A8")!,
                FontFamily = (FontFamily)FindResource("MonoFont")
            };
            canvas.Children.Add(label);
            Canvas.SetLeft(label, 8); Canvas.SetTop(label, y);
            if (ms != null)
            {
                double latency = ms.Value;
                string color = latency < 50 ? "#4ADE80" : latency < 150 ? "#FBBF24" : "#F87171";
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Height = 8, RadiusX = 4, RadiusY = 4,
                    Width = Math.Max(6, latency / maxLat * barMax),
                    Fill = (Brush)new BrushConverter().ConvertFrom(color)!
                };
                canvas.Children.Add(bar);
                Canvas.SetLeft(bar, barX); Canvas.SetTop(bar, y + 4);
                var val = new TextBlock
                {
                    Text = latency.ToString("F1") + " ms", FontSize = 10.5,
                    Foreground = (Brush)new BrushConverter().ConvertFrom("#8A90A8")!
                };
                canvas.Children.Add(val);
                Canvas.SetLeft(val, barX + bar.Width + 8); Canvas.SetTop(val, y + 1);
            }
            else
            {
                var t = new TextBlock
                {
                    Text = "timeout / no response", FontSize = 10.5,
                    Foreground = (Brush)new BrushConverter().ConvertFrom("#F87171")!
                };
                canvas.Children.Add(t);
                Canvas.SetLeft(t, barX); Canvas.SetTop(t, y + 1);
            }
        }
    }

    private async void TraceBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_traceRunning)
        {
            try { _traceProc?.Kill(); } catch { }
            return;
        }
        string host = TracerouteHost.Text.Trim();
        if (host.Length == 0) { SetTraceStatus("Please enter a host.", "#F87171"); return; }

        _traceRunning = true;
        TraceBtn.Content = "Stop";
        _traceHops.Clear();
        TracerouteCanvas.Children.Clear();
        SetTraceStatus($"Tracing route to {host} … (max 30 hops)", "#FBBF24");
        try
        {
            var psi = new ProcessStartInfo("tracert", $"-d {host}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            try { psi.StandardOutputEncoding = Encoding.GetEncoding(936); } catch { }
            _traceProc = Process.Start(psi)!;
            string? line;
            while ((line = await _traceProc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Trim().Length == 0) continue;
                ToolsLogBox.AppendText(line + "\r\n");
                ToolsLogBox.ScrollToEnd();
                var hop = ParseTracertLine(line);
                if (hop != null && hop.Value.hop > 0)
                {
                    _traceHops.Add(hop.Value);
                    DrawTracerouteRows();
                }
            }
            await _traceProc.WaitForExitAsync();
            SetTraceStatus($"Done · {_traceHops.Count} hops recorded", "#4ADE80");
            AddLog($"Traceroute done: {host} — {_traceHops.Count} hops");
        }
        catch (Exception ex)
        {
            SetTraceStatus("Failed: " + ex.Message, "#F87171");
            AddLog("Traceroute failed: " + ex.Message);
        }
        finally
        {
            _traceRunning = false;
            TraceBtn.Content = "Trace";
        }
    }

    // ── DNS benchmark: raw UDP A-queries ────────────────────────────────────
    private static async Task<double> DnsQueryMsAsync(string server, string domain, CancellationToken ct)
    {
        ushort id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var pkt = new System.IO.MemoryStream();
        pkt.Write(new byte[] { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in domain.TrimEnd('.').Split('.'))
        {
            if (label.Length == 0) continue;
            pkt.WriteByte((byte)label.Length);
            pkt.Write(Encoding.ASCII.GetBytes(label));
        }
        pkt.Write(new byte[] { 0, 1, 0, 1 });   // type A, class IN

        using var udp = new UdpClient(server, 53);
        var sw = Stopwatch.StartNew();
        await udp.SendAsync(pkt.ToArray(), (int)pkt.Length).WaitAsync(TimeSpan.FromSeconds(2), ct);
        var res = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), ct);
        sw.Stop();
        if (res.Buffer.Length < 12 || res.Buffer[0] != (byte)(id >> 8) || res.Buffer[1] != (byte)id)
            throw new Exception("unexpected DNS response");
        return sw.Elapsed.TotalMilliseconds;
    }

    private async void DnsRunBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_dnsRunning) return;
        string domain = DnsDomain.Text.Trim();
        if (domain.Length == 0) { SetDnsStatus("Please enter a domain.", "#F87171"); return; }
        _dnsRunning = true;
        DnsRunBtn.IsEnabled = false;
        _dnsRows.Clear();
        DnsGrid.ItemsSource = _dnsRows;
        try
        {
            for (int s = 0; s < DnsServers.Length; s++)
            {
                var (name, ip) = DnsServers[s];
                SetDnsStatus($"Querying {name} …", "#FBBF24");
                var times = new List<double>();
                int fails = 0;
                for (int i = 0; i < 3; i++)
                {
                    try { times.Add(await DnsQueryMsAsync(ip, domain, CancellationToken.None)); }
                    catch { fails++; }
                }
                var row = times.Count > 0
                    ? new DnsRow(name, times.Min().ToString("F1") + " ms",
                        times.Average().ToString("F1") + " ms", $"OK ({times.Count}/3)")
                    : new DnsRow(name, "-", "-", "Timeout");
                _dnsRows.Add(row);
                ToolsLogBox.AppendText($"[dns] {name} → best {row.Best} / avg {row.Avg} ({row.Result})\r\n");
                ToolsLogBox.ScrollToEnd();
                SetDnsStatus($"Done {s + 1}/{DnsServers.Length} servers", "#FBBF24");
            }
            var reachable = _dnsRows.Count(r => r.Result.StartsWith("OK"));
            SetDnsStatus($"Complete · {reachable}/{DnsServers.Length} servers reachable", reachable > 0 ? "#4ADE80" : "#F87171");
            AddLog($"DNS benchmark done for {domain}: {reachable}/{DnsServers.Length} reachable");
        }
        finally
        {
            _dnsRunning = false;
            DnsRunBtn.IsEnabled = true;
        }
    }

    private void SetDnsStatus(string text, string color)
    {
        DnsStatus.Text = text;
        DnsStatus.Foreground = (Brush)new BrushConverter().ConvertFrom(color)!;
    }

    // ── Theme follows system ────────────────────────────────────────────────
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = System.Windows.Interop.HwndSource.FromHwnd(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        hwnd?.AddHook(HwndHook);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet" &&
            SetAutoThemeCheck?.IsChecked == true)
        {
            ApplySystemTheme();
        }
        return IntPtr.Zero;
    }

    private void ApplySystemTheme()
    {
        try
        {
            var v = global::Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                ?.GetValue("AppsUseLightTheme");
            bool light = v is int i && i == 1;
            SetTheme(light ? "ClassicLight" : "DarkTech");
        }
        catch { }
    }

    private void AutoTheme_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingConfig) return;
        SaveConfig();
        if (SetAutoThemeCheck.IsChecked == true) ApplySystemTheme();
    }

    // ── Continuous ping tool ────────────────────────────────────────────────
    private bool _pingRunning;
    private CancellationTokenSource? _pingCts;
    private readonly List<double> _pingRtts = new();
    private int _pingSent, _pingRecv;

    private void SetPingStatus(string text, string color)
    {
        PingStatus.Text = text;
        PingStatus.Foreground = (Brush)new BrushConverter().ConvertFrom(color)!;
    }

    private async void PingBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (_pingRunning) { _pingCts?.Cancel(); return; }
        string host = PingHost.Text.Trim();
        if (host.Length == 0) { SetPingStatus("Please enter a host.", "#F87171"); return; }
        _pingCts = new CancellationTokenSource();
        _pingRunning = true;
        PingBtn.Content = "Stop";
        PingBtn.Background = (Brush)new BrushConverter().ConvertFrom("#F87171")!;
        _pingRtts.Clear();
        _pingSent = _pingRecv = 0;
        try { await PingLoopAsync(host, _pingCts.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetPingStatus("Failed: " + ex.Message, "#F87171"); }
        finally
        {
            _pingRunning = false;
            PingBtn.Content = "Start";
            PingBtn.Background = (Brush)new BrushConverter().ConvertFrom("#00E5FF")!;
            SetPingStatus("Stopped", "#6F7686");
        }
    }

    private async Task PingLoopAsync(string host, CancellationToken ct)
    {
        using var ping = new System.Net.NetworkInformation.Ping();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(host, 2000);
                _pingSent++;
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                {
                    _pingRecv++;
                    _pingRtts.Add(reply.RoundtripTime);
                    if (_pingRtts.Count > 120) _pingRtts.RemoveAt(0);
                }
                double loss = _pingSent > 0 ? (_pingSent - _pingRecv) * 100.0 / _pingSent : 0;
                PingStats.Text = $"Sent {_pingSent} · Recv {_pingRecv} · Loss {loss:F1}%" +
                    (_pingRtts.Count > 0
                        ? $" · Min {_pingRtts.Min():F0} · Avg {_pingRtts.Average():F1} · Max {_pingRtts.Max():F0} ms"
                        : "");
                DrawPingChart();
                SetPingStatus($"Pinging {host} …", "#4ADE80");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _pingSent++;
                SetPingStatus("Ping error: " + ex.Message, "#F87171");
            }
            await Task.Delay(1000, ct);
        }
    }

    private void DrawPingChart()
    {
        var canvas = PingCanvas;
        if (canvas == null) return;
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w < 10 || h < 10 || _pingRtts.Count == 0) return;
        double max = Math.Max(_pingRtts.Max(), 1);
        double slot = (w - 6) / 120;
        double maxScale = Math.Max(max, 30);
        for (int i = 0; i < _pingRtts.Count; i++)
        {
            double rtt = _pingRtts[i];
            double barH = Math.Max(2, rtt / maxScale * (h - 14));
            string color = rtt < 50 ? "#4ADE80" : rtt < 150 ? "#FBBF24" : "#F87171";
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, slot - 2),
                Height = barH,
                RadiusX = 1.5, RadiusY = 1.5,
                Fill = (Brush)new BrushConverter().ConvertFrom(color)!
            };
            canvas.Children.Add(bar);
            Canvas.SetLeft(bar, 3 + i * slot);
            Canvas.SetTop(bar, h - 4 - barH);
        }
        var lbl = new TextBlock
        {
            Text = max.ToString("F0") + " ms", FontSize = 9,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#565C74")!
        };
        canvas.Children.Add(lbl);
        lbl.Measure(new Size(w, h));
        Canvas.SetLeft(lbl, 6); Canvas.SetTop(lbl, 2);
    }

    private void MonitorExportCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (_monitorSamples.Count == 0)
        {
            AddMonitorLog("[i] No monitor samples to export yet.");
            return;
        }
        try
        {
            string path = global::System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "SpeedPro_Monitor_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            var sb = new StringBuilder("Time,SpeedMbps,TtfbMs\r\n");
            foreach (var m in _monitorSamples)
                sb.Append(m.Time).Append(',').Append(m.SpeedMbps).Append(',').Append(m.TtfbMs).Append("\r\n");
            global::System.IO.File.WriteAllText(path, sb.ToString());
            AddMonitorLog($"[i] Exported {_monitorSamples.Count} samples to {path}");
        }
        catch (Exception ex)
        {
            AddMonitorLog("[ERR] Export failed: " + ex.Message);
        }
    }

    // ── History ─────────────────────────────────────────────────────────────
    private void HistoryFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        ApplyHistoryFilter();
    }

    private void ClearHistory_Click(object? sender, RoutedEventArgs e)
    {
        _history.Clear();
        PersistHistory();
        ApplyHistoryFilter();
        AddLog("History cleared");
    }

    private void ExportCSV_Click(object? sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Time,Engine,Download_Mbps,Upload_Mbps,Ping_ms,Jitter_ms,IP,ISP,Server,Quality");
        foreach (var h in _history)
            sb.AppendLine($"{h.Time},{h.Engine},{h.Download},{h.Upload},{h.Ping},{h.Jitter},{h.Ip},{h.Isp},{h.Server},{h.Quality}");
        string path = global::System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SpeedPro_History.csv");
        global::System.IO.File.WriteAllText(path, sb.ToString());
        AddLog("Exported " + _history.Count + " entries to " + path);
        MessageBox.Show("Exported " + _history.Count + " entries:\n" + path, "SpeedPro",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Settings ────────────────────────────────────────────────────────────
    private void SettingEngine_Changed(object? sender, RoutedEventArgs e)
    {
        _engine = SettingEngine.SelectedIndex == 0 ? EngineType.LibreSpeed : EngineType.SpeedTestGo;
        EngineSpeedTestGo.IsChecked = SettingEngine.SelectedIndex == 1;
        EngineLibreSpeed.IsChecked  = SettingEngine.SelectedIndex == 0;
    }

    private void SettingDuration_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SettingDurationVal != null) SettingDurationVal.Text = ((int)e.NewValue) + "s";
    }

    private void SettingThreads_ValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SettingThreadsVal != null) SettingThreadsVal.Text = ((int)e.NewValue) + " threads";
    }

    // ── Theme switching ────────────────────────────────────────────────────
    /// <summary>Cycles between the two available themes (DarkTech ↔ ClassicLight).</summary>
    private void ThemeToggle_Click(object? sender, RoutedEventArgs e)
    {
        int i = Array.IndexOf(ThemeCycle, _currentTheme);
        if (i < 0) i = 0;
        string next = ThemeCycle[(i + 1) % ThemeCycle.Length];
        SetTheme(next);
    }

    /// <summary>Applies a theme dictionary and updates the settings display.</summary>
    private void SetTheme(string themeName)
    {
        if (Application.Current is App app)
            app.LoadTheme(themeName);
        _currentTheme = themeName;
        if (SetThemeValue != null)
            SetThemeValue.Text = themeName == "ClassicLight"
                ? Loc.Get("set.theme.classic")
                : Loc.Get("set.theme.darktech");
        if (ThemeBtn != null)
            ThemeBtn.Content = themeName == "ClassicLight" ? "☀" : "◐";
        AddLog("Theme changed to " + themeName);
    }

    // ── Localization ────────────────────────────────────────────────────────
    private void LangToggle_Click(object? sender, RoutedEventArgs e)
    {
        Loc.Toggle();
        ApplyLanguage();
        AddLog("Language: " + (Loc.Lang == "zh" ? "中文" : "English"));
    }

    /// <summary>Rewrites all localized UI chrome for the current language.</summary>
    private void ApplyLanguage()
    {
        // Sidebar
        NavTest.Content     = Loc.Get("nav.test");
        NavIperf3.Content   = Loc.Get("nav.iperf3");
        NavEthr.Content     = Loc.Get("nav.ethr");
        NavWebsite.Content  = Loc.Get("nav.website");
        NavMonitor.Content  = Loc.Get("nav.monitor");
        NavTools.Content    = Loc.Get("nav.tools");
        NavPing.Content     = Loc.Get("nav.ping");
        NavHistory.Content  = Loc.Get("nav.history");
        NavSettings.Content = Loc.Get("nav.settings");
        SideModules.Text    = Loc.Get("side.modules");
        SideSuite.Text      = Loc.Get("side.suite");

        // Title bar
        LangBtn.Content = Loc.Lang == "zh" ? "EN" : "中";
        ThemeBtn.Content = _currentTheme == "ClassicLight" ? "☀" : "◐";

        // Test panel
        QuickTitle.Text   = Loc.Get("panel.quick");
        StartBtn.Content  = _isRunning ? Loc.Get("btn.stop") : Loc.Get("btn.start");
        DlChartTitle.Text = Loc.Get("panel.dl");
        UlChartTitle.Text = Loc.Get("panel.ul");
        ConnTitle.Text    = Loc.Get("panel.conn");
        ScoreTitle.Text   = Loc.Get("panel.score");
        QualityLabel.Text = Loc.Get("quality.run");

        // Bottom row
        LogTitle.Text       = Loc.Get("panel.log");
        ClearLogBtn.Content = Loc.Get("btn.clear");
        SummaryTitle.Text   = Loc.Get("panel.summary");

        // Status bar
        StatusLeft.Text = Loc.Get("status.ready");

        // Settings
        SetEngineTitle.Text     = Loc.Get("set.engine");
        SetEngineLabel.Text     = Loc.Get("set.defengine");
        SetServerLabel.Text     = Loc.Get("set.server");
        SetDurationLabel.Text   = Loc.Get("set.duration");
        SetThreadsLabel.Text    = Loc.Get("set.threads");
        SetEnginesLabel.Text    = Loc.Get("set.engines");
        SetAppearanceTitle.Text = Loc.Get("set.appearance");
        SetGeneralTitle.Text    = Loc.Get("set.general");
        SetTrayCheck.Content    = Loc.Get("set.tray");
        SetAutoStartCheck.Content = Loc.Get("set.autostart");
        SetProxyTitle.Text      = Loc.Get("set.proxy");
        SetProxyHint.Text       = Loc.Get("set.proxy.hint");
        WebsiteProxyHint.Text   = Loc.Get("web.proxy.tip");
        SetThemeLabel.Text      = Loc.Get("set.theme");
        SetThemeValue.Text      = _currentTheme == "ClassicLight"
            ? Loc.Get("set.theme.classic")
            : Loc.Get("set.theme.darktech");
        SetAboutTitle.Text      = Loc.Get("set.about");
        SetSystemTitle.Text     = Loc.Get("set.system");
        SetActionsTitle.Text    = Loc.Get("set.actions");
        CheckUpdateBtn.Content  = Loc.Get("set.checkupdate");
        OpenLogBtn.Content      = Loc.Get("set.openlog");
        RefreshBtn.Content      = Loc.Get("set.refresh");

        // Engine status "Ready" labels
        LocalizeTree(this, "set.ready", "Ready");

        // Reset progress stage + refresh header
        SetProgressStage("stage.idle");
        UpdateProxyState();
        ShowPage(_currentPage);
    }

    /// <summary>Recursively rewrites TextBlocks whose text equals <paramref name="match"/>.</summary>
    private static void LocalizeTree(DependencyObject root, string key, string match)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tb && tb.Text == match)
                tb.Text = Loc.Get(key);
            LocalizeTree(child, key, match);
        }
    }

    // ── Update check (GitHub Releases) ─────────────────────────────────────
    private string? _latestTag;
    private const string ReleasesPageUrl = "https://github.com/narci2018/SpeedTest/releases/latest";

    private static string CurrentAppVersion =>
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(3, 7, 0)).ToString(3);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (manual) { CheckUpdateBtn.Content = "Checking…"; CheckUpdateBtn.IsEnabled = false; }
        string? newTag = null, error = null;
        try
        {
            // Task.Run + WhenAny：DNS 解析在断网环境下可能同步挂起且不可取消，
            // HttpClient.Timeout 对它无效——用外层超时兜底保证 UI 一定能得到结果。
            var check = Task.Run(async () =>
            {
                using var http = new HttpClient();
                http.Timeout = TimeSpan.FromSeconds(8);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SpeedPro-UpdateCheck");
                string json = await http.GetStringAsync("https://api.github.com/repos/narci2018/SpeedTest/releases/latest");
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            });
            var done = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(10)));
            if (done != check) throw new TimeoutException("检查超时（>10s）— 网络不可达或 DNS 挂起");
            newTag = (await check).TrimStart('v');
        }
        catch (Exception ex) { error = ex.Message; }

        string current = CurrentAppVersion;
        if (error == null)
        {
            var latest = new Version(newTag!);
            var cur = new Version(current);
            if (latest > cur)
            {
                _latestTag = newTag;
                StatusRight.Text = "新版本 " + newTag + " 可用 — 点击此处下载";
                StatusRight.Foreground = (Brush)new BrushConverter().ConvertFrom("#4ADE80")!;
                if (manual)
                    MessageBox.Show("发现新版本 " + newTag + "（当前 v" + current + "）。\n点击主窗口状态栏右侧的提示文字即可打开下载页。",
                        "SpeedPro", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                _latestTag = null;
                if (manual)
                    MessageBox.Show("已是最新版本（v" + current + "）。", "SpeedPro",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        else if (manual)
            MessageBox.Show("检查更新失败：" + error + "\n（需要能够访问 api.github.com，或在 设置 → 代理服务器 配置后重试）",
                "SpeedPro", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (manual)
        {
            CheckUpdateBtn.Content = Loc.Get("set.checkupdate");
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        await CheckForUpdatesAsync(manual: true);
        CheckUpdateBtn.IsEnabled = true;
    }

    private void StatusRight_Click(object? sender, MouseButtonEventArgs e)
    {
        if (_latestTag == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ReleasesPageUrl)
            { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenLogDir_Click(object? sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory);
    }

    private void RefreshEngines_Click(object? sender, RoutedEventArgs e)
    {
        AddLog("Checking engines…");
        foreach (var exe in new[] { "speedtest-go.exe", "librespeed-cli.exe", "ethr.exe", "iperf3.exe", "pingularity.exe" })
        {
            bool exists = global::System.IO.File.Exists(GetExePath(exe));
            AddLog($"  {exe}: {(exists ? "Ready" : "NOT FOUND")}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private async Task PollProgress(Stopwatch sw, int dur, int totalTimeoutSec)
    {
        while (sw.Elapsed.TotalSeconds < totalTimeoutSec)
        {
            await Task.Delay(500);
            double elapsed = sw.Elapsed.TotalSeconds;
            int pct = (int)Math.Min(92, (elapsed / totalTimeoutSec) * 88 + 4);
            SetProgress(pct);
            StatusLeft.Text = elapsed < 3 ? Loc.Get("status.connecting")
                : elapsed < dur * 0.6 ? Loc.Get("status.dl")
                : Loc.Get("status.ul");
            AddLog($"  [{elapsed:F1}s] {pct}%");
        }
    }

    private void AddLog(string msg)
    {
        if (LogList == null) return;
        var ts = DateTime.Now.ToString("HH:mm:ss");
        _logs.Insert(0, "[" + ts + "] " + msg);
        if (_logs.Count > 60) _logs.RemoveAt(_logs.Count - 1);
        LogList.AppendText("[" + ts + "] " + msg + "\n");
        LogList.CaretIndex = LogList.Text.Length;
        LogList.ScrollToEnd();
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e) => LogList.Text = "";

    private void CopyLog_Click(object? sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var line in _logs) sb.AppendLine(line);
        Clipboard.SetText(sb.ToString());
    }

    private void LogList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            CopyLog_Click(null, null!);
    }

    private void LogList_ContextMenuOpening(object? sender, ContextMenuEventArgs e) { e.Handled = true; }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        RememberWindowBounds();
        SaveConfig();
        _cts?.Cancel();
        _ethrServer?.Kill();
        _iperf3Server?.Kill();
        _pingularity?.Kill();
        _monitorCts?.Cancel();
        if (_trayIcon != null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
    }

    // ── Tray icon (minimize-to-tray) ───────────────────────────────────────
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private bool MinimizeToTray => SetTrayCheck?.IsChecked == true;

    private void InitTrayIcon()
    {
        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "SpeedPro",
                Visible = false
            };
            try
            {
                // 用应用自身图标（assets/app.ico 已编译为 exe 图标）
                if (System.Environment.ProcessPath != null)
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Environment.ProcessPath);
            }
            catch { _trayIcon.Icon = System.Drawing.SystemIcons.Application; }
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开 SpeedPro", null, (s, e) => RestoreFromTray());
            menu.Items.Add("退出", null, (s, e) => { RestoreFromTray(); Close(); });
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        }
        catch { }
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _winL = Left; _winT = Top; _winW = Width; _winH = Height;
        }
        if (WindowState == WindowState.Minimized && MinimizeToTray) HideToTray();
    }

    // 窗口位置/尺寸记忆（Normal 状态下的最近值）
    private double _winL, _winT, _winW, _winH;

    private void RememberWindowBounds()
    {
        if (WindowState == WindowState.Normal)
        {
            _winL = Left; _winT = Top; _winW = Width; _winH = Height;
        }
    }

    private void RestoreWindowBounds()
    {
        if (_winW >= MinWidth && _winH >= MinHeight)
        {
            Width = _winW; Height = _winH;
            // 钳制到虚拟屏幕范围内，防止窗口落到不可见区域
            Left = Math.Clamp(_winL, SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100);
            Top = Math.Clamp(_winT, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50);
        }
    }

    private void HideToTray()
    {
        if (_trayIcon != null) _trayIcon.Visible = true;
        Hide();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon != null) _trayIcon.Visible = false;
    }

    private static string GetExePath(string name)
    {
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        return global::System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name);
    }

    /// <summary>
    /// Returns "--proxy=URL" if a system proxy is configured, empty string otherwise.
    /// Reads both env vars and Windows registry (Internet Settings).
    /// </summary>
    private static string GetSystemProxyArg()
    {
        // Check environment variables first
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "ALL_PROXY", "all_proxy" })
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(val))
                return " --proxy=" + val.Trim();
        }
        // Fallback: read Windows Internet Settings from registry
        try
        {
            var reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (reg != null)
            {
                var enable = reg.GetValue("ProxyEnable");
                var server = reg.GetValue("ProxyServer") as string;
                var autoUrl = reg.GetValue("AutoConfigURL") as string;
                reg.Close();
                if (enable is int i && i != 0)
                {
                    if (!string.IsNullOrWhiteSpace(server))
                        return " --proxy=http://" + server;
                    if (!string.IsNullOrWhiteSpace(autoUrl))
                        return " --proxy=" + autoUrl;
                }
            }
        }
        catch { }
        return string.Empty;
    }

    /// <summary>
    /// Query ip-api.com for IP geolocation (free, no key required).
    /// Returns empty strings on failure — caller should handle gracefully.
    /// </summary>
    private static async Task<(string ip, string country, string isp)> LookupIpGeolocationAsync(string ip)
    {
        try
        {
            using var http = new HttpClient(new HttpClientHandler { Proxy = null, UseProxy = false });
            http.Timeout = TimeSpan.FromSeconds(5);
            var url = "http://ip-api.com/json/" + ip;
            var resp = await http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var st) && st.GetString() == "success")
                {
                    var c = root.TryGetProperty("country", out var co) ? (co.GetString() ?? "") : "";
                    var i = root.TryGetProperty("isp", out var isEl) ? (isEl.GetString() ?? "") : "";
                    return (ip, c, i);
                }
            }
        }
        catch { }
        return (ip, "", "");
    }
}
