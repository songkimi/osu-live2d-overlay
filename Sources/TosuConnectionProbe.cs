// ============================================================
// TosuConnectionProbe.cs —— 「测试连接」：按界面上的地址试连一次，把结果说成人话
//
// 【它解决什么问题】
//   数据源的地址与端口是用户唯一能改错、而且**改错了不会报错**的东西：
//   端口从 24050 改成别的，程序的表现只是"角色不动了"，日志里也只有一句"连接失败"。
//   用户没法自己判断"是 tosu 没开、端口不对、还是 osu 没在跑"。
//
//   所以这一块必须有"点一下试一次"的能力，而且**要能分清三种结果**：
//     · 连不上            → tosu 没运行 / 地址或端口不对
//     · 连上了但没数据    → tosu 活着，但 osu 没在跑（**这是常态，不是错误**）
//     · 连上且收到了数据  → 整条链是通的
//   只报"成功/失败"是不够的：最常见的其实是第二种，而它恰恰说明配置是对的。
//
// 【它为什么在 Sources/ 而不是 Logic/】
//   《项目结构约定》§三 的判据是"这段代码需要 IO 吗" —— 它要连网络，所以不是纯逻辑。
//   它是"一种数据来源的一次尝试"，和 TosuClient 是同一层的东西，放一起。
//   也正因为有 IO，它**没法进控制台测**（要真有一台 tosu 才能测）——
//   所以凡是能拿出来的判断（地址怎么拼、端口合不合法、结果怎么说）都留在外面：
//   拼装与兜底在 `DataSourceConfig`，那部分有验证工程盯着。
//
// 【它和 TosuClient 的区别】
//   TosuClient 是**常驻**的：一直连、断了自动重连、持续推数据给状态机。
//   这个是**一次性**的：连上、拿一包数据、立刻断开 —— 它只回答"现在能不能连上"，
//   不参与任何运行期状态。所以两者各写各的连接代码（共用的是地址拼装那一个函数），
//   不为了"少写二十行"把它塞进 TosuClient 里 —— 那会让常驻的那条路多出一堆一次性分支。
// ============================================================
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OsuLive2dOverlay;

/// <summary>一次连接测试的结果。**三种都要能说清楚**（见文件头）</summary>
public enum TosuProbeOutcome
{
    /// <summary>连上了，而且收到了能用的游戏数据 —— 整条链是通的</summary>
    Success,

    /// <summary>连上了 tosu，但等到超时也没收到游戏数据。**最常见、而且通常不是错误**：osu 没在跑</summary>
    NoGameData,

    /// <summary>连不上：tosu 没运行、地址或端口不对、被防火墙挡住</summary>
    Unreachable,

    /// <summary>压根没去连：端口越界这类输入问题。**不当成"连不上"** —— 那是自己的配置错了，不是对方不在</summary>
    InvalidInput
}

/// <summary>
/// 一次连接测试的结论。
/// </summary>
/// <param name="Outcome">三种结果（见 <see cref="TosuProbeOutcome"/>）</param>
/// <param name="Message">**给人看的一句话**：发生了什么 + 最可能的原因。界面直接显示它</param>
/// <param name="ElapsedMs">花了多久（毫秒）。失败时它就是"等了多少秒"，用户据此判断要不要调大等待秒数</param>
public sealed record TosuProbeResult(TosuProbeOutcome Outcome, string Message, int ElapsedMs)
{
    /// <summary>收到数据了没有（界面用它决定灯是什么颜色）</summary>
    public bool Ok => Outcome == TosuProbeOutcome.Success;
}

public static class TosuConnectionProbe
{
    /// <summary>
    /// 按给定的地址与端口试连一次，等第一包**能用**的数据。
    ///
    /// 【为什么是"收到一包数据"才算成功，而不是"连上了"就算】
    ///   tosu 在 osu 没运行时**照样接受 WebSocket 连接**，然后一包数据都不推
    ///   （2026-09-08 实测：HTTP /json 返回 <c>{"error":"osu is not ready/running"}</c>，
    ///     WebSocket 连得上但没有内容）。
    ///   所以"连上了"这个信号几乎不含信息量 —— 把它当成成功，用户会看到绿灯
    ///   却什么也没发生，那比红灯更让人困惑。
    ///
    /// 【等待时间用哪个值】
    ///   用配置里的「启动等待秒数」—— 它的语义本来就一模一样
    ///   （"等多久还没收到数据就算失败"）。这样那个设置项在界面上是**真的有用**的：
    ///   改大一点，这里当场就能看到等待时间变长。
    ///   ★ 将来自动启动 tosu/osu 时，同一条等待逻辑会复用这个值，
    ///     所以它只有一个含义，不存在"这里 6 秒、那里 20 秒"。
    /// </summary>
    /// <param name="ip">界面上的地址（空 → 用默认的 127.0.0.1）</param>
    /// <param name="port">界面上的端口（越界 → 直接报输入不合法，不去连）</param>
    /// <param name="waitSeconds">等多久算失败（秒），取值由 <see cref="DataSourceConfig.NormalizeStartupWaitSeconds"/> 夹好</param>
    /// <param name="token">取消（页面关掉 / 用户又点了一次）</param>
    public static async Task<TosuProbeResult> TestAsync(
        string? ip, int port, int waitSeconds, CancellationToken token = default)
    {
        var target = string.IsNullOrWhiteSpace(ip) ? DataSourceConfig.DefaultIp : ip.Trim();

        // 端口越界是**输入问题**，不是"对方不在" —— 不编一句"连不上"骗用户
        if (port is < DataSourceConfig.MinPort or > DataSourceConfig.MaxPort)
        {
            return new(TosuProbeOutcome.InvalidInput,
                       $"端口 {port} 不在 {DataSourceConfig.MinPort}~{DataSourceConfig.MaxPort} 之间，先改对再试",
                       0);
        }

        var seconds = DataSourceConfig.NormalizeStartupWaitSeconds(waitSeconds);
        var started = Environment.TickCount64;

        // 整个测试共用一个 N 秒的预算：**最坏也只让用户等 N 秒**
        // （连接和等数据分别给 N 秒的话，最坏会等 2N 秒 —— 点一下等十几秒是不能接受的）
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(seconds));

        using var socket = new ClientWebSocket();

        // ---------------- ① 先连 ----------------
        try
        {
            await socket.ConnectAsync(new Uri(DataSourceConfig.BuildUrl(target, port)), budget.Token)
                        .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(TosuProbeOutcome.Unreachable,
                       $"连不上 {target}:{port}（等了 {seconds} 秒）—— tosu 没在运行，或者端口不是这个",
                       Elapsed(started));
        }
        catch (Exception ex)
        {
            return new(TosuProbeOutcome.Unreachable,
                       $"连不上 {target}:{port}：{Describe(ex)}",
                       Elapsed(started));
        }

        // ---------------- ② 连上了，等第一包"能用"的数据 ----------------
        var buffer = new byte[64 * 1024];
        var pending = new StringBuilder();

        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, budget.Token).ConfigureAwait(false);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return new(TosuProbeOutcome.NoGameData,
                               $"连上了 {target}:{port}，但 tosu 立刻关掉了连接",
                               Elapsed(started));
                }

                pending.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
                if (!received.EndOfMessage) continue;      // 分片消息，等收完

                var json = pending.ToString();
                pending.Clear();

                // 判"能不能用"用的是**运行期同一个解析器** —— 这里要是自己写一套宽松判断，
                // 就会出现"测试连接说通了、悬浮窗却说没数据"这种最费解的不一致。
                if (TosuJsonParser.Parse(json) is not null)
                {
                    return new(TosuProbeOutcome.Success,
                               $"已连上 {target}:{port}，而且收到了游戏数据",
                               Elapsed(started));
                }

                // 心跳包 / 半截包：不是数据，接着等（和 TosuClient 的处理一致）
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(TosuProbeOutcome.NoGameData,
                       $"连上了 {target}:{port}，但 {seconds} 秒内没收到游戏数据 —— osu 没在跑？（地址和端口是对的）",
                       Elapsed(started));
        }
        catch (Exception ex)
        {
            return new(TosuProbeOutcome.NoGameData,
                       $"连上了 {target}:{port}，但读取中断：{Describe(ex)}",
                       Elapsed(started));
        }
    }

    private static int Elapsed(long started) => (int)(Environment.TickCount64 - started);

    /// <summary>
    /// 异常 → 人话。
    /// `WebSocketException` 自己那句 "Unable to connect to the remote server" 里
    /// 不含任何有用信息，真正的原因（连接被拒绝 / 名字解析不了 / 超时）在内层异常里。
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { InnerException: { } inner } => inner.Message,
        _ => ex.Message
    };
}
