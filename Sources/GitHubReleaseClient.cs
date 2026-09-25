// ============================================================
// GitHubReleaseClient.cs —— 数据层：问 GitHub "最新版本是哪个"
//
// 【它在这一步里干什么】
//   检查更新的第 ① 步。拿到远端版本号之后，交给 `Logic/UpdateVersion.IsNewer`
//   去判断谁更新（那一步不联网，已经做完、已经全绿）。
//
// 【为什么放 Sources/】
//   《项目结构约定》§三 的决策表："一种新的**外部数据来源** → `Sources/`"
//   —— 和 tosu 那一条同一类。
//
// 【为什么不像 TosuClient / TosuJsonParser 那样拆成两个文件】
//   那边拆，是因为解析要手写 JsonDocument、几十行、还要逐字段判断形状。
//   这边 GitHub 的响应是**有 schema 的**，一行 JsonSerializer.Deserialize 就够 ——
//   不值得多一个文件。但解析仍然做成 **static**，这样它能被单独调、能测
//   （和"能用控制台测的逻辑别跟要连外部世界的代码混在一起"是同一条原则）。
//
// 【字段名照着抓包写，不要靠猜】
//   实测（2026-09-25 抓的 sample-release.json，顶层 20 个字段）：
//     tag_name     = "v0.7.0"                  ← 我们要的版本号（**带 v 前缀**）
//     name         = ""                        ← ★ 空的！别拿它显示版本
//     html_url     = ".../releases/tag/v0.7.0"
//     body         = "## 更新说明\r\n..."       ← 更新说明，换行是 \r\n
//     published_at = "2026-09-25T07:40:13Z"    ← ISO 8601，带 Z（UTC）
//     assets       = []                        ← 这一版还没有附件，先不管
//
// 【必须带 User-Agent】
//   GitHub API **会拒绝不带 User-Agent 的请求**（403）。这不是"最好带上"。
// ============================================================
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OsuLive2dOverlay;

/// <summary>
/// 从 GitHub 拿到的那一份"最新版本信息" —— **只留我们真会用的字段**。
///
/// 和 <see cref="TosuSnapshot"/> 一个道理：不把整个报文搬进来，
/// 只抠出程序需要的几个值（那边有 20 个字段，我们只要 4 个）。
/// </summary>
/// <param name="TagName">版本号，如 "v0.7.0"。**喂给 UpdateVersion.IsNewer 的就是它**</param>
/// <param name="HtmlUrl">这个 Release 的网页地址（"查看更新"按钮点它）</param>
/// <param name="Notes">更新说明（body）。**是空串而不是 null** —— 免得调用方到处判空</param>
/// <param name="PublishedAt">发布时间；没读到就是 null</param>
public sealed record ReleaseInfo(
    string TagName,
    string HtmlUrl,
    string Notes,
    DateTimeOffset? PublishedAt);

public static class GitHubReleaseClient
{
    // 仓库地址**写死在代码里**：它是程序自己的身份，不是用户能配的东西
    // （和"不指望用户改 config.json"是同一条原则）。
    private const string Owner = "songkimi";
    private const string Repo = "osu-live2d-overlay";

    /// <summary>
    /// HttpClient **复用同一个实例**，别每次请求都 new。
    ///
    /// 每次 new 都会攒下一批没人回收的 socket，短时间内请求多了会把本机可用端口耗光，
    /// 报出来是 SocketException —— 而它看起来**完全不像**"HTTP 客户端建太多"的问题。
    /// 这是 .NET 里关于 HttpClient 最经典的一条坑。
    /// </summary>
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient
        {
            
            Timeout = TimeSpan.FromSeconds(15)
        };

        // 这两个头都必须有：缺 User-Agent 会被 GitHub 直接拒（403）
        client.DefaultRequestHeaders.UserAgent.ParseAdd("osu-live2d-overlay");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// 最新 Release 的地址。
    /// `latest` 这个端点**不返回草稿、也不返回预发布版本** —— 它专门挑最新的正式版。
    /// （所以 `IsNewer` 那边不需要处理 "0.8.0-beta" 这种输入，正常路径拿不到。）
    /// </summary>
    public static string LatestApiUrl => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>
    /// 从一包 GitHub 报文里抠出我们关心的那几个字段。
    ///
    /// **认不出来就返回 null** —— 和 <see cref="UpdateVersion.IsNewer"/> 同一个原则：
    /// 宁可说"查不到"，也不编一个版本号出来。
    /// </summary>
    public static ReleaseInfo? Parse(string? json)
    {
        
        try
        {
            string tagname = string.Empty;
            string htmlurl = "";
            string body = "";
            DateTimeOffset? publishedAt = null;
            if (json == null)
            {
                DebugLog.Write("GitHub返回了未知的包");
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("tag_name", out var tagElem))
            {
                tagname = tagElem.GetString() ?? "";
            }
            if (tagname == string.Empty || string.IsNullOrWhiteSpace(tagname))
            {
                DebugLog.Write("无法获取到版本号");
                return null;
            }
            if (root.TryGetProperty("html_url", out var htelElem))
            {
                htmlurl = htelElem.GetString() ?? "";
            }
            if (root.TryGetProperty("body", out var bodyElem))
            {
                body = bodyElem.GetString() ?? "";
            }
            if (root.TryGetProperty("published_at", out var publiElem))
            {
                if (publiElem.ValueKind == JsonValueKind.String && publiElem.TryGetDateTimeOffset(out var dt))
                {
                    publishedAt = dt;
                }

            }
            return new ReleaseInfo(tagname, htmlurl, body, publishedAt);
        }
        catch (JsonException)
        {
            DebugLog.Write("从GitHub得到的json包不可用");
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"解析来自Github包时发生了未知异常： {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 联网问"最新版本是哪个"。
    /// </summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken token = default)
    {
        
        try
        {
            string url = LatestApiUrl;
            using var response = await Http.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    DebugLog.Write("还没发布新版本");
                }
                else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    DebugLog.Write("请求太频繁，Github限流");
                }
                else
                {
                    DebugLog.Write($"Github接口请求失败，状态码{response.StatusCode}");
                }
                return null;
            }
            string json = await response.Content.ReadAsStringAsync(token);
            return Parse(json);


        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;                    // 用户主动取消
        }
        catch (TaskCanceledException)
        {
            DebugLog.Write("检查更新超时，请求被取消");
            return null;
        }
        catch (HttpRequestException ex)
        {
            DebugLog.Write($"网络请求异常：{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"查询更新未知异常：{ex.Message}");
            return null;
        }
    }

}
