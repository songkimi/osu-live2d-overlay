// ============================================================
// ComboTracker.cs —— 逻辑层：连击状态机（插件真正的"大脑"）
//
// 规则（阈值全部来自配置，不硬编码）：
//   · 每跨过一个阈值（如 50 / 100 / 200）→ 触发一次【Combo 动作】
//       49 → 51 会触发 50 那一档；51 → 55 不会再触发
//   · 连击下降（Miss/断连）→ 触发一次【Miss 动作】，并重新开始数
//
// 不依赖 WebSocket、不依赖界面 → 可以单独写单元测试。
// ============================================================
namespace ComboOverlay;

public enum ComboEventKind
{
    /// <summary>跨过阈值：该播放连击表情了</summary>
    Step,

    /// <summary>连击中断：该播放失误表情了</summary>
    Break
}

/// <summary>一次需要被表现层执行的动作</summary>
public readonly record struct ComboEvent(
    ComboEventKind Kind,
    int Combo,
    int PreviousCombo,
    int Level,        // 第几档（1 = 第一个阈值…）
    int Threshold,    // 本次跨过的阈值（Break 时为 0）
    int MaxCombo
);

public sealed class ComboTracker
{
    private readonly int[] _thresholds;   // 已升序去重
    private int _lastCombo;
    private int _lastLevel;

    public ComboTracker(IEnumerable<int> thresholds)
    {
        _thresholds = thresholds.Where(t => t > 0).Distinct().OrderBy(t => t).ToArray();
        if (_thresholds.Length == 0) _thresholds = new[] { 50 };   // 兜底，避免配置为空时完全不触发
    }

    /// <summary>喂入新的连击数，返回需要触发的动作（不需要触发就返回 null）</summary>
    public ComboEvent? Update(int combo, int maxCombo)
    {
        // ① 连击下降 = 断连击
        if (combo < _lastCombo)
        {
            var broke = new ComboEvent(ComboEventKind.Break, combo, _lastCombo, _lastLevel, 0, maxCombo);
            _lastCombo = combo;
            _lastLevel = 0;                 // 重新开始数
            return broke;
        }

        // ② 跨过阈值（一次跨多档也只报最高那档，避免刷屏）
        int level = 0;
        for (int i = 0; i < _thresholds.Length; i++)
            if (combo >= _thresholds[i]) level = i + 1;

        if (level > _lastLevel)
        {
            var stepped = new ComboEvent(ComboEventKind.Step, combo, _lastCombo, level,
                                         _thresholds[level - 1], maxCombo);
            _lastCombo = combo;
            _lastLevel = level;
            return stepped;
        }

        _lastCombo = combo;
        return null;
    }
}
