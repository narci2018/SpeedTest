# SpeedPro - 专业网络测速工具

基于全网 GitHub 开源测速软件深度研究构建的专业级网络测速软件。

## 研究来源

深入研究了以下核心开源项目：

| 项目 | 语言 | 核心算法 |
|------|------|----------|
| [LibreSpeed](https://github.com/librespeed/speedtest) | JS/Go/Rust | XHR+WebWorker, chunked TCP, trimean过滤 |
| [speedtest-go](https://github.com/showwin/speedtest-go) | Go | Ookla API调用, 多服务器选择 |
| [cli-speedtest](https://github.com/nazakun021/cli-speedtest) | Rust | 生产级CLI, JSON输出, 自我更新 |
| [cumulus13/speedtest](https://github.com/cumulus13/speedtest) | Rust | 纯TCP测量, 低内存占用 |
| [sivel/speedtest-cli](https://github.com/sivel/speedtest-cli) | Python | Ookla XML解析, 经典实现 |
| [librespeed/speedtest-cli](https://github.com/librespeed/speedtest-cli) | Go | ICMP ping+HTTP download/upload |
| [Ookla Methodology](https://www.speedtest.net/help/guides/how-does-speedtest-measure-my-network-speeds) | 专有 | TCP多连接, trimean统计过滤 |
| [M-Lab NDT7](https://github.com/m-lab/ndt7) | Go | TCP-based, AppInfo/TCPInfo测量 |

## 核心技术

### 1. 测速准确度
- **Trimean统计过滤**：丢弃最快10%和最慢30%的样本，取 (Q1+2×Median+Q3)/4
- **多连接并发**：默认8路TCP连接，避免单连接窗口限制
- **Warmup预热**：2秒TCP慢启动预热，消除initial burst干扰
- **4Hz采样**：每250ms记录一次瞬时速率，平滑噪声

### 2. 跨架构支持
- .NET 8 跨平台框架
- **x64** 和 **ARM64** 双架构编译
- 单文件部署（非自包含，依赖系统 .NET Runtime）

### 3. 测量维度
| 指标 | 方法 |
|------|------|
| 下载速度 | Cloudflare CDN 多连接TCP下载 |
| 上传速度 | Cloudflare POST上传测试 |
| Ping延迟 | HTTP HEAD请求(1.1.1.1, cloudflare.com) |
| Jitter | 连续ping差值的平均绝对偏差 |
| IP/ISP | ipinfo.io + Cloudflare trace双源 |
| 质量评分 | 0-100综合评分（DL/UL/Ping/Jitter加权） |

## 文件结构

```
SpeedPro/
├── Models/
│   └── TestModels.cs      # 数据模型 (TestMetrics, TestConfiguration)
├── Core/
│   └── SpeedTestEngine.cs # 核心测量引擎
├── Services/
│   └── TestHistoryService.cs # 历史记录持久化
├── UI/
│   ├── Controls/
│   │   ├── GaugeControls.cs  # 圆形仪表 + 实时折线图
│   │   └── ColorExtensions.cs
│   └── MainForm.cs           # 主窗口 (三标签页)
├── Program.cs              # 入口
├── SpeedPro.csproj         # 项目配置
└── app.manifest            # Windows DPI/manifest
```

## 使用方法

### 运行
```bash
# x64
.\publish\SpeedPro.exe

# ARM64 (Surface Pro X等)
.\publish-arm64\SpeedPro.exe
```

### GUI 功能
- **Test 标签**：运行测速，实时仪表+折线图
- **History 标签**：查看历史测试记录
- **Settings 标签**：配置测试参数

### 快捷键
- `F5` 刷新（可添加）
- 点击 "Start Test" 开始
- 点击 "Cancel" 取消

## 技术对比

| 特性 | SpeedPro | speedtest-cli | LibreSpeed CLI |
|------|----------|---------------|----------------|
| 统计过滤 | ✅ Trimean | ❌ 简单平均 | ❌ 简单平均 |
| 并发连接 | ✅ 8路 | ❌ 单连接 | ✅ 可配置 |
| 实时图表 | ✅ | ❌ | ❌ |
| GUI | ✅ WinForms | ❌ CLI | ❌ CLI |
| ARM64 | ✅ | ✅ | ✅ |
| 质量评分 | ✅ 0-100 | ❌ | ❌ |

## 构建

```bash
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 -o ./publish
dotnet publish -c Release -r win-arm64 -o ./publish-arm64
```

## License

MIT
