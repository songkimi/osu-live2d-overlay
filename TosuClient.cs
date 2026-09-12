// ============================================================
// TosuClient.cs —— 数据层：从 tosu 拿 osu! 的实时状态
//
// 职责边界（和超市项目里的 Service 层一个道理）：
//   本类只干一件事——连 WebSocket、解析 JSON、把"连击变了"这件事抛出去。
//   它不认识界面、不认识 WPF，将来换成别的数据源（比如自己做内存读取）
//   也不影响界面代码。
//
// 实测确认的数据路径：gameplay.combo.current / gameplay.combo.max
// ============================================================
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OsuLive2dOverlay;

public sealed class TosuClient
{
    private const string DefaultUrl = "ws://127.0.0.1:24050/ws";

    /// <summary>连击变化：(当前连击, 最大连击)</summary>
    public event Action<int, int>? ComboUpdated;

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

                    TryParseCombo(json);
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

    /// <summary>从一整包 JSON 里取出连击数；字段缺失或不是 JSON 就静默跳过</summary>
    private void TryParseCombo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 逐层 TryGetProperty：任何一层缺失都不会抛异常（比直接索引安全）
            if (!root.TryGetProperty("gameplay", out var gameplay)) return;
            if (!gameplay.TryGetProperty("combo", out var comboNode)) return;

            int current = comboNode.TryGetProperty("current", out var c) ? c.GetInt32() : 0;
            int max = comboNode.TryGetProperty("max", out var m) ? m.GetInt32() : 0;

            ComboUpdated?.Invoke(current, max);
        }
        catch (JsonException)
        {
            // 心跳包/空包，忽略
        }
    }
}
