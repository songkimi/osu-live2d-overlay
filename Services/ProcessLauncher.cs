// ============================================================
// ProcessLauncher.cs —— 启动 / 关闭 tosu 与 osu 的进程（"归属感"那一套）
//
// 【它解决什么问题】
//   每次打歌前都要自己先开 tosu、再开 osu，然后才开悬浮窗 —— 三步。
//   设置里那两个"启动悬浮窗时自动启动"的开关就是为了省掉这两步。
//   但**自动启动必须配一个反向开关**（"退出时关闭"），否则用户关掉悬浮窗之后
//   会留下一堆"不知道谁开的"后台进程。
//
// 【★ 最要紧的一条规矩：只关"自己启动的"那个进程】（定稿决策 23）
//   用户本来就开着游戏时，我们**绝不能**在他退出这个软件的时候把游戏也关了 ——
//   那是"归属感"问题，不是技术问题。所以这里记住的是**启动出来的那个 Process 对象**，
//   而不是"按进程名去杀"。宁可这次关不掉，也不能关错人。
//
// 【关闭为什么是"先礼貌、后强杀"】
//   osu 与 tosu 是两类完全不同的进程：
//     · osu 有主窗口 → `CloseMainWindow()` 等于用户自己点 X，它会照常保存配置
//     · tosu 是控制台程序 → **没有主窗口**，CloseMainWindow 直接返回 false
//   所以顺序是：先 CloseMainWindow 并等一会儿，**还活着才 Kill**。
//   为什么不直接 Kill：osu 正在退出时会写配置，强杀有丢东西的风险。
//
// 【它为什么在 Services/ 而不是 Logic/】
//   它要起进程、要枚举进程 —— 是 IO。按《项目结构约定》§二，Logic/ 不许有 IO。
//   它也**不引 WPF**，所以"哪些路径不算合法目标""进程名怎么取"这类判断能在控制台里测
//   （见 `.build-verify/TosuSourceCheck`）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace OsuLive2dOverlay;

/// <summary>启动一个程序的结果。**四种"没启动"要分得开** —— 它们的处置完全不同</summary>
public enum LaunchOutcome
{
    /// <summary>启动成功</summary>
    Started,

    /// <summary>已经在跑了 —— **不重复启动**，直接连它就行。这是正常结果，不是错误</summary>
    AlreadyRunning,

    /// <summary>没配路径（开关开着但路径空着）</summary>
    PathMissing,

    /// <summary>配了路径，但那个文件不在（多半是挪走了、或者换了台机器）</summary>
    FileNotFound,

    /// <summary>启动时报错（权限、路径里有非法字符…）</summary>
    Failed
}

/// <summary>一次启动的结果</summary>
/// <param name="Outcome">四种结果见 <see cref="LaunchOutcome"/></param>
/// <param name="Message">给人看的一句话。**成功也带一句**（"已经在运行，直接连"是有用的事实）</param>
public sealed record LaunchResult(LaunchOutcome Outcome, string Message)
{
    /// <summary>结果能不能用（启动成功、或本来就在跑）</summary>
    public bool Ok => Outcome is LaunchOutcome.Started or LaunchOutcome.AlreadyRunning;
}

public sealed class ProcessLauncher
{
    /// <summary>
    /// **自己启动的**那些进程。关闭时只看这一份 —— 这是"归属感"那条规矩的落点。
    /// 键是 exe 的完整路径（忽略大小写，Windows 路径本来就不区分）。
    /// </summary>
    private readonly Dictionary<string, Process> _owned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// osu 的可执行文件名：**stable 与 lazer 都叫 `osu!.exe`**。
    ///
    /// 它是"没配 osu 程序路径"时的兜底 —— 「osu 退出时退出本程序」那个开关
    /// 不该因为用户没填路径就整个失效（那是最典型的"点了没反应"）。
    /// 而这个进程名不是猜的：两代官方客户端的可执行文件确实同名。
    /// </summary>
    public const string DefaultOsuProcessName = "osu!";

    /// <summary>礼貌关闭之后等多久允许强杀</summary>
    private const int GracefulCloseWaitMs = 2000;

    // ============================================================
    // 纯逻辑（能在控制台里测的那一半）
    // ============================================================

    /// <summary>
    /// 从 exe 路径取出进程名（不含扩展名）—— 用来判断"它是不是已经在跑了"。
    ///
    /// **只认 .exe**：脚本（`.cmd`/`.bat`）的"进程名"根本不是文件名
    /// （真正在跑的是 cmd.exe 或它拉起的东西），拿文件名去查一定查不到 → 会重复启动。
    /// 所以非 .exe 一律返回空串，调用方据此**不做"已在运行"判断**（宁可不判断，也别判断错）。
    /// </summary>
    public static string ProcessNameOf(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return "";

        var name = Path.GetFileNameWithoutExtension(exePath.Trim());

        return exePath.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : "";
    }

    /// <summary>这个路径看起来是不是一个"可以启动的程序"（界面上的浏览框只给 .exe，这里是二次确认）</summary>
    public static bool IsExePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    // ============================================================
    // 判断与启动
    // ============================================================

    /// <summary>
    /// 这个程序是不是已经在跑了（**按进程名**）。
    ///
    /// 用的是进程名而不是"我们有没有启动过它" —— 用户自己先开着的 tosu 同样算"在跑"，
    /// 那时我们不该再起一个（端口会冲突，tosu 起第二个会直接失败）。
    /// </summary>
    public bool IsRunning(string? exePath)
    {
        var name = ProcessNameOf(exePath);
        return name.Length > 0 && IsProcessRunning(name);
    }

    /// <summary>
    /// **osu 在不在跑**（★ 2026-09-23，给「osu 退出时退出本程序」用）。
    ///
    /// 配了路径就按那个 exe 的进程名查；没配就用 <see cref="DefaultOsuProcessName"/> 兜底 ——
    /// 这个开关**不该要求用户先填路径**才能工作。
    /// </summary>
    public bool IsOsuRunning(string? osuPath)
    {
        var name = ProcessNameOf(osuPath);
        return IsProcessRunning(name.Length > 0 ? name : DefaultOsuProcessName);
    }

    /// <summary>按进程名判断在不在跑。名字为空一律返回 false（**不猜**）。</summary>
    public bool IsProcessRunning(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        try
        {
            var processes = Process.GetProcessesByName(processName.Trim());
            var any = processes.Length > 0;

            foreach (var process in processes) process.Dispose();   // 句柄要还回去，别攒着

            return any;
        }
        catch
        {
            // 枚举进程失败（权限之类）当作"没在跑"。
            // 注意这里**不能**反过来当作"在跑"：那会让"osu 退出时退出程序"
            // 因为一次查询失败就永远不触发（静默失效）。
            // 而当作"没在跑"的代价只是本轮不触发，2 秒后会再查一次。
            return false;
        }
    }

    /// <summary>
    /// 启动一个程序。**已经在跑就不会重复启动**（返回 <see cref="LaunchOutcome.AlreadyRunning"/>）。
    ///
    /// 启动成功时把它记进"自己启动的"那一份 —— 退出时只关这一份。
    /// </summary>
    public LaunchResult Launch(string? exePath)
    {
        var path = (exePath ?? "").Trim();

        if (path.Length == 0) return new(LaunchOutcome.PathMissing, "没填程序路径");

        if (!File.Exists(path)) return new(LaunchOutcome.FileNotFound, $"找不到文件：{path}");

        if (IsRunning(path)) return new(LaunchOutcome.AlreadyRunning, "它已经在运行了，直接连它");

        try
        {
            // WorkingDirectory 必须给：tosu 是"在哪启动就把配置和数据放哪"的那类程序，
            // 不指定的话它会用当前工作目录（对 WPF 程序来说是 exe 旁边，通常也对，
            // 但万一用户用的是快捷方式那种布局就会跑到别处去写文件 —— 指定它没有坏处）
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory
            };

            var process = Process.Start(startInfo);

            if (process is null)
                return new(LaunchOutcome.Failed, "启动没有报错，但没有拿到进程（系统没让起来）");

            _owned[path] = process;

            // 不是 .exe（用户可能配了 .cmd / .bat）也能启动，但**没法判断它是否已经在跑**
            // （见 ProcessNameOf 的说明）→ 把这件事**放进返回值**、而不是在这里写日志：
            // 这一层不去够 Infrastructure 的日志门面，调用方本来就会把结果记下来。
            return new(LaunchOutcome.Started, IsExePath(path)
                ? "已启动"
                : $"已启动（它不是 .exe，所以不会判断「是否已经在跑」，可能会重复启动）");
        }
        catch (Exception ex)
        {
            return new(LaunchOutcome.Failed, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 关掉**自己启动的**那些进程（退出程序时调用）。
    ///
    /// 返回的是**给日志看的**逐条结果 —— 关不掉要说出来，
    /// 不然"退出时关闭"这个开关会变成"静默不生效"。
    /// </summary>
    public IReadOnlyList<string> CloseOwned()
    {
        var lines = new List<string>();

        foreach (var (path, process) in _owned)
        {
            var name = Path.GetFileName(path);

            try
            {
                if (process.HasExited)
                {
                    lines.Add($"{name}：早就退出了，不用管");
                    continue;
                }

                // ① 先礼貌：等于用户自己点那个窗口的 X。osu 会照常保存配置。
                var asked = process.CloseMainWindow();

                if (asked && process.WaitForExit(GracefulCloseWaitMs))
                {
                    lines.Add($"{name}：已关闭");
                    continue;
                }

                // ② 还活着才强杀。走到这里多半是两种：tosu（控制台程序，没有主窗口，
                //    CloseMainWindow 必然失败）或者 osu 卡在退出流程里。
                process.Kill(entireProcessTree: true);
                process.WaitForExit(GracefulCloseWaitMs);

                lines.Add(asked
                    ? $"{name}：礼貌关闭没反应，已强制结束"
                    : $"{name}：没有主窗口（控制台程序），已结束进程");
            }
            catch (Exception ex)
            {
                // 关不掉要留痕：这个开关的承诺是"退出时关闭"，做不到就得让人看见
                lines.Add($"{name}：关闭失败 —— {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        _owned.Clear();
        return lines;
    }

    /// <summary>自己启动过几个还记着的进程（给界面/日志看，不参与判断）</summary>
    public int OwnedCount => _owned.Count;
}
