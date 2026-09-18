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

    /// <summary>连击变化：(当前连击, 最大连击)</summary>
    public event Action<int, int>? ComboUpdated;

    /// <summary>
    /// 每一包**能用的**数据都抛一次（界面状态、连击都在里面）。
    /// 心跳包、半截包不会走到这儿 —— TosuJsonParser 对它们返回 null，这里直接跳过。
    /// </summary>
    public event Action<TosuSnapshot>? SnapshotReceived;

    /// <summary>连接状态文字，用于在界面上显示</summary>
    public event Action<string>? StatusChanged;

    /// <summary>收到的第一条原始消息（调试用，方便排查字段结构变化）</summary>
    public event Action<string>? FirstMessageReceived;

    public string Url { get; init; } = DefaultUrl;

    /// <summary>持续运行：连不上就等 3 秒重试，断了也自动重连（tosu 或游戏重启后能自愈）</summary>
    public async Task RunAsync(CancellationToken token)
    {
        bool announcedFirstMessage = false;

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
            }

            if (token.IsCancellationRequested) return;

            try { await Task.Delay(3000, token); }
            catch (OperationCanceledException) { return; }
        }
    }
}
