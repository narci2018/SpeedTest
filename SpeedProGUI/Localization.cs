using System.Collections.Generic;

namespace SpeedPro;

/// <summary>
/// 简中/英文本地化。通过键查找当前语言的文本。
/// </summary>
public static class Loc
{
    public static string Lang { get; set; } = "zh";   // "zh" | "en"

    private static readonly Dictionary<string, (string En, string Zh)> _map = new()
    {
        // ── Navigation ──
        ["nav.test"]      = ("Broadband Test",   "宽带测速"),
        ["nav.iperf3"]    = ("Iperf3 Throughput","iperf3 吞吐"),
        ["nav.ethr"]      = ("Ethr Benchmark",   "Ethr 基准"),
        ["nav.website"]   = ("Website Test",     "网站测速"),
        ["nav.monitor"]   = ("Monitor",          "持续监测"),
        ["nav.tools"]     = ("Tools",            "工具箱"),
        ["web.proxy.tip"] = ("Tip: to test a blocked site, set a proxy in Settings → Proxy Server first.", "提示：测被墙网站前，请先在 设置 → 代理服务器 中配置代理。"),
        ["nav.ping"]      = ("Pingularity",      "Pingularity"),
        ["nav.history"]   = ("History",          "历史记录"),
        ["nav.settings"]  = ("Settings",         "设置"),

        // ── Header / buttons ──
        ["title.test"]    = ("Broadband Test",   "宽带测速"),
        ["title.iperf3"]  = ("Iperf3 Throughput Test", "iperf3 吞吐测试"),
        ["title.ethr"]    = ("Ethr Benchmark",   "Ethr 基准测试"),
        ["title.website"] = ("Website Speed Test", "网站测速"),
        ["title.monitor"] = ("Continuous Monitoring", "持续监测"),
        ["title.tools"]   = ("Network Tools",      "网络工具箱"),
        ["title.ping"]    = ("Pingularity Monitor", "Pingularity 监控"),
        ["title.history"] = ("Test History",     "测速历史"),
        ["title.settings"]= ("Settings",         "设置"),
        ["btn.start"]     = ("▶ Start Test",     "▶ 开始测速"),
        ["btn.stop"]      = ("■ Stop",           "■ 停止"),
        ["btn.clear"]     = ("Clear",            "清空"),

        // ── Test panel ──
        ["panel.quick"]   = ("Quick Test",       "快速测速"),
        ["panel.dl"]      = ("Download Speed",   "下载速度"),
        ["panel.ul"]      = ("Upload Speed",     "上传速度"),
        ["panel.conn"]    = ("CONNECTION",       "连接信息"),
        ["panel.score"]   = ("QUALITY SCORE",    "质量评分"),
        ["label.ip"]      = ("IP",               "IP"),
        ["label.isp"]     = ("ISP",              "ISP"),
        ["label.server"]  = ("Server",           "服务器"),
        ["label.country"] = ("Country",          "国家"),
        ["label.dl"]      = ("Download",         "下载"),
        ["label.ul"]      = ("Upload",           "上传"),
        ["label.ping"]    = ("Ping",             "延迟"),
        ["label.jitter"]  = ("Jitter",           "抖动"),
        ["unit.mbps"]     = ("Mbps",             "Mbps"),
        ["unit.ms"]       = ("ms",               "ms"),

        // ── Log / summary / progress ──
        ["panel.log"]     = ("TEST LOG",         "测试日志"),
        ["panel.summary"] = ("RESULTS SUMMARY",  "结果摘要"),
        ["panel.progress"]= ("PROGRESS",         "测速进度"),

        // ── Status ──
        ["status.ready"]  = ("Ready",            "就绪"),
        ["status.testing"]= ("Testing…",         "测速中…"),
        ["status.finding"]= ("Finding server…",  "寻找服务器…"),
        ["status.measuring"]=("Measuring…",      "测量中…"),
        ["status.done"]   = ("Complete",         "完成"),
        ["status.testcomplete"] = ("Test complete", "测速完成"),
        ["status.ping"]   = ("Ping…",            "延迟…"),
        ["status.dl"]     = ("Download…",        "下载…"),
        ["status.ul"]     = ("Upload…",          "上传…"),
        ["status.iperf"]  = ("Iperf3 testing…",  "iperf3 测速中…"),
        ["status.iperfdone"] = ("Iperf3 complete", "iperf3 完成"),
        ["status.ethr"]   = ("Ethr testing…",    "Ethr 测速中…"),
        ["status.ethrdone"]= ("Ethr complete",    "Ethr 完成"),
        ["status.connecting"] = ("Connecting…",  "连接中…"),
        ["status.error"]   = ("Error",             "错误"),

        // ── Charts ──
        ["chart.peak"]    = ("Peak",              "峰值"),

        // ── Progress stages ──
        ["stage.idle"]    = ("Idle",             "空闲"),
        ["stage.finding"] = ("Finding server",   "寻找服务器"),
        ["stage.ping"]    = ("Measuring ping",   "测量延迟"),
        ["stage.download"]= ("Download test",    "下载测试"),
        ["stage.upload"]  = ("Upload test",      "上传测试"),
        ["stage.done"]    = ("Complete",         "完成"),

        // ── Quality ──
        ["quality.run"]   = ("Run a test",       "运行一次测速"),
        ["quality.desc"]  = ("Connection quality", "连接质量"),
        ["quality.outstanding"] = ("Outstanding", "卓越"),
        ["quality.excellent"]   = ("Excellent",   "优秀"),
        ["quality.good"]        = ("Good",        "良好"),
        ["quality.fair"]        = ("Fair",        "一般"),
        ["quality.poor"]        = ("Poor",        "较差"),

        // ── Sidebar ──
        ["side.modules"]  = ("MODULES",          "模块"),
        ["side.suite"]    = ("Network Suite",    "网络套件"),
        ["side.idle"]     = ("Idle",             "空闲"),

        // ── Settings ──
        ["set.engine"]    = ("ENGINE CONFIGURATION", "引擎配置"),
        ["set.defengine"] = ("Default Engine",   "默认引擎"),
        ["set.server"]    = ("Test Server",      "测试服务器"),
        ["set.duration"]  = ("Test Duration (seconds)", "测试时长（秒）"),
        ["set.threads"]   = ("Concurrent Threads", "并发线程"),
        ["set.engines"]   = ("Available Engines", "可用引擎"),
        ["set.ready"]     = ("Ready",            "就绪"),
        ["set.general"]   = ("GENERAL",          "通用"),
        ["set.tray"]      = ("Minimize to tray", "最小化时隐藏到托盘"),
        ["set.autostart"] = ("Start with Windows", "开机自启"),
        ["set.autotheme"] = ("Follow system theme", "主题跟随系统"),
        ["set.appearance"]= ("APPEARANCE",       "外观"),
        ["set.proxy"]     = ("PROXY SERVER",     "代理服务器"),
        ["set.proxy.hint"]= ("Used by the Website Speed Test page — useful for sites that fail on direct connection. Example: http://127.0.0.1:7890", "供“网站测速”使用，适合访问直连失败的网站。示例：http://127.0.0.1:7890"),
        ["set.proxy.state.off"] = ("Not set — Website Test connects directly", "未配置 — 网站测速将直连"),
        ["set.proxy.state.on"]  = ("Active — Website Test connects via this proxy", "已启用 — 网站测速将经此代理连接"),
        ["set.theme"]     = ("Active Theme",     "当前主题"),
        ["set.theme.darktech"] = ("Dark Tech",   "黑科技"),
        ["set.theme.classic"]  = ("Classic Light", "经典浅白"),
        ["set.lang"]      = ("LANGUAGE",         "语言"),
        ["set.about"]     = ("About SpeedPro",   "关于 SpeedPro"),
        ["set.system"]    = ("System",           "系统"),
        ["set.actions"]   = ("Quick Actions",    "快捷操作"),
        ["set.checkupdate"] = ("Check Updates", "检查更新"),
        ["set.openlog"]   = ("Open Log Directory", "打开日志目录"),
        ["set.refresh"]   = ("Refresh Engine Status", "刷新引擎状态"),
    };

    /// <summary>Get the localized string for a key in the current language.</summary>
    public static string Get(string key)
    {
        if (_map.TryGetValue(key, out var v))
            return Lang == "zh" ? v.Zh : v.En;
        return key;
    }

    /// <summary>Toggle between Chinese and English.</summary>
    public static void Toggle() => Lang = Lang == "zh" ? "en" : "zh";
}
