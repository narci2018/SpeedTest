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

    protected override void OnStartup(StartupEventArgs e)
    {
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
