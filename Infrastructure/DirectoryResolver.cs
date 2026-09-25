// ============================================================
// DirectoryResolver.cs —— 把"配置里填的路径"解析成"实际用的路径"
//
// 【为什么需要它】
//   有些路径在配置里**允许留空**，留空的意思是"用默认位置"。
//   于是界面必须回答一个问题："那默认位置到底在哪儿？"
//   —— 界面显示"实际会用哪个"，用户才不用去猜、也才不需要一段解释文字。
//
//   而且这个答案必须**只有一份**：设置页显示的路径，必须与程序真正读写的那个完全一致。
//   所以默认文件夹名定义在这里，谁要用谁来取 —— 尤其是 `DebugLog`，
//   它写的目录必须和设置页显示的是同一个。
//
// 【它为什么在 Infrastructure】
//   纯字符串与路径拼接，不依赖 WPF、不做 IO —— 谁都可以用，它不依赖任何人
//   （《项目结构约定》§二）。也正因为如此，它能进控制台测。
//
// 【2026-09-20 精简过一次】
//   原本还有 `ProfilesFolderName` / `CharacterDistributionFolderName` 两个常量，
//   对应"配置档案目录"和"角色分布目录"两个字段 —— 那两个字段**代码里没人读**，
//   是"先有字段再找用途"的产物，已随 `目录{}` 段一起删除。
// ============================================================
using System;
using System.IO;

namespace OsuLive2dOverlay;

public static class DirectoryResolver
{
    /// <summary>日志目录的默认文件夹名（相对 exe）。**DebugLog 用的就是这个常量**。</summary>
    public const string LogsFolderName = "logs";

    /// <summary>
    /// 算出实际用的路径。
    /// 配置里留空 → exe 旁边的默认文件夹；填了就照用（绝对路径原样返回）。
    /// </summary>
    public static string Resolve(string? configured, string defaultFolderName)
        => string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, defaultFolderName)
            : configured!;

    /// <summary>这一项是不是在用默认位置</summary>
    public static bool IsUsingDefault(string? configured)
        => string.IsNullOrWhiteSpace(configured);
}
