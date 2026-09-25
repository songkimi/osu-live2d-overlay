// ============================================================
// UpdateVersion.cs —— 纯逻辑：判断"远端那个版本是不是比本地新"
//
// 【它为什么是纯逻辑】
//   进两个字符串、出一个 bool —— 不联网、不碰 WPF、不碰文件，
//   所以能进控制台测（《项目结构约定》§二，和 ComboTracker / TosuJsonParser 同一个道理）。
//   "联网去 GitHub 问最新版本是多少"那一半在它外面，还没做。
//
//
// 【★ 最要命的一条：版本号不能当字符串比】
//     "0.10.0" < "0.9.0"     ← 字符串比较会这么说，因为 '1' < '9'
//     但 0.10.0 明明比 0.9.0 新。
//   所以要先把两边变成"一串数字"，再逐段比大小 —— 这是本次要练的核心。
//
// 【认不出来就返回 false】
//   宁可漏报"有新版本"（用户自己去仓库看一眼），
//   不可误报（用户白跑一趟下载，还会觉得这软件不靠谱）。
//
// ============================================================
using System;

namespace OsuLive2dOverlay;

public static class UpdateVersion
{
    /// <summary>
    /// 远端版本比本地新吗。
    ///
    /// **认不出来（空、乱码、格式不对）→ 返回 <c>false</c>。**
    /// </summary>
    /// <param name="local">本地版本。可能是 "0.7.0"，也可能是 "0.7.0+65dd21fe..."（来自程序集）</param>
    /// <param name="remote">远端版本。可能是 "v0.8.0"、"0.8.0"，也可能是脏数据</param>
    public static bool IsNewer(string? local, string? remote)
    {
        
        var oldVersion = Parse(local);
        var newVersion = Parse(remote);
        if(oldVersion is null || newVersion is null) return false;
        if(oldVersion >= newVersion) return false;
        return true;
    }

    /// <summary>
    /// 把真实世界那些写法变成能比大小的东西。认不出来返回 <c>null</c>。
    ///
    /// 要能吃下的写法：
    ///   "0.7.0"                ← csproj 里那个
    ///   "0.7.0+65dd21fe..."    ← 程序集里那个（"+" 后面是 git commit，不是版本的一部分）
    ///   "v0.8.0" / "V0.8.0"    ← GitHub tag 的习惯写法
    ///   "  0.8.0  "            ← 手打时多出来的空格
    ///
    /// 认不出的例子：""、"abc"、"v"、"0.8.0-beta"、"1.2.3.4.5"
    /// </summary>
    private static Version? Parse(string? text)
    {
        if (text == null) return null;
        var noEmpty = text.Trim();
        if (noEmpty.IndexOf("+") != -1)
        {
            noEmpty= noEmpty.Substring(0, noEmpty.IndexOf("+"));
        }

        noEmpty = noEmpty.TrimStart('v', 'V');

        if (noEmpty.IndexOf("-") != -1)
        {
            return null;
        }
        try
        {
            var parts = noEmpty.Split('.');
            if (parts.Length > 4)
            {
                return null;
            }
            Version.TryParse(noEmpty, out var version);
            return version;
        }
        catch
        {
            return null;
        }
    }
}
