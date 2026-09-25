// ============================================================
// BehaviorViewModel.cs —— 「行为」页

// 【清单是哪来的】
//   模型扫描（`ModelScanner`）→ 表情清单（`ExpressionCatalog`，给标识配注册名）
//   → 引用次数（`ExpressionReferences`，**"什么算一次引用"的唯一定义**）。
//   三样都是既有的东西，这一页只是把它们摆出来 —— 不新增任何"重复的真相"。
//
// 【清单只读】点一行只影响预览，落不到配置里，所以这一页**没有保存栏**。
//
// 【清单什么时候重建】
//   构造时扫一次（每次点导航进来都是新的 VM，见 `MainWindow.BuildNav` 的委托）。
//   故意**不订阅** `ConfigStore.ConfigChanged` —— 页面来一个订阅一份，
//   而那些订阅挂在一个活得比页面长的对象上，就是一份泄漏；
//   换档案本来就是"离开这一页"的动作，回来时自然是新的。
// ============================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

public partial class BehaviorViewModel : ObservableObject
{
    private readonly ConfigStore _store;

    /// <summary>挂在 `ConfigStore` 上的那个处理器 —— 存着它才取消得掉（lambda 是取消不掉的）</summary>
    private readonly Action _onDirtyChanged;

    public BehaviorViewModel(ConfigStore store)
    {
        _store = store;

        // 未保存标记：底部保存栏靠它决定"保存/放弃"能不能点
        _onDirtyChanged = () =>
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
        };
        _store.DirtyChanged += _onDirtyChanged;

        Reload();
    }

    /// <summary>
    /// 页面被换掉时调用：把挂在 `ConfigStore` 上的订阅摘掉。
    ///
    ///   为什么必须成对：页面是**每次点导航都新建**的，而 `ConfigStore` 活到程序退出 ——
    ///   订阅了不摘，就是"每切一次页留一份"。体检页那份尤其实在（它每次都会重扫模型）。
    ///   2026-09-23 查"反复切页就掉帧"时把这一类一并翻了出来：**先在这一页做对**，
    ///   其余几页的同类订阅还没补（定稿决策 56 附近记着）。
    /// </summary>
    public void Detach() => _store.DirtyChanged -= _onDirtyChanged;

    // ------------------------------------------------------------
    // 给预览区用的两样东西
    // ------------------------------------------------------------

    /// <summary>
    /// 现在这份配置。换档案时它会换成**新对象**，预览区靠这一次引用比较就知道该重建 ——
    /// 所以这里必须每次现取，不能存字段。
    /// </summary>
    public PluginConfig Config => _store.Config;

    /// <summary>调试模式：决定页面要不要跑渲染自检</summary>
    public bool DebugMode => _store.Settings.DebugMode;

    /// <summary>
    /// 预览该显示的那份站位：取**「打歌」**那一档。
    ///
    /// 为什么是打歌：它是角色最要紧的场合，也是唯一"必须显示角色"的界面。
    /// 为什么用 `PlacementFor(null)`：这里要看的是"标识在角色身上是什么样"，
    /// 不是"某个游玩模式怎么摆" —— 按模式分站位是打歌页自己的事。
    ///
    /// 取值走**和悬浮窗同一个** `PlacementFor`（不是另算一套）：
    /// 预览要是自己算，"看到的"和"跑到"的迟早对不上，那是最不该出的错。
    /// </summary>
    public CharacterPlacement PreviewPlacement =>
        _store.Config.Scenes?.Get(GameScene.Playing)?.PlacementFor(null) ?? new CharacterPlacement();

    /// <summary>
    /// 预览框的尺寸：按「打歌」那一档存过的尺寸。返回 0 = "没存过"，
    /// 由页面换成**程序默认尺寸**（默认尺寸的唯一出处见 `OverlayWindow.DefaultWidth`）。
    /// </summary>
    public double PreviewWindowWidth => _store.Config.Scenes?.Get(GameScene.Playing)?.Window.Width ?? 0;

    public double PreviewWindowHeight => _store.Config.Scenes?.Get(GameScene.Playing)?.Window.Height ?? 0;

    // ------------------------------------------------------------
    // 清单
    // ------------------------------------------------------------

    /// <summary>表情清单</summary>
    public ObservableCollection<IdentifierRow> Expressions { get; } = new();

    /// <summary>动作清单（一行 = 一个动作组，不是单个 motion3 文件）</summary>
    public ObservableCollection<IdentifierRow> Motions { get; } = new();

    [ObservableProperty] private IdentifierRow? _selectedExpression;
    [ObservableProperty] private IdentifierRow? _selectedMotion;

    /// <summary>一行摘要，例如"表情 17 个 · 动作组 1 个"</summary>
    [ObservableProperty] private string _summary = "";

    /// <summary>扫描/读文件时遇到的麻烦（多行）。空 = 没问题</summary>
    [ObservableProperty] private string _problems = "";

    /// <summary>有没有问题 —— 单独一个属性是给 XAML 的 BoolToVisibility 用的</summary>
    [ObservableProperty] private bool _hasProblems;

   

    /// <summary>常态区间</summary>
    public ObservableCollection<RuleCard> Ranges { get; } = new();

    /// <summary>触发点</summary>
    public ObservableCollection<RuleCard> Triggers { get; } = new();

    /// <summary>失误反应：**固定两行**（小额 / 大额）</summary>
    public ObservableCollection<RuleCard> Misses { get; } = new();

    
    /// <summary>常驻组件：列出模型里的表情，勾上的那几个一直挂着</summary>
    public ObservableCollection<PersistentCard> Persistent { get; } = new();

    public bool HasRanges => Ranges.Count > 0;
    public bool HasTriggers => Triggers.Count > 0;
    public bool HasPersistent => Persistent.Count > 0;

    /// <summary>底部保存栏的"● 未保存"和两个按钮都看它</summary>
    public bool HasUnsavedChanges => _store.IsDirty;

    /// <summary>一句话回执（保存了 / 放弃了 / 失败了）—— View 接过去弹 Toast</summary>
    public event Action<string>? Notify;

    /// <summary>点了小卡片 → 让 View 去调预览区（VM 不认识 WPF 控件，所以走事件）</summary>
    public event Action<PreviewRequest>? PreviewRequested;

    /// <summary>常驻层变了 → 让 View 通知预览区**立刻重发**</summary>
    public event Action? PersistentChanged;

    /// <summary>
    /// 点一张规则小卡片 = 试播一小段。
    /// 页面协议上就是"试播表情 + 播一次动作"两条消息（各自独立，§3.1 的解耦）。
    /// </summary>
    [RelayCommand]
    private void PreviewCard(RuleCard? card)
    {
        if (card is null || !card.HasReaction) return;

        // 语音**不发**：预览区按设计不播声音（见 `PreviewPane` 文件头第 ② 条）
        PreviewRequested?.Invoke(new PreviewRequest(card.Expression, card.Parameters, card.Action));
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Save()
    {
        try
        {
            _store.SaveConfig();
            Notify?.Invoke("已保存到当前档案");
        }
        catch (Exception ex)
        {
            Notify?.Invoke("保存失败：" + ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Discard()
    {
        _store.ReloadConfig();
        Reload();
        Notify?.Invoke("已放弃改动");
    }

    /// <summary>勾选一个常驻组件 → 立刻写进配置（未保存标记由 `MarkConfigDirty` 带起来）</summary>
    private void OnPersistentToggled(PersistentCard card, bool isOn)
    {
        var parts = _store.Config.PersistentParts;
        var existing = parts.FirstOrDefault(p => string.Equals(p, card.Id, StringComparison.OrdinalIgnoreCase));

        if (isOn)
        {
            if (existing is null) parts.Add(card.Id);
        }
        else if (existing is not null)
        {
            parts.Remove(existing);
        }

        _store.MarkConfigDirty();
        PersistentChanged?.Invoke();
    }

    // ------------------------------------------------------------
    // 语音与口型（同样是"配置"：改完立刻进内存 + 标脏；生效方式是重载）
    //
    // 语音和表情是**完全解耦**的两条链：表情管"演什么"、语音管"说什么"，
    // 所以这里全是独立开关 —— 关掉语音不影响表情，反之亦然。
    // ------------------------------------------------------------

    public bool VoiceEnabled
    {
        get => _store.Config.Voice.Enabled;
        set { if (value == VoiceEnabled) return; _store.Config.Voice.Enabled = value; Mark(); }
    }

    /// <summary>语音文件所在目录（只读框 + 浏览：手打路径太容易错，而错了不报错、只是没声音）</summary>
    public string VoiceDirectory
    {
        get => _store.Config.Voice.Directory;
        set
        {
            if (value == VoiceDirectory) return;
            _store.Config.Voice.Directory = value;
            Mark();
        }
    }

    /// <summary>音量（配置里是 0~1，滑块直接给这个范围）</summary>
    public double VoiceVolume
    {
        get => _store.Config.Voice.Volume;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), 0.0, 1.0);
            if (Math.Abs(clamped - _store.Config.Voice.Volume) < 0.001) return;
            _store.Config.Voice.Volume = clamped;
            Mark();
        }
    }

    /// <summary>最小间隔毫秒（节流：防止一直说话）</summary>
    public double VoiceMinIntervalMs
    {
        get => _store.Config.Voice.MinIntervalMs;
        set
        {
            var clamped = Math.Clamp((int)Math.Round(value), 0, 5000);
            if (clamped == _store.Config.Voice.MinIntervalMs) return;
            _store.Config.Voice.MinIntervalMs = clamped;
            Mark();
        }
    }

    /// <summary>情绪清单（一个情绪 = 语音目录下的一个子文件夹）—— 只读展示</summary>
    public string VoiceEmotionsText
    {
        get
        {
            var list = _store.Config.Voice.Emotions;
            return list.Count > 0 ? string.Join("、", list) : "（还没有情绪）";
        }
    }

    public bool MouthEnabled
    {
        get => _store.Config.Mouth.Enabled;
        set { if (value == MouthEnabled) return; _store.Config.Mouth.Enabled = value; Mark(); }
    }

    /// <summary>口型参数名（**留空 = 自动从模型里找**）</summary>
    public string MouthParameter
    {
        get => _store.Config.Mouth.Parameter;
        set
        {
            if (value == MouthParameter) return;
            _store.Config.Mouth.Parameter = value ?? "";
            Mark();
        }
    }

    /// <summary>口型幅度（0~1）</summary>
    public double MouthStrength
    {
        get => _store.Config.Mouth.Strength;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), 0.0, 1.0);
            if (Math.Abs(clamped - _store.Config.Mouth.Strength) < 0.001) return;
            _store.Config.Mouth.Strength = clamped;
            Mark();
        }
    }

    /// <summary>配置类改动共用的收尾（标脏 + 通知界面；底部"● 未保存"就会亮）</summary>
    private void Mark()
    {
        _store.MarkConfigDirty();
        OnPropertyChanged(nameof(VoiceEnabled));
        OnPropertyChanged(nameof(VoiceDirectory));
        OnPropertyChanged(nameof(VoiceVolume));
        OnPropertyChanged(nameof(VoiceMinIntervalMs));
        OnPropertyChanged(nameof(VoiceEmotionsText));
        OnPropertyChanged(nameof(MouthEnabled));
        OnPropertyChanged(nameof(MouthParameter));
        OnPropertyChanged(nameof(MouthStrength));
    }
    // ------------------------------------------------------------
    // 表现策略（这是"配置"，所以改动**立刻写进内存 + 标脏**，和常驻组件同一条路）
    //
    // ★ 下拉的选项**逐字照抄 `PluginConfig` 里的字面量**（定稿 §8.3 的硬规矩）：
    //   解析层对未知值是"回落默认、保留原文"——写错的值不报错，只是静默失效，
    //   用户在下拉里选了、保存了，程序读回来不认识，表现就是"选了等于没选"。
    //   所以这两个数组里的字必须和 `PerformanceConfig` 里那两行一模一样。
    //
    // 生效方式是**重载**（设置项总表就是这么标的）：改了要重启悬浮窗才生效，
    // 所以这里不假装"立刻能看到".
    // ------------------------------------------------------------

    /// <summary>时长策略的选项（值就是配置里存的字面量）</summary>
    public IReadOnlyList<string> LengthStrategies { get; } = new[] { "取最长", "跟随表情" };

    /// <summary>重复触发策略的选项（同上）</summary>
    public IReadOnlyList<string> RepeatStrategies { get; } = new[] { "保留正在播放", "切换到新的" };

    public string LengthStrategy
    {
        get => _store.Config.Performance.LengthStrategyText;
        set
        {
            if (value == LengthStrategy) return;
            _store.Config.Performance.LengthStrategyText = value;
            _store.MarkConfigDirty();
            OnPropertyChanged();
        }
    }

    public string RepeatStrategy
    {
        get => _store.Config.Performance.RepeatStrategyText;
        set
        {
            if (value == RepeatStrategy) return;
            _store.Config.Performance.RepeatStrategyText = value;
            _store.MarkConfigDirty();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 表情持续毫秒（瞬时表情挂多久）。
    /// 用滑块给（用户偏好"能拖的就别让人手打"），所以是 double；
    /// 写回配置时夹到 100~5000 —— 体检那条只对"跟随表情"策略下的过小值报警，
    /// 但 0 或负数在任何策略下都没意义。
    /// </summary>
    public double ExpressionDurationMs
    {
        get => _store.Config.Performance.ExpressionDurationMs;
        set
        {
            var clamped = Math.Clamp((int)Math.Round(value), 100, 5000);
            if (clamped == _store.Config.Performance.ExpressionDurationMs) return;

            _store.Config.Performance.ExpressionDurationMs = clamped;
            _store.MarkConfigDirty();
            OnPropertyChanged();
        }
    }
    // ------------------------------------------------------------
    // 编辑弹窗
    // ------------------------------------------------------------

    /// <summary>弹窗开着没有</summary>
    [ObservableProperty] private bool _isEditing;

    /// <summary>正在编辑配置里的第几条；**-1 = 新增**</summary>
    private int _editIndex = -1;

    /// <summary>正在编辑哪一张卡片的规则（保存/删除要按它分派）</summary>
    private RuleKind _editKind = RuleKind.None;

    [ObservableProperty] private string _editMinText = "0";
    [ObservableProperty] private string _editMaxText = "";
    [ObservableProperty] private bool _editMaxUnlimited = true;
    [ObservableProperty] private string? _editExpression = "";

    // 触发点 / 失误反应的三个槽位（和区间共用同一个弹窗，按 `IsReactionEdit` 显示）
    [ObservableProperty] private string? _editReactionExpression = "";
    [ObservableProperty] private string? _editReactionAction = "";
    [ObservableProperty] private string? _editReactionEmotion = "";

    /// <summary>表情下拉的候选：第一项是"（不配）"（空标识）</summary>
    public ObservableCollection<IdChoice> ExpressionChoices { get; } = new();

    /// <summary>动作下拉的候选（同上，第一项不配）</summary>
    public ObservableCollection<IdChoice> MotionChoices { get; } = new();

    /// <summary>语音情绪下拉的候选（情绪名就是文件夹名，不来自模型）</summary>
    public ObservableCollection<IdChoice> EmotionChoices { get; } = new();

    public string EditTitle => _editKind switch
    {
        RuleKind.Trigger => _editIndex < 0 ? "新增触发点" : "编辑触发点",
        // 失误反应固定两档，所以是"编辑小额/大额"，没有新增
        RuleKind.Miss => _editIndex == 0 ? "编辑小额失误" : "编辑大额失误",
        _ => _editIndex < 0 ? "新增区间" : "编辑区间"
    };

    /// <summary>那个数字框叫什么（三张卡片含义不同）</summary>
    public string EditNumberLabel => _editKind switch
    {
        RuleKind.Trigger => "连击阈值",
        RuleKind.Miss => "门槛（连击）",
        _ => "最小连击"
    };

    /// <summary>是不是"区间"那种编辑（只有它有最大/以上这种字段）</summary>
    public bool IsRangeEdit => _editKind == RuleKind.Range;

    /// <summary>要不要显示三个槽位（触发点 / 失误都是"反应"）</summary>
    public bool IsReactionEdit => _editKind is RuleKind.Trigger or RuleKind.Miss;

    /// <summary>弹窗里的即时反馈（数值打错、保存出问题都在这里说）</summary>
    [ObservableProperty] private string _editError = "";

    /// <summary>有没有错要显示（XAML 用它决定那行红字显不显示）</summary>
    public bool HasEditError => EditError.Length > 0;

    partial void OnEditErrorChanged(string value) => OnPropertyChanged(nameof(HasEditError));

    /// <summary>最大值那个输入框能不能用（勾了「以上」就灰掉）</summary>
    public bool EditMaxEnabled => !EditMaxUnlimited;

    partial void OnEditMaxUnlimitedChanged(bool value) => OnPropertyChanged(nameof(EditMaxEnabled));

    /// <summary>编辑已有的那一条时才能删（新增的时候没有可删的）</summary>
    /// <summary>能不能删：区间和触发点可以，**失误反应不行**（它就是固定两档，删掉没有意义）</summary>
    public bool CanDelete => _editKind is RuleKind.Range or RuleKind.Trigger;

    [RelayCommand]
    private void BeginNewRange()
    {
        _editIndex = -1;
        _editKind = RuleKind.Range;
        EditMinText = "0";
        EditMaxText = "";
        EditMaxUnlimited = true;
        EditExpression = "";
        AfterBegin();
    }

    [RelayCommand]
    private void BeginEditRange(RuleCard? card)
    {
        if (card is null || card.Index < 0 || card.Index >= _store.Config.Ranges.Count) return;

        var range = _store.Config.Ranges[card.Index];
        _editIndex = card.Index;
        _editKind = RuleKind.Range;

        EditMinText = range.Min.ToString();
        EditMaxText = range.Max?.ToString() ?? "";
        EditMaxUnlimited = range.Max is null;
        EditExpression = range.Expression;
        AfterBegin();
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    /// <summary>保存：按正在编辑哪种规则分派</summary>
    [RelayCommand]
    private void SaveEdit()
    {
        switch (_editKind)
        {
            case RuleKind.Range: SaveRangeEdit(); break;
            case RuleKind.Trigger: SaveTriggerEdit(); break;
            case RuleKind.Miss: SaveMissEdit(); break;
        }
    }

    /// <summary>删除：只有区间和触发点走得到（失误那张卡片 `CanDelete` 是 false）</summary>
    [RelayCommand]
    private void DeleteEditing()
    {
        switch (_editKind)
        {
            case RuleKind.Range: DeleteRangeEditing(); break;
            case RuleKind.Trigger: DeleteTriggerEditing(); break;
            // Miss：**不给删**（固定两档）
        }
    }

    /// <summary>
    /// 把弹窗里的值写回配置。
    /// **只改内存、不落盘** —— 落盘仍然靠底部那个「保存」（和别页一致，
    /// 也让用户能一次性攒几条改动再存）。
    /// </summary>
    // ------------------------------------------------------------
    // 触发点（和区间同构，只是多了两个槽位）
    // ------------------------------------------------------------

    [RelayCommand]
    private void BeginNewTrigger()
    {
        _editIndex = -1;
        _editKind = RuleKind.Trigger;
        EditMinText = "50";
        EditReactionExpression = "";
        EditReactionAction = "";
        EditReactionEmotion = "";
        AfterBegin();
    }

    [RelayCommand]
    private void BeginEditTrigger(RuleCard? card)
    {
        if (card is null || card.Index < 0 || card.Index >= _store.Config.ComboTriggers.Count) return;

        var trigger = _store.Config.ComboTriggers[card.Index];
        _editIndex = card.Index;
        _editKind = RuleKind.Trigger;
        EditMinText = trigger.Threshold.ToString();
        EditReactionExpression = trigger.Reaction.Expression;
        EditReactionAction = trigger.Reaction.Action;
        EditReactionEmotion = trigger.Reaction.VoiceEmotion;
        AfterBegin();
    }

    // ------------------------------------------------------------
    // 失误反应
    // ------------------------------------------------------------

    [RelayCommand]
    private void BeginEditMiss(RuleCard? card)
    {
        if (card is null || card.Index is < 0 or > 1) return;

        var miss = _store.Config.Miss;
        var (threshold, reaction) = card.Index == 0
            ? (miss.SmallThreshold, miss.SmallReaction)
            : (miss.BigThreshold, miss.BigReaction);

        _editIndex = card.Index;
        _editKind = RuleKind.Miss;
        EditMinText = threshold.ToString();
        EditReactionExpression = reaction.Expression;
        EditReactionAction = reaction.Action;
        EditReactionEmotion = reaction.VoiceEmotion;
        AfterBegin();
    }

    /// <summary>打开弹窗的共同收尾（各 Begin 都要走一遍，免得漏了哪一处通知）</summary>
    private void AfterBegin()
    {
        EditError = "";
        OnPropertyChanged(nameof(EditTitle));
        OnPropertyChanged(nameof(EditNumberLabel));
        OnPropertyChanged(nameof(IsRangeEdit));
        OnPropertyChanged(nameof(IsReactionEdit));
        OnPropertyChanged(nameof(CanDelete));
        IsEditing = true;
    }

    [RelayCommand]
    private void SaveRangeEdit()
    {
        if (!int.TryParse(EditMinText.Trim(), out var min))
        {
            EditError = "最小值要是个整数。";
            return;
        }

        int? max = null;
        if (!EditMaxUnlimited)
        {
            if (!int.TryParse(EditMaxText.Trim(), out var parsed))
            {
                EditError = "最大值要是个整数（想表示「以上」就把那个勾打上）。";
                return;
            }

            if (parsed < min)
            {
                // 这条定稿里是要体检报的，但当场说更省事（用户不用去体检页才发现）
                EditError = "最大值不能小于最小值。";
                return;
            }

            max = parsed;
        }

        var target = new ComboRanger
        {
            Min = min,
            Max = max,
            Expression = (EditExpression ?? "").Trim()
        };

        if (_editIndex < 0) _store.Config.Ranges.Add(target);
        else _store.Config.Ranges[_editIndex] = target;

        // 区间的顺序有意义（状态机逐个比、命中就 break），所以排一下：
        // 用户新加一条小的却排在后面，会很莫名。
        _store.Config.Ranges.Sort((a, b) => a.Min.CompareTo(b.Min));

        FinishEdit("已改动，记得点保存");
    }

    /// <summary>读弹窗里那个数字（三张卡片共用；读不出来就报错，返回 false）</summary>
    private bool TryReadNumber(out int value, string what, int minimum)
    {
        if (!int.TryParse(EditMinText.Trim(), out value))
        {
            EditError = what + "要是个整数。";
            return false;
        }

        if (value < minimum)
        {
            EditError = $"{what}不能小于 {minimum}。";
            return false;
        }

        return true;
    }

    /// <summary>触发点：阈值必须为正（体检也会报"非正数"，但当场说更省事）</summary>
    private void SaveTriggerEdit()
    {
        if (!TryReadNumber(out var threshold, "阈值", minimum: 1)) return;

        var target = new ComboTrigger
        {
            Threshold = threshold,
            Reaction = new ReactionConfig
            {
                Expression = (EditReactionExpression ?? "").Trim(),
                Action = (EditReactionAction ?? "").Trim(),
                VoiceEmotion = (EditReactionEmotion ?? "").Trim()
            }
        };

        if (_editIndex < 0) _store.Config.ComboTriggers.Add(target);
        else _store.Config.ComboTriggers[_editIndex] = target;

        // 阈值小的排前面：和区间同一个道理（顺序是有意义的，乱序读起来莫名）
        _store.Config.ComboTriggers.Sort((a, b) => a.Threshold.CompareTo(b.Threshold));

        FinishEdit("已改动，记得点保存");
    }

    /// <summary>失误反应：只改门槛和反应，**不新增也不删除**</summary>
    private void SaveMissEdit()
    {
        if (!TryReadNumber(out var threshold, "门槛", minimum: 0)) return;

        var reaction = new ReactionConfig
        {
            Expression = (EditReactionExpression ?? "").Trim(),
            Action = (EditReactionAction ?? "").Trim(),
            VoiceEmotion = (EditReactionEmotion ?? "").Trim()
        };

        var miss = _store.Config.Miss;
        if (_editIndex == 0) { miss.SmallThreshold = threshold; miss.SmallReaction = reaction; }
        else { miss.BigThreshold = threshold; miss.BigReaction = reaction; }

        // 大额门槛 ≤ 小额门槛是体检第 11 条要报的，但这里能当场说清
        if (miss.BigThreshold <= miss.SmallThreshold)
        {
            EditError = "大额门槛要大于小额门槛。";
            return;
        }

        FinishEdit("已改动，记得点保存");
    }

    /// <summary>保存成功后的共同收尾</summary>
    private void FinishEdit(string message)
    {
        _store.MarkConfigDirty();
        Reload();
        IsEditing = false;
        Notify?.Invoke(message);
    }

    private void DeleteTriggerEditing()
    {
        if (_editIndex < 0 || _editIndex >= _store.Config.ComboTriggers.Count) return;

        _store.Config.ComboTriggers.RemoveAt(_editIndex);
        FinishEdit("已删除，记得点保存");
    }

    /// <summary>删掉正在编辑的那一条（弹窗里的「删除」按钮）</summary>
    [RelayCommand]
    private void DeleteRangeEditing()
    {
        if (_editIndex < 0 || _editIndex >= _store.Config.Ranges.Count) return;

        _store.Config.Ranges.RemoveAt(_editIndex);
        FinishEdit("已删除，记得点保存");
    }

    /// <summary>
    /// 重新扫一遍模型。
    /// 在「形象」页换了模型目录/入口之后按一下 —— 这一页自己不会知道那件事
    ///（它只在构造时扫一次，见文件头的说明）。
    /// </summary>
    [RelayCommand]
    private void Reload()
    {
        Expressions.Clear();
        Motions.Clear();

        var directory = _store.Config.Model.Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Summary = "还没选模型";
            SetProblems("找不到模型目录：" + directory + "\n去「形象」页选一个模型目录。");
            return;
        }

        var scan = ModelScanner.Scan(directory, _store.Config.Model.Entry);

        // 表情清单：把 3.exp3 这种标识配上注册名
        var catalog = ExpressionCatalog.Build(scan, directory);

        // 引用次数：同一个标识被多少条规则用到。"0"是有意义的 ——
        // 它正是"配了半天没人用"和"改名之后漏绑"的线索（定稿 §3.1 要求保留这个标记）
        var used = ExpressionReferences.CountBy(_store.Config);

        foreach (var resource in scan.Expressions)
        {
            var display = catalog.DisplayName(resource.Id);

            Expressions.Add(new IdentifierRow(
                Id: resource.Id,
                Name: display,
                // 没有 cdi3 名字时 DisplayName 会退回标识本身，那就别把它显示两遍
                Detail: display == resource.Id ? "" : resource.Id,
                References: Count(used, resource.Id),
                Parameters: catalog.ParametersOf(resource.Id)));
        }

        foreach (var resource in scan.Motions)
        {
            Motions.Add(new IdentifierRow(
                Id: resource.Id,
                Name: resource.Id,          // 动作组的"名字"就是组名（注册进 model3.json 的那个）
                Detail: resource.Files.Count > 1 ? $"{resource.Files.Count} 个变体" : "",
                References: Count(used, resource.Id),
                Parameters: Array.Empty<ExpressionParam>()));   // 动作交给库自己播，不需要参数
        }

        
        Summary = $"{Expressions.Count} 表情 · {Motions.Count} 动作组";
        SetProblems(string.Join("\n", scan.Problems.Concat(catalog.Problems)));

        BuildCards(catalog, scan);
    }

    /// <summary>
    /// 把四张大卡片的行搭出来。
    ///
    /// 摘要格式照 §3.2 的例子来：`0–49 · 星星眼.exp3`、`50 连击 · 3.exp3 / — / 高兴`
    /// —— 空槽位写「—」（**不是空着**：空着看不出"这里少配了一样"，而定稿明确要求
    /// 三个槽位一眼能看全）。
    /// </summary>
    private void BuildCards(ExpressionCatalog catalog, ModelScanResult scan)
    {
        // 模型里**真实存在**的标识 —— 摘要要按它标出"配了但模型里没有"，
        // 可播的字段也按它过滤（否则卡片点了没反应，正是 §1.2 禁止的东西）。
        // 体检会报这类问题，但用户点卡片的那一刻就该看见，不能等他去翻体检页。
        var available = new HashSet<string>(
            scan.Expressions.Select(e => e.Id).Concat(scan.Motions.Select(m => m.Id)),
            StringComparer.OrdinalIgnoreCase);

        // 语音情绪**不来自模型**，来自语音配置里那份情绪清单（每项 = 一个子文件夹名）。
        // 一开始我拿"模型里的标识"去查情绪，于是三条触发点全被标上"模型里没有"——
        // 又一个假警报。**不同种类的标识要跟不同的清单对**。
        var emotions = new HashSet<string>(_store.Config.Voice.Emotions, StringComparer.OrdinalIgnoreCase);

        Ranges.Clear();
        for (var i = 0; i < _store.Config.Ranges.Count; i++)
        {
            var range = _store.Config.Ranges[i];
            Ranges.Add(new RuleCard(
                Summary: $"{range.Min}–{(range.Max is null ? "以上" : range.Max.Value.ToString())}" +
                         $" · {Slot(range.Expression, catalog, available)}",
                Expression: Playable(range.Expression, available),
                Parameters: catalog.ParametersOf(Playable(range.Expression, available)),
                Action: "",
                Index: i));
        }

        // 弹窗里那个表情下拉的候选（第一项 = 不配）
        ExpressionChoices.Clear();
        ExpressionChoices.Add(new IdChoice("", "（不配）"));
        foreach (var resource in scan.Expressions)
            ExpressionChoices.Add(new IdChoice(resource.Id, catalog.DisplayName(resource.Id)));

        MotionChoices.Clear();
        MotionChoices.Add(new IdChoice("", "（不配）"));
        foreach (var resource in scan.Motions)
            MotionChoices.Add(new IdChoice(resource.Id, resource.Id));

        EmotionChoices.Clear();
        EmotionChoices.Add(new IdChoice("", "（不配）"));
        foreach (var emotion in _store.Config.Voice.Emotions)
            EmotionChoices.Add(new IdChoice(emotion, emotion));

        Triggers.Clear();
        foreach (var trigger in _store.Config.ComboTriggers)
        {
            Triggers.Add(ReactionCard($"{trigger.Threshold} 连击", trigger.Reaction, catalog, available, emotions,
                                      _store.Config.ComboTriggers.IndexOf(trigger)));
        }

        Misses.Clear();
        var miss = _store.Config.Miss;
        // 失误反应固定两档，索引就是它的档位：0 = 小额、1 = 大额
        Misses.Add(ReactionCard($"小额 {miss.SmallThreshold}", miss.SmallReaction, catalog, available, emotions, index: 0));
        Misses.Add(ReactionCard($"大额 {miss.BigThreshold}", miss.BigReaction, catalog, available, emotions, index: 1));

        Persistent.Clear();
        var on = new HashSet<string>(_store.Config.PersistentParts, StringComparer.OrdinalIgnoreCase);
        foreach (var resource in scan.Expressions)
        {
            Persistent.Add(new PersistentCard(
                id: resource.Id,
                name: catalog.DisplayName(resource.Id),
                isOn: on.Contains(resource.Id),
                onToggled: OnPersistentToggled));
        }

        OnPropertyChanged(nameof(HasRanges));
        OnPropertyChanged(nameof(HasTriggers));
        OnPropertyChanged(nameof(HasPersistent));
    }

    /// <summary>带"反应"的小卡片（触发点 / 失误反应共用）：三个槽位依次是 表情 / 动作 / 情绪</summary>
    /// <summary>
    /// 带"反应"的小卡片（触发点 / 失误反应共用）。
    /// **`index` 必须传**：铅笔入口靠它回到配置里那一条；
    /// </summary>
    private static RuleCard ReactionCard(string head, ReactionConfig reaction, ExpressionCatalog catalog,
                                         IReadOnlySet<string> available, IReadOnlySet<string> emotions, int index)
        => new(Summary: $"{head} · {Slot(reaction.Expression, catalog, available)} / " +
                       $"{Slot(reaction.Action, catalog, available)} / " +
                       $"{SlotPlain(reaction.VoiceEmotion, emotions)}",
               Expression: Playable(reaction.Expression, available),
               Parameters: catalog.ParametersOf(Playable(reaction.Expression, available)),
               Action: Playable(reaction.Action, available),
               Index: index);

    /// <summary>摘要里那一格显示什么：**没配 = 「—」**；配了但模型里没有 → **标出来**</summary>
    private static string Slot(string? id, ExpressionCatalog catalog, IReadOnlySet<string> available)
    {
        if (string.IsNullOrWhiteSpace(id)) return "—";

        var name = catalog.DisplayName(id);
        return available.Contains(id.Trim()) ? name : name + "（模型里没有）";
    }

    /// <summary>语音情绪那一格：情绪名**本来就是文件夹名**，没有"人话名字"可翻，只查在不在清单里</summary>
    private static string SlotPlain(string? name, IReadOnlySet<string> known)
    {
        if (string.IsNullOrWhiteSpace(name)) return "—";
        return known.Contains(name.Trim()) ? name : name + "（情绪清单里没有）";
    }

    /// <summary>能播才发出去：模型里没有的标识发过去也是白搭（页面找不到那个表情）</summary>
    private static string Playable(string? id, IReadOnlySet<string> available)
        => string.IsNullOrWhiteSpace(id) || !available.Contains(id.Trim()) ? "" : id.Trim();

    private static int Count(IReadOnlyDictionary<string, int> used, string id)
        => used.TryGetValue(id, out var n) ? n : 0;

    private void SetProblems(string text)
    {
        Problems = text;
        HasProblems = text.Length > 0;
    }
}

/// <summary>
/// 清单里的一行：一个表情，或者一个动作组。
///
/// `Parameters` 只有表情用得上（页面要拿它往模型上写参数）；动作交给库自己播。
/// </summary>
public sealed record IdentifierRow(
    string Id,
    string Name,
    string Detail,
    int References,
    IReadOnlyList<ExpressionParam> Parameters)
{
    /// <summary>被引用过（有规则用到它）</summary>
    public bool HasReferences => References > 0;

    /// <summary>一次都没被引用 —— 界面要标出来（漏绑 / 重绑就靠这个发现）</summary>
    public bool Unused => References == 0;

    public string ReferenceText => $"已被引用 {References} 处";

    /// <summary>
    /// 第二行（标识 · 引用次数）。
    ///
    /// ★ 2026-09-23 用户看图之后改的：原来是**三行**（名字 / 标识 / 引用次数），
    /// 每项三行、行间又没分界，整列糊成一片。现在标识和引用次数并成一行，
    /// 用户扫一眼就是"名字 + 补充信息"两行。
    /// </summary>
    public string MetaText => Detail.Length > 0 ? $"{Detail} · {ReferenceText}" : ReferenceText;
}

/// <summary>
/// 一张**规则小卡片**（常态区间 / 触发点 / 失误反应共用）。
///
/// 这三张卡片的行长得一模一样：一行摘要 + 点一下试播；差别只在摘要怎么写、点了播什么。
/// 所以三类共用一份记录、一套模板 —— §8.3 说的"卡片只做摘要与预览入口"。
/// </summary>
public sealed record RuleCard(
    string Summary,
    string Expression,
    IReadOnlyList<ExpressionParam> Parameters,
    string Action,
    int Index = -1)
{
    /// <summary>这条规则有没有反应可播（三个槽位全空时按下去不会有任何反应，所以挡住它）</summary>
    public bool HasReaction => Expression.Length > 0 || Action.Length > 0;
}

/// <summary>
/// 常驻组件的一行：**勾上 = 这个表情的参数一直挂着**（"列出＝开"）。
///
/// 它和上面三张卡片的点击语义**故意不同**（§3.1）：那三张是"播一小段"，
/// 这一张是"一直挂着" —— 因为常驻组件本来就是"永远生效的常态表现"
///
/// 实现上它必须是 `ObservableObject`（要能被勾），所以不能像 `RuleCard` 那样是 record。
/// </summary>
public sealed partial class PersistentCard : ObservableObject
{
    /// <summary>勾选回调：改动要落到配置上 —— 由 VM 处理，因为它才认识 `ConfigStore`</summary>
    private readonly Action<PersistentCard, bool> _onToggled;

    public PersistentCard(string id, string name, bool isOn, Action<PersistentCard, bool> onToggled)
    {
        Id = id;
        Name = name;
        _onToggled = onToggled;

        // 注意这里是给**字段**赋值：走属性会触发回调，载入时就把配置标脏了
        _isOn = isOn;
    }

    /// <summary>配置里存的就是这个标识</summary>
    public string Id { get; }

    /// <summary>给人看的名字（没有 cdi3 名字时就是标识本身）</summary>
    public string Name { get; }

    [ObservableProperty] private bool _isOn;

    partial void OnIsOnChanged(bool value) => _onToggled(this, value);
}

/// <summary>弹窗正在编辑哪一张卡片的规则（保存/删除按它分派）</summary>
public enum RuleKind
{
    None,
    Range,       // 常态区间
    Trigger,     // 触发点
    Miss         // 失误反应（固定两档：只能改，不能增删）
}

/// <summary>
/// 下拉里的一个候选：**存标识、显示人话名字**（"（不配）"那一项的标识是空串）。
/// </summary>
public sealed record IdChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 一次"试播"：小卡片点了以后要让页面做什么。
/// **语音不在里面** —— 预览区按设计不播声音（见 `PreviewPane` 文件头第 ② 条）。
/// </summary>
public sealed record PreviewRequest(string Expression, IReadOnlyList<ExpressionParam> Parameters, string Action);
