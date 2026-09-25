// ============================================================
// FileLocationViewModel.cs —— 「数据与档案 → 程序与数据源」页
//
// 【这一页回答两个问题】
//   ① "文件都在哪？"       —— 程序（osu / tosu）、日志、两个配置文件
//   ② "怎么连上 tosu？"    —— 监听地址 / 端口 / 等多久算失败 / 试连一次
//
// 【为什么两块并成一页，而不是单开一个「数据源」页】
//   它们回答的是同一类问题："东西在哪、怎么跟它说上话"。
//   拆成两页之后，用户改完 tosu 程序路径还得翻到另一页去填端口 ——
//   而这两个值**永远是一起配的**。
//
// 【数据源那几项**不即时生效**，但也不置灰】
//   地址 / 端口是悬浮窗**启动时**读的（TosuClient 的地址在构造时定死，之后整条连接
//   都跟着它）。真要"改了立刻重连"，就得把常驻连接拆掉重建 ——
//   按用户"偏保守的设计"的取舍，不做这个机制，改成**在界面上说清楚**：
//   保存后提示"重启悬浮窗后生效"。
//   作为补偿，「测试连接」**用界面上当前的值**当场试一次（不要求先保存）——
//   用户改完端口马上就知道对不对
// ============================================================
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

/// <summary>程序与数据源里的一行</summary>
public sealed partial class FileLocationEntryViewModel : ObservableObject
{
    private readonly string _defaultFolderName;
    private readonly bool _isFixed;

    /// <param name="title">显示名。**用常规叫法**，不要靠下面的说明来补救</param>
    /// <param name="fieldPath">配置坐标；空串表示这一项不落配置（固定在 exe 旁）</param>
    /// <param name="defaultFolderName">留空时的默认文件夹名（相对 exe）；空串 = 没有默认、必须用户自己选</param>
    /// <param name="isFixed">固定在 exe 旁、不可改（如 config.json 本身）</param>
    /// <param name="isFile">这一行指的是**文件**（osu 程序）还是**目录**（日志）。
    ///                  浏览时用哪种对话框靠它，界面不去猜"有没有扩展名"。</param>
    public FileLocationEntryViewModel(string title, string fieldPath,
                                      string defaultFolderName = "", bool isFixed = false,
                                      bool isFile = false)
    {
        Title = title;
        FieldPath = fieldPath;
        _defaultFolderName = defaultFolderName;
        _isFixed = isFixed;
        IsFile = isFile;
    }

    public string Title { get; }

    /// <summary>指的是文件还是目录（决定"浏览"该弹哪种对话框）</summary>
    public bool IsFile { get; }

    /// <summary>配置坐标（`数据源.osu路径` 这类）——XAML 绑到 SettingIndex 的附加属性上</summary>
    public string FieldPath { get; }

    /// <summary>能不能改（固定的那两项不可改，界面不显示"浏览/清除"）</summary>
    public bool CanEdit => !_isFixed;

    /// <summary>配置里原样存的值。留空 = 用默认位置。</summary>
    [ObservableProperty] private string _path = "";

    /// <summary>
    /// 实际用的路径 —— **界面显示的是这个**，所以用户不需要理解"留空"是什么意思。
    ///
    /// 三种情况：
    ///   · 填了值 → 就是它
    ///   · 留空、但有默认位置 → 算出默认位置（日志目录属于这种）
    ///   · 留空、且没有默认位置 → **空**（osu / tosu 的路径属于这种：
    ///     它必须由用户自己指，不能瞎猜一个 exe 目录出来）
    /// </summary>
    public string EffectivePath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Path)) return Path;

            return string.IsNullOrEmpty(_defaultFolderName)
                ? ""
                : DirectoryResolver.Resolve(Path, _defaultFolderName);
        }
    }

    partial void OnPathChanged(string value) => OnPropertyChanged(nameof(EffectivePath));
}

public partial class FileLocationViewModel : ObservableObject
{
    private readonly ConfigStore _store;
    private bool _loading;

    /// <summary>
    /// "现在有没有悬浮窗在跑"。
    ///
    /// 做成注入的委托而不是在 VM 里读 <c>App.Overlay</c>：
    /// 与「运行期调整」页同一个理由（《项目结构约定》§四）——
    /// VM 不自己去摸静态成员，那样它就没法在控制台里单独构造。
    /// 它的唯一用途是让"已保存"那句提示说得准（要不要加"重启悬浮窗后生效"）。
    /// </summary>
    private readonly Func<bool> _overlayRunning;

    /// <summary>正在进行的连接测试。用户又点一次时要先取消上一次 —— 否则两个结果会打架</summary>
    private CancellationTokenSource? _probeCts;

    public FileLocationViewModel(ConfigStore store, Func<bool>? overlayRunning = null)
    {
        _store = store;
        _overlayRunning = overlayRunning ?? (() => false);

        Entries = new ObservableCollection<FileLocationEntryViewModel>
        {
            new("osu 程序", "数据源.osu路径", isFile: true),
            new("tosu 程序", "数据源.tosu路径", isFile: true),
            new("日志", "日志.目录", DirectoryResolver.LogsFolderName),

            // 这两项固定在 exe 旁边，不给改也不落配置 —— 只是让用户知道文件叫什么、在哪
            new("配置文件", fieldPath: "", isFixed: true, isFile: true),
            new("软件设置文件", fieldPath: "", isFixed: true, isFile: true),
        };

        ReloadFromConfig();

        _store.DirtyChanged += () =>
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            ResetToDefaultCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<FileLocationEntryViewModel> Entries { get; }

    public bool HasUnsavedChanges => _store.IsDirty;

    public event Action<string>? Notify;

    // ============================================================
    // 数据源（"怎么连"）
    // 属性名与 DataSourceConfig 的 C# 属性名一一对应（中文键名由 [JsonPropertyName] 映射）
    // ============================================================

    /// <summary>tosu 的地址。空 = 用默认的 127.0.0.1（不是错，是"没改过"）</summary>
    [ObservableProperty] private string _ip = DataSourceConfig.DefaultIp;

    /// <summary>
    /// 监听端口 —— **存的是用户打的原文，不是 int**。
    /// </summary>
    [ObservableProperty] private string _portText = DataSourceConfig.DefaultPort.ToString();

    /// <summary>启动等待秒数 —— 同上，存原文</summary>
    [ObservableProperty] private string _waitSecondsText = DataSourceConfig.DefaultStartupWaitSeconds.ToString();

    // ---- 自动启动 / 退出时关闭（★ 2026-09-23 接上，四个开关）----
    //
    // 它们是真的会去启停进程的（`Services/ProcessLauncher.cs`）：先说清楚——
    // 「启动悬浮窗时自动启动」省掉的是每次打歌前"先开 tosu、再开 osu"这两步；
    // 「退出时关闭」是它的配套，否则用户关掉软件之后会留下一堆不知道谁开的后台进程。
    //
    // **只关"自己启动的"那个进程**（决策 23）：用户本来就开着的游戏，我们一个都不碰。

    /// <summary>启动悬浮窗时自动启动 tosu</summary>
    [ObservableProperty] private bool _autoStartTosu;

    /// <summary>启动悬浮窗时自动启动 osu</summary>
    [ObservableProperty] private bool _autoStartOsu;

    /// <summary>退出时关闭 tosu（**只关自己启动的**）</summary>
    [ObservableProperty] private bool _closeTosuOnExit;

    /// <summary>
    /// osu退出时退出本程序
    /// </summary>
    [ObservableProperty] private bool _exitWhenOsuExits;

    // ---- 连接测试的界面状态（**都不落配置**，所以它们一个都不标脏）----

    /// <summary>状态档：idle / testing / ok / warn / error。XAML 用它选灯的颜色</summary>
    [ObservableProperty] private string _connectionStateKind = ConnectionIdle;

    /// <summary>状态那句话</summary>
    [ObservableProperty] private string _connectionStateText = NotTestedYet;

    /// <summary>正在测（按钮要灰掉，不然用户会连点，看到几个结果先后蹦出来）</summary>
    [ObservableProperty] private bool _isTesting;

    private const string ConnectionIdle = "idle";

    /// <summary>
    /// 还没测过的初始那句话。
    /// **不能只写"未测试"** —— 那一行没有标题，用户看不出它是干什么的；
    /// 写全一句"点哪个按钮干什么"，它自己就把用法讲清楚了（省掉一行小字说明）。
    /// </summary>
    private const string NotTestedYet = "还没测试过 —— 点「测试连接」按上面的地址试一次。";

    // ============================================================
    // 两个数字框的解析与校验
    //
    // 实现在 `DataSourceConfig.TryParsePortText` / `TryParseWaitSecondsText` ——
    // **故意不写在这一层**：VM 要 CommunityToolkit、要 ConfigStore，
    // 一旦逻辑落在这里，控制台工程就再也验不到它，
    // 而"空 / 全角数字 / 多余空格 / 越界"这几条分支恰恰最该被钉死。
    // 这里只做转发与给界面用的派生属性。
    // ============================================================

    /// <summary>测试的目标（界面上显示，让用户确认"它到底在试哪个地址"）</summary>
    public string ConnectionTarget
    {
        get
        {
            var ip = string.IsNullOrWhiteSpace(Ip) ? DataSourceConfig.DefaultIp : Ip.Trim();
            return DataSourceConfig.TryParsePortText(PortText, out var port, out _)
                ? $"{ip}:{port}"
                : $"{ip}:?";
        }
    }

    /// <summary>端口这一项哪里不对（对就返回空串）。**边打字边提示**，不等保存</summary>
    public string PortWarning =>
        DataSourceConfig.TryParsePortText(PortText, out _, out var reason) ? "" : reason;

    public bool HasPortWarning => PortWarning.Length > 0;

    /// <summary>等待秒数这一项哪里不对</summary>
    public string WaitWarning =>
        DataSourceConfig.TryParseWaitSecondsText(WaitSecondsText, out _, out var reason) ? "" : reason;

    public bool HasWaitWarning => WaitWarning.Length > 0;

    /// <summary>
    /// 范围提示。**数字写在 VM 里、不写在 XAML 里**（定稿 §8.4 第 2 条：文案集中一处）——
    /// 以后默认端口换了（tosu 改过好几次），改一个常量就够了，不会漏掉界面上那句小字。
    /// </summary>
    public string PortRangeHint =>
        $"范围 {DataSourceConfig.MinPort}~{DataSourceConfig.MaxPort}，tosu 默认 {DataSourceConfig.DefaultPort}";

    public string WaitRangeHint =>
        $"范围 {DataSourceConfig.MinStartupWaitSeconds}~{DataSourceConfig.MaxStartupWaitSeconds} 秒" +
        $"，默认 {DataSourceConfig.DefaultStartupWaitSeconds} 秒";

    // ------------------------------------------------------------

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loading) return;
        if (e.PropertyName == nameof(FileLocationEntryViewModel.Path))
            _store.MarkSettingsDirty();
    }

    // 白名单式标脏：**只有配置字段**才标脏。
    // 反过来说，`ConnectionStateText` / `IsTesting` 这些界面状态怎么变都不会脏 ——
    // 它们本来就不该被保存（否则用户点一次测试连接就多一个"未保存"）。
    partial void OnIpChanged(string value)
    {
        MarkDirty();
        NotifyTargetChanged();
    }

    partial void OnPortTextChanged(string value)
    {
        MarkDirty();
        OnPropertyChanged(nameof(PortWarning));
        OnPropertyChanged(nameof(HasPortWarning));
        NotifyTargetChanged();
    }

    partial void OnWaitSecondsTextChanged(string value)
    {
        MarkDirty();
        OnPropertyChanged(nameof(WaitWarning));
        OnPropertyChanged(nameof(HasWaitWarning));
    }

    partial void OnAutoStartTosuChanged(bool value) => MarkDirty();
    partial void OnAutoStartOsuChanged(bool value) => MarkDirty();
    partial void OnCloseTosuOnExitChanged(bool value) => MarkDirty();
    partial void OnExitWhenOsuExitsChanged(bool value) => MarkDirty();

    partial void OnIsTestingChanged(bool value)
    {
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    private void NotifyTargetChanged() => OnPropertyChanged(nameof(ConnectionTarget));

    private void MarkDirty()
    {
        if (_loading) return;
        _store.MarkSettingsDirty();
    }

    // ============================================================
    // 测试连接
    // ============================================================

    private bool CanTestConnection() => !IsTesting;

    /// <summary>
    /// 按**界面上当前的值**试连一次（**不要求先保存**）。
    ///
    /// 这是有意为之：用户改端口的动机就是想确认"这个端口对不对"，
    /// 逼他先保存再测试，等于让他在"改了不生效"和"存错了"之间二选一。
    /// 而测试本身只读不写，用没保存的值没有任何风险。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        // 又点了一次：上一次的结果已经没人要了，先取消
        var previous = _probeCts;
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        previous?.Cancel();
        previous?.Dispose();

        // 填错了就别去连 —— 报"连不上"会把用户引去查 tosu，
        // 而真正的问题是他框里那个字（体检制度的同一条道理：**别报假警报**）
        if (!DataSourceConfig.TryParsePortText(PortText, out var port, out var portReason))
        {
            ConnectionStateKind = "error";
            ConnectionStateText = portReason;
            return;
        }

        if (!DataSourceConfig.TryParseWaitSecondsText(WaitSecondsText, out var waitSeconds, out var waitReason))
        {
            ConnectionStateKind = "error";
            ConnectionStateText = waitReason;
            return;
        }

        IsTesting = true;
        ConnectionStateKind = "testing";
        ConnectionStateText = $"正在连接 {ConnectionTarget} ...";

        try
        {
            var result = await TosuConnectionProbe.TestAsync(Ip, port, waitSeconds, cts.Token);

            // 期间用户又点了一次 → 这次的结果作废，别去覆盖新的那次
            if (!ReferenceEquals(_probeCts, cts)) return;

            ConnectionStateKind = result.Outcome switch
            {
                TosuProbeOutcome.Success => "ok",
                TosuProbeOutcome.NoGameData => "warn",     // 配置是对的，只是游戏没开
                _ => "error"
            };
            ConnectionStateText = result.Message;

            // 日志里也留一份：出问题时"到底测过没有、结果是什么"要能倒查
            DebugLog.Write($"测试连接（{result.ElapsedMs} 毫秒）：{result.Message}");
        }
        catch (OperationCanceledException)
        {
            // 被后一次测试取代（或者程序在退出）—— 什么都不用说
        }
        catch (Exception ex)
        {
            ConnectionStateKind = "error";
            ConnectionStateText = $"测试失败：{ex.Message}";
            DebugLog.Write("测试连接异常：" + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts)) IsTesting = false;
        }
    }

    // ============================================================
    // 保存 / 放弃 / 恢复默认
    // ============================================================

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Save()
    {
        // 填错了就别存 —— 存下去也是个连不上的值，而用户此刻正看着那句红字提示。
        // （**不静默用默认值替换**：那等于"用户改了、程序没听"，比拒绝保存还难查。）
        if (!DataSourceConfig.TryParsePortText(PortText, out var port, out var portReason))
        {
            Notify?.Invoke("保存不了：" + portReason);
            return;
        }

        if (!DataSourceConfig.TryParseWaitSecondsText(WaitSecondsText, out var waitSeconds, out var waitReason))
        {
            Notify?.Invoke("保存不了：" + waitReason);
            return;
        }

        var dataSource = _store.Settings.DataSource;

        dataSource.OsuPath = Entries[0].Path;
        dataSource.TosuPath = Entries[1].Path;
        _store.Settings.Log.Directory = Entries[2].Path;

        // 地址留空 = 没改过（用默认)
        // 否则"框里是空的、配置里是 127.0.0.1"，用户下次打开会以为没存上。
        if (string.IsNullOrWhiteSpace(Ip))
        {
            Ip = DataSourceConfig.DefaultIp;
            dataSource.Ip = DataSourceConfig.DefaultIp;
        }
        else
        {
            dataSource.Ip = Ip.Trim();

            // 顺手把多余空格从框里也去掉
            // 而那个空格会一直留在配置里（下次复制出去用就带上了）
            if (Ip != dataSource.Ip) Ip = dataSource.Ip;
        }

        dataSource.Port = port;
        dataSource.StartupWaitSeconds = waitSeconds;

        dataSource.AutoStartTosu = AutoStartTosu;
        dataSource.AutoStartOsu = AutoStartOsu;
        dataSource.CloseTosuOnExit = CloseTosuOnExit;
        dataSource.ExitWhenOsuExits = ExitWhenOsuExits;

        try
        {
            _store.SaveSettings();

            // ★ 地址与端口是**启动时**读的（见文件头）——
            // 悬浮窗正跑着的时候必须说清楚"现在还没生效"，否则用户会以为改坏了。
            Notify?.Invoke(_overlayRunning() ? "已保存 —— 重启悬浮窗后生效" : "已保存");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"保存失败：{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Discard()
    {
        _store.ReloadSettings();
        ReloadFromConfig();
        Notify?.Invoke("已放弃改动");
    }

    /// <summary>恢复默认 = 把可编辑的几项清空，数据源回落到出厂值。</summary>
    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void ResetToDefault()
    {
        foreach (var entry in Entries.Where(e => e.CanEdit)) entry.Path = "";

        Ip = DataSourceConfig.DefaultIp;
        PortText = DataSourceConfig.DefaultPort.ToString();
        WaitSecondsText = DataSourceConfig.DefaultStartupWaitSeconds.ToString();

        // 四个开关的默认值全部是"关"（见 DataSourceConfig）——
        // 自动去启停别人的进程、或者自己关掉自己，默认都必须是关的
        AutoStartTosu = false;
        AutoStartOsu = false;
        CloseTosuOnExit = false;
        ExitWhenOsuExits = false;

        Notify?.Invoke("已恢复默认（还要点保存才会生效）");
    }

    /// <summary>清空一行（回到默认位置）</summary>
    [RelayCommand]
    private void ClearEntry(FileLocationEntryViewModel? entry)
    {
        if (entry is null || !entry.CanEdit) return;
        entry.Path = "";
    }

    /// <summary>
    /// 打开所在位置。
    /// **文件**就选中它（`explorer /select,`）、**目录**就打开它 ——
    /// 这样"打开"这个动作在两种行上都符合直觉。
    /// 路径还不存在时先建目录 / 提示，别弹一句"找不到"。
    /// </summary>
    [RelayCommand]
    private void OpenEntry(FileLocationEntryViewModel? entry)
    {
        if (entry is null) return;

        try
        {
            var path = entry.EffectivePath;
            if (string.IsNullOrWhiteSpace(path))
            {
                Notify?.Invoke("这一项还没设置");
                return;
            }

            if (File.Exists(path))
            {
                // 是文件：打开资源管理器并选中它
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }

            // 还不存在：目录就建出来再打开；文件（osu.exe 之类）只提示
            if (Path.HasExtension(path))
            {
                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                    return;
                }

                Notify?.Invoke("这个位置还不存在");
                return;
            }

            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"打不开：{ex.Message}");
        }
    }

    // ------------------------------------------------------------
    private void ReloadFromConfig()
    {
        _loading = true;
        try
        {
            foreach (var entry in Entries) entry.PropertyChanged -= OnEntryChanged;
            foreach (var entry in Entries) entry.PropertyChanged += OnEntryChanged;

            var settings = _store.Settings;

            Entries[0].Path = settings.DataSource.OsuPath;
            Entries[1].Path = settings.DataSource.TosuPath;
            Entries[2].Path = settings.Log.Directory;

            // 数据源：配置里存的就是"真正会用的值"（读盘时已经兜过底），
            // 所以界面直接显示它 —— 不做"空着代表默认"那一套（端口没有"没设过"这个状态）。
            // 两个数字转成文本存进框里：**界面上的原文与配置里的值就此分开**（见 PortText 的说明）。
            Ip = settings.DataSource.Ip;
            PortText = settings.DataSource.Port.ToString();
            WaitSecondsText = settings.DataSource.StartupWaitSeconds.ToString();

            AutoStartTosu = settings.DataSource.AutoStartTosu;
            AutoStartOsu = settings.DataSource.AutoStartOsu;
            CloseTosuOnExit = settings.DataSource.CloseTosuOnExit;
            ExitWhenOsuExits = settings.DataSource.ExitWhenOsuExits;

            // 固定项：直接显示它们真正的位置（不落配置，所以没有"留空"这回事）
            Entries[3].Path = _store.ConfigPath;
            Entries[4].Path = _store.SettingsPath;
        }
        finally
        {
            _loading = false;
        }

        // 换了值之后,刷新检测结果
        ConnectionStateKind = ConnectionIdle;
        ConnectionStateText = NotTestedYet;
    }
}
