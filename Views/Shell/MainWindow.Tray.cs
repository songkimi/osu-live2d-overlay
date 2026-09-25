// ============================================================
// MainWindow.Tray.cs —— 托盘图标（★ 2026-09-24）
//
// 【为什么放在 MainWindow 而不是单开一个服务】
//   托盘菜单要干的事，**全都是 MainWindow 已经在干的事**：
//   显示配置界面、启动/停止悬浮窗、退出程序。
//   抽成一个"托盘服务"就得把这些动作以委托的形式再传进去一遍 ——
//   多一层间接，还多一份"两边状态不一致"的机会。
//   拆成 partial 文件只是为了别让主文件继续变长。
//
// 【★ 为什么是 WinForms 的 NotifyIcon，而不是 HandyControl 那个】
//   一开始用的是 `HandyControl.Controls.NotifyIcon`（它不依赖 WinForms，自己 P/Invoke
//   `Shell_NotifyIcon`），但**用户实测托盘里就是不出现**。
//   于是写了 `.build-verify/TrayProbe` 去查，结论是：
//     · `_added = true`        → 内部认为"NIM_ADD 成功了"
//     · 图标句柄导出来是 24×24、452 个非透明像素 → 图标本身**有效**，不是空图
//     · 放进窗口、先 Collapsed 再 Visible 的写法也是 `_added = true`
//   也就是**它那边一切"正常"，系统里却什么都没有，而且没有可查的状态能说明为什么**。
//
//   托盘图标看不见对一个托盘程序是致命的 —— 窗口一缩进去就没有任何入口了
//   （用户实测只能去任务管理器结束进程）。所以换成 .NET 官方的实现：
//   行为确定、社区验证了二十年、出问题至少有地方可查。
//
// 【★ WinForms + WPF 混用的两个注意点】
//   ① `using` 别名：`MessageBox` / `Application` / `ContextMenu` 这些名字**两边都有**，
//      直接 using 会一片歧义。所以统一写 `WinForms.` 前缀。
//   ② `NotifyIcon` 的菜单是 WinForms 的 `ContextMenuStrip`（系统原生外观）——
//      它和 WPF 的 ContextMenu 不是一回事，别混着写。
//
// 【托盘图标关掉时，"关闭时最小化到托盘"也必须一起关掉】
//   这一条在 GeneralViewModel 里联动，理由见那里：托盘关着 + 最小化开着，
//   点 X 会让窗口消失而且**没有任何入口能把它叫回来** —— 程序变成看不见的幽灵进程。
//   这里是第二道防线：真的到了关闭那一刻，还会再确认一次托盘是否可用。
// ============================================================
using System;
using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace OsuLive2dOverlay;

public partial class MainWindow
{
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _trayOverlayItem;

    /// <summary>
    /// 建托盘图标与菜单（窗口 Loaded 时调一次）。
    ///
    /// **一律在代码里建，不写进 XAML**：WinForms 的控件放进 WPF 的 XAML 里
    /// 需要 WindowsFormsHost 那一套（还有 airspace 的老问题），而托盘图标根本不需要
    /// 参与布局 —— 它是"没有窗口的控件"，代码里 new 一个最干净。
    /// </summary>
    private void InitTray()
    {
        var menu = new WinForms.ContextMenuStrip();

        var showItem = new WinForms.ToolStripMenuItem("显示配置界面");
        showItem.Click += (_, _) => ShowConfigWindow();
        menu.Items.Add(showItem);

        // 这一项的文案**跟着状态走**（见 UpdateOverlayState）。
        // 对着一份写着"启动"、点下去却什么都没发生的菜单发愣，
        // 是托盘菜单最常见的糟糕体验。
        _trayOverlayItem = new WinForms.ToolStripMenuItem("启动悬浮窗");
        _trayOverlayItem.Click += (_, _) =>
        {
            if (_overlay is null) StartOverlay();
            else StopOverlay();
        };
        menu.Items.Add(_trayOverlayItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication(askedByUser: true);
        menu.Items.Add(exitItem);

        _tray = new WinForms.NotifyIcon
        {
            // 悬停提示。**最长 63 个字符**，超了会被系统截断
            Text = "osu-live2d-overlay",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = menu,
            Visible = false        // 真正的显示由 ApplyTrayVisibility 按配置决定
        };

        // 双击托盘图标 = 显示配置界面（托盘最通行的用法，不这么做用户会去双击试）
        _tray.MouseDoubleClick += (_, _) => ShowConfigWindow();
    }

    /// <summary>
    /// 读托盘用的图标。
    ///
    /// **失败也一定要给一个图标**：托盘整套机制都建立在"有一个图标"之上，
    /// 拿不到图标就等于整个功能失效（而那时窗口还能被缩进去 —— 正是最坏的组合）。
    /// 所以兜底用系统自带的应用图标，宁可丑一点，也不能没有。
    /// </summary>
    private static Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/olo.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;

            if (stream is not null)
            {
                using (stream) return new Icon(stream);
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("托盘图标加载失败，改用系统默认图标：" + ex.Message);
        }

        return SystemIcons.Application;
    }

    /// <summary>
    /// 按配置把托盘图标显示或隐藏。
    /// **由设置页那个开关直接调用**（`GeneralView` 收到 VM 的事件后转过来）——
    /// 所以用户勾一下就能当场看到图标出现，不用先点保存。
    /// </summary>
    public void ApplyTrayVisibility()
    {
        if (_tray is null) return;

        var show = App.Config.Settings.General.TrayIcon;
        _tray.Visible = show;

        DebugLog.Write(show ? "托盘图标：已显示" : "托盘图标：已隐藏");
    }

    /// <summary>
    /// 把配置界面从"最小化 / 藏到托盘"里叫回来。
    /// 三句都不能少：`Show()` 负责从隐藏恢复、`WindowState` 负责从最小化恢复、
    /// `Activate()` 负责把它推到最前面（不然它只是出现在任务栏里闪）。
    /// </summary>
    public void ShowConfigWindow()
    {
        if (!IsVisible) Show();

        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 真正退出程序（托盘菜单、以及别的"明确要退出"的入口都走这里）。
    /// </summary>
    /// <param name="askedByUser">
    /// 是不是用户明确点的"退出"。目前只用于日志区分 ——
    /// 两条路都不该被 `OnClosing` 拦下来（那正是 `_exiting` 的作用）。
    /// </param>
    public void ExitApplication(bool askedByUser = false)
    {
        if (_exiting) return;

        _exiting = true;
        DebugLog.Write(askedByUser ? "退出程序（用户从托盘菜单点的）" : "退出程序");
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// 窗口真的关掉了 —— 把托盘图标摘掉。
    /// 不摘的话它会一直留在通知区域里，直到用户把鼠标划过它才由系统清掉。
    /// </summary>
    private void DisposeTray()
    {
        if (_tray is null) return;

        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
    }
}
