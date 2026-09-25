// ============================================================
// SettingIndex.cs —— 给每个设置项标上"它在配置里的坐标"，并做出索引
//
// 【为什么一开始就要做】（定稿决策 29）
//   一份数据要喂**三处**：
//     · 体检报告点"某条问题" → 跳到对应设置项、滚动到它、高亮它
//     · 设置搜索 → 定位并高亮
//     · 帮助文档的"配置字段说明" → 指到界面上哪儿
//   这三件事都要回答同一个问题："`语音.目录` 这个设置在界面的哪个控件上？"
//
//   **后补的代价很大**：等做体检跳转时才想起来，就得给已经写好的十几页
//   逐个控件补标记 —— 而那时候页面都长好了，改一遍要重测一遍。
//   所以第一页就带上它，后面每一页照抄。
//
// 【用法】
//   XAML 里给控件挂上路径（写法与体检的 FieldPath 完全一致）：
//       <ComboBox svc:SettingIndex.Path="通用.外观" ... />
//   页面加载完调一次 Rebuild(页面根)，之后就能 Find("通用.外观") 拿到那个控件。
//
// 【Rebuild 的语义是"重建当前这一页的索引"】
//   不是"往全局表里累加" —— 因为页面切换后旧控件已经不在了，累加会留下悬空引用。
//   体检跳转的流程正好是"先切页 → 再 Rebuild → 再 Find"，所以够用。
// ============================================================
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace OsuLive2dOverlay;

public static class SettingIndex
{
    /// <summary>
    /// 附加属性：这个控件对应配置里的哪一项。
    /// 值用**体检 FieldPath 的同一种写法**（中文键名点号分级，数组用 [N]、1 起）。
    /// </summary>
    public static readonly DependencyProperty PathProperty =
        DependencyProperty.RegisterAttached(
            "Path",
            typeof(string),
            typeof(SettingIndex),
            new PropertyMetadata(null));

    public static string? GetPath(DependencyObject element)
        => (string?)element.GetValue(PathProperty);

    public static void SetPath(DependencyObject element, string? value)
        => element.SetValue(PathProperty, value);

    private static readonly Dictionary<string, FrameworkElement> Map = new(StringComparer.Ordinal);

    /// <summary>当前页面的索引：配置路径 → 控件</summary>
    public static IReadOnlyDictionary<string, FrameworkElement> Index => Map;

    /// <summary>重建索引（页面加载完调一次）。会先清空 —— 见文件头的说明。</summary>
    public static void Rebuild(DependencyObject root)
    {
        Map.Clear();
        Walk(root);
    }

    /// <summary>按配置路径找控件；找不到返回 null（找不到是正常情况：那一项可能还没做出来）。</summary>
    public static FrameworkElement? Find(string path)
        => Map.TryGetValue(path, out var element) ? element : null;

    private static void Walk(DependencyObject node)
    {
        if (node is FrameworkElement element)
        {
            var path = GetPath(element);
            if (!string.IsNullOrWhiteSpace(path)) Map[path!] = element;
        }

        // 走**可视树**而不是逻辑树：模板生成的控件只在可视树里，
        // 而设置项常常整个包在模板化的控件里。
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            Walk(VisualTreeHelper.GetChild(node, i));
    }
}
