// ============================================================
// HotkeyParser.cs —— 把配置里那个"人写的样子"翻成 Win32 要的两个数
//
// 【为什么需要它】
//   配置里热键存成 `"Ctrl+Alt+Q"`（人能看懂、能手改），
//   而 `RegisterHotKey` 要的是 `MOD_CONTROL | MOD_ALT` 和 `VK_Q` 这样的数。
//   中间这一步原先**根本不存在** —— 窗口里是写死注册的：
//
//       Hotkey(HOTKEY_QUIT, Win32.VK_Q);          // 永远是 Ctrl+Alt+Q
//       Hotkey(HOTKEY_TOGGLE_MODE, Win32.VK_T);   // 永远是 Ctrl+Alt+T
//
//   于是配置里那三个热键字段（结束 / 临时交互 / 显示隐藏）**一个都没被读过**，
//   而且埋着一个直接骗用户的矛盾：文档和配置默认值写的"临时交互 = Ctrl+Alt+E"，
//   代码里注册的却是 `T` —— 差一个字母，用户照着界面提示按键**按不出来**。
//   （设置项总表 §3.1 把这件事记了很久，这一步就是去把它接上。）
//
// 【为什么单独一个文件】
//   它是**纯逻辑**：进一个字符串、出一对数，不碰窗口、不碰 Win32 —— 所以能写用例。
//   这也是整个窗口设置里最该被测的一块：解析错一个键，表现就是"按了没反应"，
//   而那种问题在界面上看不出任何线索。
// ============================================================
using System;
using System.Collections.Generic;

namespace OsuLive2dOverlay;

public static class HotkeyParser
{
    // 和 Win32 的 MOD_* 对齐（故意用同样的数值：调用方可以直接拿去注册）
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    /// <summary>解析结果：修饰键 + 主键虚拟码 + 统一写法</summary>
    public sealed record ParsedHotkey(uint Modifiers, uint VirtualKey, string Normalized)
    {
        /// <summary>给人看的写法（和 `Normalized` 相同，方便界面直接用）</summary>
        public override string ToString() => Normalized;
    }

    /// <summary>
    /// 解析。**写不对就返回 null**，绝不猜一个默认值 ——
    /// 猜的话用户会看到"按了没反应"，而那是所有设置里最难查的一种。
    /// </summary>
    public static ParsedHotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;

        var modifiers = 0u;
        var virtualKey = 0u;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;

                default:
                    // 主键只允许一个：`Ctrl+A+B` 这种是写错了
                    if (virtualKey != 0) return null;

                    var vk = ToVirtualKey(part);
                    if (vk == 0) return null;
                    virtualKey = vk;
                    break;
            }
        }

        // 只有修饰键没有主键 → 不是热键（`Ctrl+Alt+` 这种多半是手滑）
        if (virtualKey == 0) return null;

        return new ParsedHotkey(modifiers, virtualKey, Normalize(modifiers, virtualKey));
    }

    /// <summary>统一写法：`Ctrl+Alt+Q`（修饰键固定顺序，主键大写）</summary>
    public static string Normalize(uint modifiers, uint virtualKey)
    {
        var names = new List<string>(5);

        if ((modifiers & ModControl) != 0) names.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) names.Add("Alt");
        if ((modifiers & ModShift) != 0) names.Add("Shift");
        if ((modifiers & ModWin) != 0) names.Add("Win");

        names.Add(KeyName(virtualKey));
        return string.Join("+", names);
    }

    /// <summary>虚拟码 → 给人看的键名（只覆盖我们支持的那些，别的原样报个十六进制）</summary>
    public static string KeyName(uint virtualKey)
    {
        if (virtualKey is >= 'A' and <= 'Z') return ((char)virtualKey).ToString();
        if (virtualKey is >= '0' and <= '9') return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x7B) return "F" + (virtualKey - 0x70 + 1);

        return virtualKey switch
        {
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ => "0x" + virtualKey.ToString("X2")
        };
    }

    /// <summary>键名 → 虚拟码。认不出来返回 0（调用方按"解析失败"处理）</summary>
    private static uint ToVirtualKey(string token)
    {
        // 单个字母 / 数字：虚拟码恰好等于大写的 ASCII 码
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }

        // F1~F12（F1 = 0x70）
        if (token.Length is 2 or 3 &&
            (token[0] is 'F' or 'f') &&
            int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 12)
        {
            return (uint)(0x70 + fn - 1);
        }

        return token.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "esc" or "escape" => 0x1B,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ => 0
        };
    }
}
