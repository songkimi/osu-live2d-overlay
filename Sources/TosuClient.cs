// ============================================================
// TosuClient.cs —— 数据层：从 tosu 拿 osu! 的实时状态
//
// 职责边界（和超市项目里的 Service 层一个道理）：
//   本类负责"连 WebSocket、收包、把解析结果抛出去"，**不做任何字段解析** ——
//   解析在 TosuJsonParser 里，因为那部分能在控制台测，而连网络这部分不能。
//   所以这个文件里不该出现任何 JSON 字段名。
//
// 实测确认的数据路径：见 TosuJsonParser 的说明。
// ============================================================
using System.Net.WebSockets;
using System.Text;

namespace OsuLive2dOverlay;

public sealed class TosuClient
{
    private const string DefaultUrl = "ws://127.0.0.1:24050/ws";

    /// <summary>
    /// 多久没收到**能用的**数据，就认定"游戏那边已经没了"（毫秒）。
    ///
    /// 为什么需要它：osu 退出之后 tosu 常常还活着、WebSocket 连接也没断，
    /// 但它**不再推完整数据**（只剩心跳包，被 TosuJsonParser 判成"没法用"直接跳过）。
    /// 于是这边一声不响，界面状态机自己永远不会变 ——
    /// 表现就是"把游戏关掉、角色还挂在屏幕上"。所以必须自己发现"没数据了"。
    /// </summary>
    private const int SilentAfterMs = 3000;

    /// <summary>最后一次收到**能用的**数据的时间（Environment.TickCount64）</summary>
    private long _lastDataTicks;

    /// <summary>连击变化：(当前连击, 最大连击)</summary>
    public event Action<int, int>? ComboUpdated;

    /// <summary>
    /// 每一包**能用的**数据都抛一次（界面状态、连击都在里面）。
    /// 心跳包、半截包不会走到这儿 —— TosuJsonParser 对它们返回 null，这里直接跳过。
    /// </summary>
    public event Action<TosuSnapshot>? SnapshotReceived;

    /// <summary>连接状态文字，用于在界面上显示</summary>
    public event Action<string>? StatusChanged;

    /// <summary>
    /// 连接断开 / 连不上（tosu 关了，或者 osu 退出了）。
    ///
    /// 为什么要单独一个事件：断开之后不会再有数据推来，界面状态必须回到"不知道" ——
    /// 否则角色会停在上一个界面不动。最典型的症状就是"把游戏关掉，角色还挂在屏幕上"。
    /// </summary>
    public event Action? Disconnected;

    /// <summary>收到的第一条原始消息（调试用，方便排查字段结构变化）</summary>
    public event Action<string>? FirstMessageReceived;

    public string Url { get; init; } = DefaultUrl;

    /// <summary>持续运行：连不上就等 3 秒重试，断了也自动重连（tosu 或游戏重启后能自愈）</summary>
    public async Task RunAsync(CancellationToken token)
    {
        bool announcedFirstMessage = false;
        _lastDataTicks = Environment.TickCount64;

        // 沉默检测：另起一个后台任务盯着"最后一次收到数据是什么时候"。
        // 不能在收数据的那段代码里发现这件事 —— 没有数据的时候，那段代码根本不会被调用。
        _ = Task.Run(async () =>
        {
            bool reported = false;
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(500, token); }
                catch (OperationCanceledException) { return; }

                if (Environment.TickCount64 - _lastDataTicks > SilentAfterMs)
                {
                    if (reported) continue;
                    reported = true;
                    Disconnected?.Invoke();
                }
                else
                {
                    reported = false;      // 数据又来了 → 允许下一次再报
                }
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();

            try
            {
                StatusChanged?.Invoke("正在连接 tosu ...");
                await socket.ConnectAsync(new Uri(Url), token);
                StatusChanged?.Invoke("已连接 tosu，等待游戏数据");

                var buffer = new byte[64 * 1024];
                var pending = new StringBuilder();

                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        StatusChanged?.Invoke("tosu 关闭了连接，3 秒后重连");
                        Disconnected?.Invoke();
                        break;
                    }

                    pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;   // 分片消息，等收完

                    var json = pending.ToString();
                    pending.Clear();

                    if (!announcedFirstMessage)
                    {
                        announcedFirstMessage = true;
                        FirstMessageReceived?.Invoke(json);
                    }

                    var snapshot = TosuJsonParser.Parse(json);
                    if (snapshot is null) continue;       // 心跳包 / 半截包：不是数据，跳过这一包

                    _lastDataTicks = Environment.TickCount64;   // 有能用的数据了 → 沉默检测重新计时
                    SnapshotReceived?.Invoke(snapshot.Value);
                    ComboUpdated?.Invoke(snapshot.Value.Combo, snapshot.Value.MaxCombo);
                }
            }
            catch (OperationCanceledException)
            {
                return;                                  // 程序退出，正常结束
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"连接失败：{ex.Message}");
                Disconnected?.Invoke();
            }

            if (token.IsCancellationRequested) return;

            try { await Task.Delay(3000, token); }
            catch (OperationCanceledException) { return; }
        }
    }
}
