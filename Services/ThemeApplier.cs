// ============================================================
// ThemeApplier.cs —— 把「外观 / 字体 / 字号」三项设置即时作用到界面上
//
// 【为什么需要一个"应用器"】
//   这三项在定稿 §1.1 里都属于**即时**类：改完立刻生效、与游戏状态无关。
//   而"立刻生效"这件事的本质是**改资源字典**，不是改控件属性 ——
//   因为界面上所有颜色/字号都是 `{DynamicResource ...}` 引 Token 的
//   （见 Resources/Styles 里的说明），改字典就等于改全局。
//
// 【它为什么在 Services/ 而不是 Views/】
//   它要 WPF 类型，但它不是"某个页面"，而是**跨页面的能力**
//   （《项目结构约定》§四：Services/ 放配置读写门面、服务生命周期这类东西）。
//
// 【唯一要注意的顺序问题】
//   Tokens 字典必须待在它原来的**位置**上 —— 因为 `HCOverrides.xaml` 在它后面加载，
//   靠"后加载覆盖同名 key"生效。所以下面用**原位替换**，不是先删后加。
// ============================================================
using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using HcThemes = HandyControl.Themes;
using HcData = HandyControl.Data;

namespace OsuLive2dOverlay;

public static class ThemeApplier
{
    /// <summary>三项一起应用。页面在"即时生效"的字段变更时调它。</summary>
    public static void Apply(string appearance, string font, double fontSizePoints)
    {
        ApplyAppearance(appearance);
        ApplyFont(font);
        ApplyFontSize(fontSizePoints);
    }

    /// <summary>
    /// 外观：<c>跟随系统</c> / <c>浅色</c> / <c>深色</c>。
    ///
    /// 【必须走两条通道，缺一条就有一半界面不变】（2026-09-20 实测踩出来的）
    ///
    ///   **① HandyControl 的控件 → 只能用它官方的 `Theme.SetSkin`**
    ///   我原来的做法是"手动把 HC 的皮肤字典替换掉"，实测**控件的实际颜色根本不变**：
    ///     · 手动替换：控件背景 <c>#FFFFFFFF → #FFFFFFFF</c>（没动）
    ///     · 官方换肤：控件背景 <c>#FFFFFFFF → #FF1C1C1C</c>（真的换了）
    ///   原因是 HC 控件的笔刷在**加载时就定死**了，换字典追不上；
    ///   `Theme.SetSkin` 会让控件**重新应用一遍样式**，颜色才真的跟着走。
    ///
    ///   **② 我们自己的 Token → 换字典**
    ///   HC 管不到我们自己的 key（`Brush.Primary` 那些），这一半照样得靠换字典。
    ///   我们是 `{DynamicResource ...}` 引的，所以换字典立刻生效 —— 这一半本来就没问题。
    ///
    ///   **③ ★ 换肤之后必须把我们自己的样式"抬到窗口上"**（2026-09-23 实测补的一条）
    ///
    ///   `Theme.SetSkin(window, …)` 不只是换颜色，它还会把 HandyControl 的**控件样式**
    ///   挂到**窗口**的 `MergedDictionaries` 上。而 WPF 查资源是
    ///   **元素 → 窗口（含窗口的合并字典）→ 应用** —— 窗口级先于应用级。
    ///   我们那份 `Styles.Basic.xaml` 挂在**应用级**，于是**被整体压住**：
    ///     · 症状：自己画的 TabControl / TabItem 在**设计器里是好的、运行时不生效**
    ///       （用户截图对照：设计器里是圆角浅蓝块，运行时却是 HC 的默认样子）
    ///     · 实测证据：换肤后"窗口级字典里有 TabControl / Button 的隐式样式"
    ///       （`.build-verify/SkinProbe`）
    ///   所以换肤之后要把我们的三份也挂到窗口上，**而且排在 HC 那份之后**（后面的赢）。
    /// </summary>
    public static void ApplyAppearance(string appearance)
    {
        var app = Application.Current;
        if (app is null) return;                 // 设计器/控制台里没有 Application，静默跳过

        var wantDark = appearance switch
        {
            "深色" => true,
            "浅色" => false,
            _ => IsSystemDark()                  // 「跟随系统」，同时也兜住未知值
        };

        // ① HandyControl：官方换肤。
        //    它要挂在一个 DependencyObject 上，一般就是主窗口。
        //    **启动早期 MainWindow 还是 null**（App.OnStartup 时窗口还没建），
        //    所以 MainWindow 加载完还会再调一次 Apply —— 见 MainWindow.xaml.cs。
        if (app.MainWindow is { } window)
        {
            HcThemes.Theme.SetSkin(window, wantDark ? HcData.SkinType.Dark : HcData.SkinType.Default);

            // 紧接着把我们自己的三份抬到同一个窗口上（见上面第 ③ 条）——
            // 否则 HC 挂在窗口上的那批控件样式会把我们的压住。
            AttachOurDictionariesAboveSkin(window, wantDark);
        }

        // ② 我们自己的 Token：整份替换（浅色 ↔ 深色）
        ReplaceTokens(wantDark);
    }

    /// <summary>
    /// 把我们自己的三份字典挂到**窗口**上，排在 HC 换肤字典**之后**。
    ///
    /// 顺序不能反：同一层里**后面的字典赢**。三份之间的相对顺序也必须保持
    /// （Tokens → HCOverrides → Styles.Basic），理由和 App.xaml 里那条一样。
    ///
    /// 每次调用先摘掉上次挂的那些：换肤可以来回切，不摘就会越挂越多，
    /// 而且旧的那份（比如 Tokens.Light）会一直压在下面 —— 表现成"深色切不干净"。
    /// </summary>
    private static void AttachOurDictionariesAboveSkin(Window window, bool dark)
    {
        var dicts = window.Resources.MergedDictionaries;

        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("/Resources/Styles/", StringComparison.Ordinal))
                dicts.RemoveAt(i);
        }

        var suffix = dark ? "Dark" : "Light";

        dicts.Add(new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/Styles/Tokens.{suffix}.xaml")
        });
        dicts.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Resources/Styles/HCOverrides.xaml")
        });
        dicts.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Resources/Styles/Styles.Basic.xaml")
        });
    }

    /// <summary>
    /// 整份替换 Tokens 字典（我们自己的颜色/字号定义）。
    ///
    /// **原位替换**，不是先删后加 —— 因为 `HCOverrides.xaml` 排在它后面，
    /// 靠"后加载覆盖同名 key"生效，顺序一乱覆盖就没了。
    /// </summary>
    private static void ReplaceTokens(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        var wanted = new Uri(
            "pack://application:,,,/Resources/Styles/Tokens." + (dark ? "Dark" : "Light") + ".xaml");

        var dicts = app.Resources.MergedDictionaries;
        var old = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains("Tokens.") == true);

        if (old?.Source == wanted) return;       // 已经是这一份，不用动

        var fresh = new ResourceDictionary { Source = wanted };

        if (old is not null)
            dicts[dicts.IndexOf(old)] = fresh;   // ★ 原位替换：保住字典顺序
        else
            dicts.Insert(0, fresh);              // 异常路径（正常由 App.xaml 提供）
    }

    /// <summary>
    /// 系统是否用深色（Win10/11：读注册表的 AppsUseLightTheme，1 = 浅色、0 = 深色）。
    /// 读不到就当浅色 —— **主题判断失败不该让程序崩**。
    /// </summary>
    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 字体：<c>跟随系统</c> 或具体字体名。
    ///
    /// 「跟随系统」要拼上**中文兜底**，这一步不能省：
    /// 探针确认 `Segoe UI` / `Tahoma` / `Arial` **都没有中文字形**，
    /// 非中文系统上让它们独自渲染中文就只能靠字体回退（定稿 §8.9）。
    /// WPF 的 FontFamily 支持逗号分隔的候选、**按字符**回退，所以一行就够。
    /// </summary>
    public static void ApplyFont(string font)
    {
        var app = Application.Current;
        if (app is null) return;

        var family = string.IsNullOrWhiteSpace(font) || font == "跟随系统"
            ? new FontFamily($"{SystemFonts.MessageFontFamily.Source}, Microsoft YaHei UI, DengXian")
            : new FontFamily(font);

        // 直接写在 Application.Resources 顶层：资源查找先看顶层、再看它合并的字典，
        // 所以这一行会盖住 Styles.Basic.xaml 里的默认值。
        app.Resources["FontFamily.UI"] = family;
    }

    /// <summary>
    /// 字号：给一个**基准 pt**，五个层级各自按倍率算出来。
    ///
    /// 单位是 pt，而 WPF 的 FontSize 是 DIP —— 换算与"吸附到整数"都在
    /// <see cref="FontScale"/> 里（那里也写清了为什么必须吸附到整数）。
    /// </summary>
    public static void ApplyFontSize(double fontSizePoints)
    {
        var app = Application.Current;
        if (app is null) return;

        app.Resources["FontSize.PageTitle"] = FontScale.Dip(FontLevel.PageTitle, fontSizePoints);
        app.Resources["FontSize.CardTitle"] = FontScale.Dip(FontLevel.CardTitle, fontSizePoints);
        app.Resources["FontSize.Body"] = FontScale.Dip(FontLevel.Body, fontSizePoints);
        app.Resources["FontSize.Caption"] = FontScale.Dip(FontLevel.Caption, fontSizePoints);
        app.Resources["FontSize.Label"] = FontScale.Dip(FontLevel.Label, fontSizePoints);
    }
}
