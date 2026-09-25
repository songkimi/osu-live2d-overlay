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

    /// <summary>上一次喂进来的连击（只给日志用：作废记忆时说得出"上一局到过多少"）</summary>
    public int LastCombo => _lastCombo;

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
    public static List<(int IndexA, T A,int IndexB,T B)> FindOverlaps<T>(IEnumerable<T> items, Func<T, ComboRange> getRange)
    {
        var list = items.ToList();
        var conflicts = new List<(int IndexA, T A, int IndexB, T B)>();
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            var ra = getRange(a);
            for (int j = i + 1; j < list.Count; j++)
            {
                var b = list[j];
                var rb = getRange(b);
                bool isOverlap = ra.Min <= rb.Max && rb.Min <= ra.Max;
                if (isOverlap)
                {
                    conflicts.Add((i, a, j, b));
                }
            }
        }
        return conflicts;
    }

    /// <summary>
    /// 喂入新的连击数，返回这一瞬间需要处理的事件。
    /// 返回**空列表**表示什么都不用做（不是 null）。
    /// </summary>
    /// <param name="combo">这一包报的连击</param>
    /// <param name="maxCombo">这一包报的最大连击</param>
    /// <param name="inGame">
    /// **现在是不是在打歌**。
    ///
    /// 【为什么要有这个参数】（★ 2026-09-23 修"打完歌角色又做一次表现"）
    ///   「跨阈值」和「断连击」这两件事**只在打歌里有意义** ——
    ///   打歌之外 tosu 照样报连击，但那是上一局的残留值：
    ///   实测（2026-09-22 抓包）退回选歌时 state 已经是 5，而 gameplay 段里
    ///   combo=85 / score=712897 还挂着，下一包才整体归零。
    ///
    ///   所以外面的调用方在非打歌时**按 combo=0 喂**（见 OverlayWindow.OnComboUpdated），
    ///   并且把这里设为 false：区间照算（常态表情要跟着回到"0 连击那一档"），
    ///   但断连击与跨阈值一概不报 —— 那两件事报出来就是"凭空做一次反应"。
    ///
    ///   默认 true 是为了让老的调用点（练习与验证工程那一堆）行为一个字不变。
    /// </param>
    public IReadOnlyList<ComboEvent> Update(int combo, int maxCombo, bool inGame = true)
    {
        List<ComboEvent> comboEvents = new List<ComboEvent>();
        int range = _lastRange;
        for (int i = 0; i < _comboRanges.Length; i++)
        {
            if (_comboRanges[i].Min <= combo && combo <= _comboRanges[i].Max) { range = i; break; }
        }
        if (inGame && combo < _lastCombo)
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

        if (inGame && level > _lastLevel)
        {
            var stepped = new ComboEvent(ComboEventKind.Step, combo, _lastCombo, level, _thresholds[level - 1], maxCombo,range);
            comboEvents.Add(stepped);
        }

        // ★ `_lastLevel` 现在**总是跟着 combo 走**（原来只在"报 Step"时更新）。
        //
        //   理由是上面那个 inGame：非打歌时我们按 combo=0 喂，这里必须把等级一起归零 ——
        //   否则下一局开局 combo 涨过第一个阈值时，"level > _lastLevel" 不成立，
        //   连击表情就**再也不会触发**（一种很典型的静默失效）。
        //
        //   顺带修掉一个老毛病：combo 下降但仍高于阈值时（数据抖动会出现），
        //   旧写法会在报完 Break 之后紧接着再报一个 Step —— 那是凭空多出来的表现。
        _lastLevel = level;

        _lastCombo = combo;

        
        return comboEvents.AsReadOnly();
    }

    /// <summary>
    /// 把内部记忆清回初始状态：连击 0、等级 0、没有区间。
    ///
    /// ★ **调用时机（2026-09-23 改过）**：**离开打歌的那一刻**调一次。
    ///
    ///   原来是在"**进入**打歌"时调的，那是有问题的：
    ///   进入打歌的那一包数据里，combo **未必是 0** —— 从加载切到打歌、或者场景被一包
    ///   残留数据误判成 Playing 时，同一包里 combo 可能还是上一局的高值。
    ///   记忆刚清成 0，紧接着就吃到"1000" → 会被当成**连击刚刚涨上去** →
    ///   凭空报一次 RangeChanged + Step（角色的常态表情与最高阈值表情一起重放一遍）。
    ///   用户报的现象正是这个："打完歌角色会再次触发打歌那一区间的常态表情，
    ///   同时似乎还触发了最高阈值的那个状态"。
    ///
    ///   改成"离开时清"之后，进入打歌时记忆本来就是干净的：
    ///   第一包无论报 0 还是报几十，都是**真实的当下状态**，不会重放任何东西。
    ///
    /// 清完后 <see cref="CurrentRangeIndex"/> 回到 -1，所以下一次 Update 一定会
    /// 报一次 RangeChanged（把常态表情摆回正确的档位）。
    /// </summary>
    public void Reset()
    {
        _lastCombo = 0;
        _lastLevel = 0;
        _lastRange = -1;
    }
}
