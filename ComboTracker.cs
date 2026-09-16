namespace OsuLive2dOverlay;

public enum ComboEventKind
{
    /// <summary>跨过了阈值：该做点瞬时表现了</summary>
    Step,

    /// <summary>连击中断了：该做点反应了</summary>
    Break,

    /// <summary>连击区间变了：常态表情该换了</summary>
    RangeChanged
}

/// <summary>一个连击区间（只关心边界；那一段该用哪个表情由外面的配置决定）</summary>
public readonly record struct ComboRange(int Min, int Max);

/// <summary>
/// 一次需要被表现层执行的动作
/// </summary>
/// <param name="Kind">应该发生的状态</param>
/// <param name="Combo">当前combo</param>
/// <param name="PreviousCombo">前combo</param>
/// <param name="Level">触发的阈值等级</param>
/// <param name="Threshold">当前正跨过的阈值</param>
/// <param name="MaxCombo">最大连击</param>
/// <param name="RangeIndex">当前处于的区间（默认-1）</param>
public readonly record struct ComboEvent(
    ComboEventKind Kind,
    int Combo,
    int PreviousCombo,
    int Level,
    int Threshold,
    int MaxCombo,
    int RangeIndex      // 当前处于第几个区间（没配区间 = -1）
);

public sealed class ComboTracker
{
    private readonly int[] _thresholds;
    private readonly ComboRange[] _comboRanges;
    private int _lastCombo;
    private int _lastLevel;
    private int _lastRange = -1;

    /// <summary>
    /// 当前处于第几个区间（没配区间 = -1）。
    /// 给"页面就绪时补发一次常态表情"用：区间事件在启动那一刻就报过了，
    /// 而那时页面还在加载、消息被丢弃 —— 不补这一下，角色会一直没有常态表情。
    /// </summary>
    public int CurrentRangeIndex => _lastRange;

    /// <summary>
    /// thresholds：阈值（来自配置，比如 50、100、200）；可能乱序、重复、带 0，需要你整理
    /// ranges：连击区间；顺序有意义，不要排序；Min &gt; Max 的非法项要忽略
    /// </summary>
    public ComboTracker(IEnumerable<int> thresholds, IEnumerable<ComboRange> ranges)
    {
        _thresholds = thresholds.Where(x => x > 0).Distinct().OrderBy(x => x).ToArray();
        if (_thresholds.Length == 0) { _thresholds = new int[] { 50 }; }
        _comboRanges = ranges.Where(r => r.Min <= r.Max).ToArray();
    }
    /// <summary>
    /// 检测区间列表中是否存在相互重叠，返回所有重叠冲突对（配合配置加载处的宽松和严格模式）
    /// </summary>
    /// <param name="ranges"></param>
    /// <returns></returns>
    public static List<(ComboRange A, ComboRange B)> FindOverlaps(IEnumerable<ComboRange> ranges)
    {
        var list = ranges.ToList();
        var conflicts = new List<(ComboRange A, ComboRange B)>();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            for (int j = i + 1; j < list.Count; j++)
            {
                var b = list[j];
                bool isOverlap = a.Min <= b.Max && b.Min <= a.Max;
                if (isOverlap)
                {
                    conflicts.Add((a, b));
                }
            }
            
        }
        return conflicts;
    }
    /// <summary>
    /// 喂入新的连击数，返回这一瞬间需要处理的事件。
    /// 返回**空列表**表示什么都不用做（不是 null）。
    /// </summary>
    public IReadOnlyList<ComboEvent> Update(int combo, int maxCombo)
    {
        List<ComboEvent> comboEvents = new List<ComboEvent>();
        int range = _lastRange;
        for (int i = 0; i < _comboRanges.Length; i++)
        {
            if (_comboRanges[i].Min <= combo && combo <= _comboRanges[i].Max) { range = i; break; }
        }
        if (combo < _lastCombo)
        {
            var broke = new ComboEvent(ComboEventKind.Break, combo, _lastCombo, _lastLevel, 0, maxCombo, range);
            comboEvents.Insert(0, broke);
            _lastLevel = 0;
        }
        if (range!=_lastRange)
        {
            var rangeChange = new ComboEvent(ComboEventKind.RangeChanged,combo,_lastCombo, _lastLevel,0,maxCombo, range);
            _lastRange = range;
            comboEvents.Insert(0, rangeChange);
        }
        int level = 0;
        for (int i = 0; i < _thresholds.Length; i++)
            if (combo >= _thresholds[i])
                level++;

        if (level > _lastLevel)
        {
            var stepped = new ComboEvent(ComboEventKind.Step, combo, _lastCombo, level, _thresholds[level - 1], maxCombo,range);
            _lastLevel = level;
            comboEvents.Add(stepped);
        }
        _lastCombo = combo;

        
        return comboEvents.AsReadOnly();
    }
}
