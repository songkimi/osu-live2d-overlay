// ============================================================
// App.xaml.cs —— 应用入口：开机的三件事
//
// 【为什么 App 要有点逻辑了】
//   以前它是空的（StartupUri 直接开悬浮窗）。现在配置界面是主窗口，
//   而"先读配置、再按配置决定界面长什么样"必须**在窗口出现之前**做完 ——
//   否则用户会先看到浅色界面闪一下、再跳成深色。
//
// 【两个配置文件，别搞混】（2026-09-20 拆分；同日加多档案）
//   · `profiles\<档案名>.json` —— **配置档案**：模型 / 规则 / 语音 / 界面感知（按角色切换）
//   · `settings.json`          —— **软件设置**：外观 / 字体 / 数据源 / 热键 / 日志（全局唯一）
//   启动时两个都读；"当前是哪个档案"记在 settings.json 里，
//   路径规则全部由 `ProfileLocator` 说了算（悬浮窗那边也问它，不再各拼各的）。
//   `ConfigStore` 负责在 settings.json 缺失时**从旧 config.json 迁移一次**。
//
// 【启动顺序】
//   ① 建 ConfigStore 并读盘（读不到/读坏了都拿默认值，不会崩）
//   ② 把「外观 / 字体 / 字号」应用到资源字典（定稿 §1.1 的"即时"三项）
//      —— 但注意：**HC 控件的换肤这一次不会生效**，因为主窗口还没被创建；
//         主窗口 `Loaded` 时会再调一次补上（见 MainWindow.xaml.cs）
//   ③ 交给 App.xaml 的 StartupUri 去开 MainWindow
// ============================================================
using System.IO;
using System.Threading;
using System.Windows;

namespace OsuLive2dOverlay;

public partial class App : Application
{
    /// <summary>
    /// 全局唯一的配置门面。
    /// 做成 <c>static</c> 是因为主窗口、悬浮窗、体检、日志都要用它，
    /// 而这一版还没有依赖注入容器（页面不多，等真的乱了再引）。
    /// </summary>
    public static ConfigStore Config { get; private set; } = null!;

    /// <summary>
    /// 「运行期调整」攒下来的窗口状态 —— **全程序共用一份**。
    ///
    /// 为什么放在这里、而不是让悬浮窗自己拿着（2026-09-20）：
    /// 那一层现在有**两个**用户 —— 悬浮窗关闭时问一次要不要保存，
    /// 以及设置界面的「数据与档案 → 运行期调整」页。
    /// 放在窗口实例里的话，设置页根本看不见它，那一页就只能是个空壳
    /// （和当初那个"目录.角色分布"一个下场：名字很响，背后没人读）。
    ///
    /// 顺带解决一件事：用户在悬浮窗退出时选了"不保存"，调整**不会当场蒸发**——
    /// 它还在这一层里，设置页仍然看得见、还能存下来。真正的丢弃点是退出整个程序。
    /// </summary>
    public static PendingWindowAdjustments WindowAdjustments { get; } = new();

    /// <summary>
    /// 正在跑的悬浮窗（没在跑就是 null），由 MainWindow 在启停时设置。
    ///
    /// 设置界面需要它来回答"改了要不要让悬浮窗当场反应"：
    ///   · 运行期调整页：点了「放弃」要让窗口**当场摆回**已保存的位置
    ///   · 运行期调整页：还要如实告诉用户现在到底有没有悬浮窗在跑
    /// 没有它，用户改了东西只能重启悬浮窗才生效 —— 那正是"改了不生效"。
    /// </summary>
    public static OverlayWindow? Overlay { get; set; }

    /// <summary>
    /// 启动 / 关闭 tosu 与 osu 的进程管理器 —— **全程序共用一份**（★ 2026-09-23）。
    ///
    /// 为什么要共用：**启动**发生在「启动悬浮窗」那一刻（`MainWindow`），
    /// 而**关闭**发生在程序退出时（这里）。
    /// 如果各拿一份，关的那一份里就永远是空的 ——
    /// "退出时关闭"会变成一个**看起来有、其实没人执行**的开关
    /// （同 `目录.角色分布`、`位置调整后自动保存` 那一类问题，见定稿决策 58）。
    ///
    /// 它还装着那条最要紧的规矩：**只关"自己启动的"进程**（决策 23）。
    /// </summary>
    public static ProcessLauncher Processes { get; } = new();

    // ============================================================
    // 单实例（★ 2026-09-24）
    //
    // 它有两个作用，第二个才是重点：
    //   ① 重复双击图标不会开出第二个程序（本来就没有保护）
    //   ② **「关闭时最小化到托盘」的兜底** —— 窗口藏起来之后，
    //      用户再双击一次图标就能把它叫回来，完全不需要知道托盘图标在哪
    //
    // 为什么兜底非有不可：Windows（尤其 Win11）会把新注册的托盘图标放进任务栏的
    // 「隐藏的图标」面板里，**用户默认看不见它**。那时他点 X 把窗口藏了，
    // 就真的没有任何入口了 —— 实测发生过一次，最后只能去任务管理器结束进程。
    // ============================================================

    /// <summary>互斥体名：用了 `Local\` 前缀，只在本登录会话内唯一（多用户登录时互不干扰）</summary>
    private const string InstanceMutexName = @"Local\osu-live2d-overlay.instance";

    /// <summary>唤起通道名（第二个实例通过它叫第一个把窗口显示出来）</summary>
    private const string ShowWindowSignalName = @"Local\osu-live2d-overlay.show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showWindowSignal;

    /// <summary>
    /// 抢单实例。返回 <c>false</c> = 已经有一个在跑，**本次启动应当直接结束**。
    ///
    /// 任何一步出错都**照常启动** —— 单实例机制坏了绝不能让程序起不来，
    /// 大不了允许开第二个（那是小问题，起不来是大问题）。
    /// </summary>
    private bool TryBecomeSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);

            if (isFirst)
            {
                ListenForSecondInstance();
                return true;
            }

            // 已经有一个在跑 → 叫它把配置界面显示出来，然后自己走人
            if (EventWaitHandle.TryOpenExisting(ShowWindowSignalName, out var signal))
            {
                signal.Set();
                signal.Dispose();
            }
            else
            {
                // 通道还没建好（对方刚启动的那一瞬间）→ 至少别开出第二个窗口
                Console.WriteLine("osu-live2d-overlay 已经在运行了。");
            }

            Shutdown();
            return false;
        }
        catch
        {
            return true;        // 出错就当自己是第一个
        }
    }

    /// <summary>
    /// 起一条后台线程等"第二个实例"来敲门。
    /// 它不碰界面 —— 收到信号后一律丢回 UI 线程去处理。
    /// </summary>
    private void ListenForSecondInstance()
    {
        try
        {
            _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowSignalName);

            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        _showWindowSignal.WaitOne();
                    }
                    catch
                    {
                        return;     // 程序退出时通道被释放，正常收尾
                    }

                    Dispatcher.Invoke(() =>
                    {
                        DebugLog.Write("检测到又一次启动 → 把配置界面叫回来");

                        // 启动早期窗口还没建好时这一下会落空。罕见，而且用户再点一次就有 ——
                        // 不为此加一套排队机制（那才是真的复杂）。
                        if (MainWindow is MainWindow main) main.ShowConfigWindow();
                    });
                }
            })
            { IsBackground = true, Name = "单实例唤起" };

            thread.Start();
        }
        catch
        {
            // 通道建不起来不影响启动，只是没了这个兜底
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ★ 2026-09-24：单实例 —— 这同时是「关闭时最小化到托盘」**唯一可靠的兜底**。
        //
        // 为什么非有不可（用户实测踩到过）：Windows 会把新注册的托盘图标放进任务栏的
        // 「隐藏的图标」面板里（Win11 默认就是这样），用户可能根本看不到它。
        // 那时他再点一次 X（窗口藏起来），就真的**没有任何入口**能把它叫回来了 ——
        // 实测那次只能去任务管理器结束进程。
        //
        // 有了它，用户的出路就非常自然：**再双击一次桌面图标**，
        // 已经在跑的那个实例会把窗口显示出来，第二个实例自己退出。
        // （顺带也解决了"重复双击会开两个程序"这个隐患。）
        if (!TryBecomeSingleInstance()) return;

        // 三个文件都在 exe 旁边：settings.json + profiles\<档案名>.json
        // （档案目录名/文件名规则只在 ProfileLocator 里写一份 —— 悬浮窗保存窗口位置时也问它）
        var dir = System.AppContext.BaseDirectory;
        var settingsPath = Path.Combine(dir, "settings.json");
        var legacyConfig = Path.Combine(dir, ProfileLocator.LegacyFileName);

        Config = new ConfigStore(settingsPath, dir, legacyConfig);

        Config.Load();

        // 日志：内存里总是留最近若干条；只有调试模式才写文件。
        // 日志根目录用 `目录.日志`，没配就用 exe 旁边的 logs\（便携定位，用户找得到）。
        var settings = Config.Settings;
        DebugLog.Start(settings.DebugMode, settings.Log.Directory);

        // 清理过期日志（启动时做一次就够，不用定时器）——
        // 它删的是**整个月份文件夹**，所以"保留 N 个月"这件事才有明确粒度
        // （原来写在一个文件里的时候，这个功能根本没法实现）。
        DebugLog.Cleanup(settings.Log.RetainMonths);

        // 把"即时"三项先应用上：这样主窗口一出现就已经是对的主题与字号，
        // 不会先亮一下浅色再跳深色。
        var general = settings.General;
        ThemeApplier.Apply(general.Appearance, general.Font, general.FontSizePoints);

        // ★ 2026-09-24：开机自启的登记项**每次启动都幂等地刷一遍**。
        //
        // 为什么：用户可能把程序挪到别的目录（换个盘、解压到别处）。
        // 注册表里那条命令还指着旧路径，于是自启在某次搬家之后就**悄悄失效了** ——
        // 而它失效的时机是"下次开机"，用户根本不会把它和搬家联系起来。
        //
        // 只在开关开着时写：**关着时一个字都不动**（免得把用户在别处加的登记误删）。
        if (general.AutoStart)
        {
            var exe = AutoStartRegistration.CurrentExePath;

            if (AutoStartRegistration.Enable(exe))
                DebugLog.Write($"开机自启已登记：{exe}");
            else
                DebugLog.Write("开机自启：写注册表失败 —— 可能被安全软件或组策略拦了");
        }
    }

    /// <summary>
    /// 程序退出时：按设置关掉**自己启动的** tosu（★ 2026-09-23）。    ///
    /// 【为什么放在这里，而不是 MainWindow 的关闭事件】
    ///   配置界面关掉不等于程序退出 —— 悬浮窗还开着时程序还在跑
    ///   （`ShutdownMode` 是默认的"最后一个窗口关闭才退出"）。
    ///   而 `OnExit` 是**无论哪个窗口导致的退出**都会走到的那一个点，
    ///   所以"退出时关闭"必须挂在这里，挂在窗口上会漏。
    ///   退出也可能是「osu 退出时退出本程序」触发的 —— 那条路同样经过这里。
    ///
    /// 【★ 现在只剩 tosu 一个了】
    ///   原来还有一个「退出时关闭 osu」，2026-09-23 用户把它换成了
    ///   「osu 退出时退出本程序」（见 `DataSourceConfig.ExitWhenOsuExits`）——
    ///   方向反过来：不是"我关软件时把游戏也关了"，而是"你关了游戏我就收摊"。
    ///   所以这里只判断 tosu；osu 的进程**我们一个都不主动关**。
    ///
    /// 【只关自己启动的】（决策 23）
    ///   用户自己先开着的 tosu **一个都不碰** —— 那是归属感问题。
    ///   关不掉的会逐条写进日志：这个开关的承诺是"退出时关闭"，
    ///   做不到就得让人看得见（不然又成了静默失效）。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        var settings = Config.Settings.DataSource;

        if (settings.CloseTosuOnExit)
        {
            foreach (var line in Processes.CloseOwned())
                DebugLog.Write("退出时关闭：" + line);
        }
        else if (Processes.OwnedCount > 0)
        {
            // 开关没开 → 不动它们（这是默认行为）。但要说一句"我启动过、没关"，
            // 免得用户以为程序把它们关掉了，回头找不到自己开着的 tosu
            DebugLog.Write($"退出：本次启动过 {Processes.OwnedCount} 个程序，" +
                           "但「退出时关闭 tosu」没开，所以留着不动");
        }

        // 单实例的通道收掉（进程退出本来也会释放，显式来一下更干净）
        try { _showWindowSignal?.Dispose(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }

        base.OnExit(e);
    }
}
