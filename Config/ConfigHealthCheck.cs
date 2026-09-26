// ============================================================
// ConfigHealthCheck.cs —— 配置体检：把"宽松加载"悄悄放过的问题列出来
//
// 【它解决什么问题】
//   配置层一直是"宽松加载、永不崩"：写错字、门槛配反、引用了不存在的表情 ——
//   程序照样跑，**只是那一条静默失效**。用户不知道自己配错了，只会觉得
//   "这个功能怎么没反应"。体检报告就是这条策略的必要配对（UI框架_定稿.md §3.4）：
//     解析层宽松兜底 → 保证不崩
//     体检报告       → 保证**不静默失效**
//
// 【三档的语义】
//   Error   —— 兜不住，这份配置真用不了。必须改掉，**没有"忽略"这个选项**
//   Warning —— 能兜住，但用户有权知道并决定：改掉，或者**明确点"忽略"**才能继续
//   Info    —— 不影响运行，只是"你可能不是这么想的"
//
//   「严格 / 宽松」只作用于"区间与标识"那一类（第 7~10 条，见 RangeCheckMode）——
//   模式的名字本来就叫"区间检查"。其余条目大多也能兜住（阈值重复会被去掉、
//   没开穿透照样能跑），但级别固定、不随模式变 —— 否则用户会以为"调一下模式"
//   就能让所有问题消失。
//
// 【为什么"外部世界"是参数】
//   要判断"目录在不在""情绪文件夹里有没有文件"，但不**不自己**去摸文件系统 ——
//   那样这个类就没法在控制台里测（总不能为了测一条规则去建一堆真目录）。
//   所以这些打包成 HealthEnvironment，由调用方探测好传进来。
//
// 【FieldPath：给界面用的坐标】
//   每条问题都带 FieldPath，指向配置里出问题的那一项。它是**坐标**，不是"该怎么改"的
//   建议 —— 要不要跳过去、跳过去高不高亮，是界面层的决定，这里不表态。
//
//   写法（与 config.json 的形状一一对应）：
//     · 对象字段：点号分级，中文键名原样 —— 界面感知.打歌.悬浮窗.点击穿透
//     · 数组元素：`[N]`，**从 1 开始**（它最终是给人看的"第 N 条"）——
//       常态区间[3]、连击触发[2]、语音.情绪[2]、常驻组件[1]
//     · 能指到具体字段就指字段，指不到（整行不对、成对才算的问题）就指那一行 ——
//       常态区间[2].表情、连击触发[1].反应.瞬时表情、常态区间[2]（重叠）
//
//   **数组为什么一律用下标，不拿里面的值当路径**：
//     `root["语音"]["情绪"]["伤心"]` 在 JSON 里根本取不到东西（数组没有那个键），
//     而且出现重复值时也无法唯一指向某一项。界面要的就是"第几行"，而 FieldPath
//     每次体检都重新生成、当场使用，不存在"用户删掉前面一项导致下标失效"的问题。
// ============================================================
namespace OsuLive2dOverlay;

/// <summary>体检出来的问题有多严重</summary>
public enum HealthLevel
{
    /// <summary>兜不住，配置真用不了 —— 必须改掉（没有"忽略"）</summary>
    Error,

    /// <summary>能兜住，但用户有权知道并决定：改掉，或者明确点"忽略"才能继续</summary>
    Warning,

    /// <summary>不影响运行，只是"你可能不是这么想的"</summary>
    Info
}

/// <summary>一条体检结果</summary>
/// <param name="Level">严重程度，语义见文件头</param>
/// <param name="Message">用户须知：问题是什么 + 后果，带上具体数值和名字</param>
/// <param name="FieldPath">给界面用：配置里的哪个字段（写法见文件头）</param>
public sealed record HealthIssue(HealthLevel Level, string Message, string FieldPath);

/// <summary>
/// 体检要知道的"外部世界"。做成参数而不是自己去摸文件系统 —— 纯逻辑类才能用控制台测。
/// </summary>
/// <param name="ModelDirectoryExists">模型目录在不在</param>
/// <param name="ModelEntryExists">模型入口文件在不在</param>
/// <param name="VoiceDirectoryExists">语音目录在不在</param>
/// <param name="KnownExpressionIds">模型里有哪些表情标识（来自 ModelScanner）</param>
/// <param name="KnownMotionIds">模型里有哪些动作标识</param>
/// <param name="VoiceEmotionFileCounts">每个情绪文件夹里有几个可用音频；没有这个情绪就是 0（或干脆没有这个键）</param>
/// <param name="TosuExeExists">「tosu 程序」那个文件在不在（**只在要查数据源时才探测**）</param>
/// <param name="OsuExeExists">「osu 程序」那个文件在不在（同上）</param>
public sealed record HealthEnvironment(
    bool ModelDirectoryExists,
    bool ModelEntryExists,
    bool VoiceDirectoryExists,
    IReadOnlySet<string> KnownExpressionIds,
    IReadOnlySet<string> KnownMotionIds,
    IReadOnlyDictionary<string, int> VoiceEmotionFileCounts,
    // ★ 2026-09-23 新增（第 21 条要用）。**带默认值**是有意的：
    //   而"没探测过"与"不存在"在这里的处理是一样的（不传 dataSource 就不查那一条）。
    bool TosuExeExists = false,
    bool OsuExeExists = false);

/// <summary>
/// 区间这类"能兜住"的检查，严不严格由用户定（定稿 §3.4：这个选项归"配置体检"页）。
/// 它只影响第 7~10 条 —— 超出这个范围调它没有任何效果，别指望它"一键让所有问题变错"。
///
/// **2026-09-20 挪走了**：它现在是配置数据的一部分（`体检.检查模式`），
/// 定义已移到 `Config/PluginConfig.cs`（同命名空间，这里照旧能用）。
/// 挪动的理由：否则 PluginConfig 会反向依赖本文件，连带所有"只需要配置类"的验证工程
/// 都得把体检一起链接进来。
/// </summary>
public static class ConfigHealthCheck
{
    /// <summary>两个连击阈值差多少就算"太接近"（两档几乎同时触发，看起来像卡了一下）</summary>
    public const int TooCloseThresholdGap = 10;

    /// <summary>表情持续时间短于这个毫秒数就算"短得看不清"</summary>
    public const int TooShortExpressionMs = 200;

    /// <summary>
    /// 给一份配置做体检。返回的问题清单可能为空（= 没毛病）。
    ///
    /// 一次把话说完：不是遇到第一个问题就返回 —— 用户改配置要一轮改完，不是改一条跑一次。
    /// </summary>
    /// <param name="config">**配置档案**（模型 / 规则 / 语音 / 界面感知）</param>
    /// <param name="env">"外部世界"（见 <see cref="HealthEnvironment"/>）</param>
    /// <param name="mode">区间与标识那几条的严格程度</param>
    /// <param name="dataSource">
    /// **软件设置**里的「数据源」那一段（★ 2026-09-23 加的参数，第 21 条要用）。
    ///
    /// 传 null = 不查数据源那一条 —— 这是**故意留的口子**：
    /// 数据源不在档案里（它跟着 settings.json 走，不随角色切换），
    /// 带默认值之后，老调用点一个字都不用改，而"要不要连数据源一起查"由调用方说了算。
    /// </param>
    public static IReadOnlyList<HealthIssue> Check(
        PluginConfig config, HealthEnvironment env, RangeCheckMode mode = RangeCheckMode.Lenient,
        DataSourceConfig? dataSource = null)
    {
        var issues = new List<HealthIssue>();

        // "能兜住"那一类的级别由模式决定；其余条目各自写死级别（见文件头）
        var recoverable = mode == RangeCheckMode.Strict ? HealthLevel.Error : HealthLevel.Warning;

        CheckModel(config, env, issues);
        CheckVoice(config, env, issues);
        CheckRanges(config, issues, recoverable);
        CheckResultRanges(config, issues, recoverable);
        CheckReferencedIds(config, env, issues, recoverable);
        CheckMiss(config, issues);
        CheckThresholds(config, issues);
        CheckScenes(config, issues);
        CheckPerformance(config, issues);

        if (dataSource is not null) CheckDataSource(dataSource, env, issues);

        return issues;
    }

    // ---------------- 模型：兜不住的三种 ----------------

    private static void CheckModel(PluginConfig config, HealthEnvironment env, List<HealthIssue> issues)
    {
        var entry = config.Model.Entry;

        // 入口为空和入口文件不存在是**两回事**，但只报其中一条就够（空的时候谈"文件不存在"没意义，
        // 所以是 else if）。入口通常由模型扫描器选出来，用户不该手打文件名。
        if (string.IsNullOrWhiteSpace(entry))
            issues.Add(new(HealthLevel.Error, "没有配置模型入口文件（「模型.入口」是空的），角色显示不出来", "模型.入口"));
        else if (!env.ModelEntryExists)
            issues.Add(new(HealthLevel.Error, $"模型入口文件不存在：{entry}", "模型.入口"));

        // 目录和入口是两个独立判断：目录在、但里面没有那个入口文件，同样得报出来
        if (!string.IsNullOrWhiteSpace(config.Model.Directory) && !env.ModelDirectoryExists)
            issues.Add(new(HealthLevel.Error, $"模型目录不存在：{config.Model.Directory}", "模型.目录"));
    }

    // ---------------- 语音：目录不存在是错误，情绪没文件是警告 ----------------

    private static void CheckVoice(PluginConfig config, HealthEnvironment env, List<HealthIssue> issues)
    {
        if (!config.Voice.Enabled) return;      // 没启用语音就别提语音的事

        if (!string.IsNullOrWhiteSpace(config.Voice.Directory) && !env.VoiceDirectoryExists)
            issues.Add(new(HealthLevel.Error, $"语音已启用，但语音目录不存在：{config.Voice.Directory}", "语音.目录"));

        // 空名字不算"配了情绪"：它在解析时会被直接跳过，等于没配
        var names = config.Voice.Emotions.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names.Count == 0)
        {
            issues.Add(new(HealthLevel.Warning, "语音已启用，但一个情绪都没配 —— 语音不会出声", "语音.情绪"));
            return;      // 一条都没配，就别再逐个情绪说"没有文件"了（那会刷一屏）
        }

        for (var i = 0; i < config.Voice.Emotions.Count; i++)
        {
            var name = config.Voice.Emotions[i];
            if (string.IsNullOrWhiteSpace(name)) continue;

            // 字典里没有这个情绪也当成 0：文件夹不存在和文件夹里没音频，对用户是同一件事
            var count = env.VoiceEmotionFileCounts.TryGetValue(name, out var n) ? n : 0;
            if (count == 0)
                issues.Add(new(HealthLevel.Warning,
                               $"语音情绪「{name}」下没有可用文件（文件夹不存在，或者里面没有音频），这个情绪不会出声",
                               $"语音.情绪[{i + 1}]"));
        }
    }

    // ---------------- 常态区间：能兜住的那一类，级别由模式决定 ----------------

    private static void CheckRanges(PluginConfig config, List<HealthIssue> issues, HealthLevel level)
    {
        if (config.Ranges.Count == 0) return;

        // 「最大」不写 = 以上：运行时它就是 [Min, int.MaxValue]
        // （建状态机时用的就是 `r.Max ?? int.MaxValue`）。体检必须用**同一套换算**，
        // 否则报告说的"重叠"和运行时真正会发生的重叠是两回事。
        static ComboRange Range(ComboRanger r) => new(r.Min, r.Max ?? int.MaxValue);

        // 报告里别把哨兵值印成 2147483647
        static string End(ComboRange r) => r.Max == int.MaxValue ? "以上" : r.Max.ToString();

        for (var i = 0; i < config.Ranges.Count; i++)
        {
            var r = config.Ranges[i];
            if (r.Max is not { } max || r.Min <= max) continue;

            issues.Add(new(level,
                           $"常态区间[{i + 1}] 的最小值（{r.Min}）比最大值（{max}）还大，这条规则会被忽略",
                           $"常态区间[{i + 1}]"));
        }

        // Min > Max 的非法项运行时会直接丢掉（建状态机时 Where(Min <= Max)）→ 不参与重叠比较，
        // 否则会报出运行时根本不存在的"重叠"。
        var legal = config.Ranges.Where(r => r.Min <= (r.Max ?? int.MaxValue)).ToList();

        foreach (var (_, a, _, b) in ComboTracker.FindOverlaps(legal, Range))
        {
            var indexA = config.Ranges.IndexOf(a) + 1;      // 1 起，给人看的"第几条"
            var indexB = config.Ranges.IndexOf(b) + 1;
            var ra = Range(a);
            var rb = Range(b);

            // 重叠处运行时的规则是"取第一个匹配的"（状态机里逐个比、命中就 break），
            // 所以那一段会被**前**一条接走，被盖住的是**后**一条 —— 该改的就是它。
            issues.Add(new(level,
                           $"常态区间[{indexA}]（{ra.Min}-{End(ra)}）和常态区间[{indexB}]（{rb.Min}-{End(rb)}）重叠，" +
                           $"重叠的那一段会用前一条（常态区间[{indexA}]），常态区间[{indexB}] 在那一段里用不到",
                           $"常态区间[{indexB}]"));
        }
    }

    // ---------------- 结算反应：和常态区间同一套检查（只是数字是小数） ----------------

    /// <summary>
    /// 结算反应的档位检查：越界的档 + 重叠。
    ///
    /// 和 <see cref="CheckRanges"/> 是同一个形状，差别只有一个：**这里的数是小数**
    ///（准确率 0~100）。`ComboTracker.FindOverlaps` 虽然泛型，但它要的是整数区间
    /// （<c>Func&lt;T, ComboRange&gt;</c>），套不上，所以重叠那段在这儿重写了一遍 ——
    /// **规则和它完全一致：重叠处运行时取前一条**（逐个比、命中就 break）。
    ///
    /// **刻意不报"档位之间有洞"**（用户 2026-09-26 定）：那是配置写法的选择，不是错误 ——
    /// 有人就是想"90 分以下不表态"。空档的代价是"那一段成绩没有反应"，而那是他自己配的。
    /// </summary>
    private static void CheckResultRanges(PluginConfig config, List<HealthIssue> issues, HealthLevel level)
    {
        var ranges = config.ResultRanges;
        if (ranges.Count == 0) return;

        // 报告里别把哨兵值印成 1.7976931348623157E+308
        static string End(AccuracyRange r) => r.Max is { } m ? m.ToString("0.##") : "以上";

        for (var i = 0; i < ranges.Count; i++)
        {
            var r = ranges[i];

            if (r.Min < 0 || r.Min > 100)
                issues.Add(new(level,
                               $"结算反应[{i + 1}] 的最小值（{r.Min:0.##}）不在 0~100 里 —— " +
                               "准确率永远落不进这一档",
                               $"结算反应[{i + 1}]"));

            if (r.Max is not { } max) continue;

            if (r.Min >= max)
                issues.Add(new(level,
                               $"结算反应[{i + 1}] 的最小值（{r.Min:0.##}）不小于最大值（{max:0.##}），" +
                               "这一档永远不会被用到",
                               $"结算反应[{i + 1}]"));
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            for (var j = i + 1; j < ranges.Count; j++)
            {
                var a = ranges[i];
                var b = ranges[j];
                var aMax = a.Max ?? double.MaxValue;
                var bMax = b.Max ?? double.MaxValue;

                // ★ **开区间**：`[a.Min, a.Max)` 和 `[b.Min, b.Max)` 重叠的判据是
                //     `a.Min < b.Max && b.Min < a.Max` —— **两边相等不算重叠**。
                //   所以 `[90,95)` 和 `[95,101)` 是**首尾相接、不重叠**的，
                //   而"首尾相接"恰好是推荐的配法（相邻档之间不留空隙）。
                if (a.Min >= bMax || b.Min >= aMax) continue;

                issues.Add(new(level,
                               $"结算反应[{i + 1}]（{a.Min:0.##}-{End(a)}）和结算反应[{j + 1}]（{b.Min:0.##}-{End(b)}）重叠，" +
                               $"重叠的那一段会用前一条（结算反应[{i + 1}]），结算反应[{j + 1}] 在那一段里用不到",
                               $"结算反应[{j + 1}]"));
            }
        }
    }

    // ---------------- 被引用的表情 / 动作标识：模型里到底有没有 ----------------

    /// <summary>一条"某处引用了某个标识"的记录：标识本身 + 人话说的出处 + 它的配置坐标</summary>
    private readonly record struct IdReference(string Id, string Where, string Field);

    private static void CheckReferencedIds(
        PluginConfig config, HealthEnvironment env, List<HealthIssue> issues, HealthLevel level)
    {
        // 空的标识不报：留空是合法的"不配这个"，不是"配错了"
        foreach (var (id, where, field) in ExpressionReferences(config))
            if (!string.IsNullOrWhiteSpace(id) && !env.KnownExpressionIds.Contains(id))
                issues.Add(new(level, $"{where}引用的表情「{id}」在模型里找不到，它不会播出来", field));

        foreach (var (id, where, field) in MotionReferences(config))
            if (!string.IsNullOrWhiteSpace(id) && !env.KnownMotionIds.Contains(id))
                issues.Add(new(level, $"{where}引用的动作「{id}」在模型里找不到", field));
    }

    /// <summary>配置里所有引用"表情标识"的位置（多一处就得在这里多列一行）</summary>
    private static IEnumerable<IdReference> ExpressionReferences(PluginConfig config)
    {
        for (var i = 0; i < config.PersistentParts.Count; i++)
            yield return new(config.PersistentParts[i], $"常驻组件[{i + 1}] ", $"常驻组件[{i + 1}]");

        for (var i = 0; i < config.Ranges.Count; i++)
            yield return new(config.Ranges[i].Expression, $"常态区间[{i + 1}] ",
                             $"常态区间[{i + 1}].表情");

        for (var i = 0; i < config.ComboTriggers.Count; i++)
        {
            var threshold = config.ComboTriggers[i].Threshold;
            yield return new(config.ComboTriggers[i].Reaction?.Expression ?? "",
                             $"{threshold} 连击触发 ", $"连击触发[{i + 1}].反应.瞬时表情");
        }

        yield return new(config.Miss.SmallReaction?.Expression ?? "", "失误（小额）", "失误触发.小额反应.瞬时表情");
        yield return new(config.Miss.BigReaction?.Expression ?? "", "失误（大额）", "失误触发.大额反应.瞬时表情");

        
        for (var i = 0; i < config.Touch.Count; i++)
            yield return new(config.Touch[i]?.Expression ?? "",
                             $"触摸反应[{i + 1}] ", $"触摸[{i + 1}].反应.瞬时表情");

        for (var i = 0; i < config.ResultRanges.Count; i++)
            yield return new(config.ResultRanges[i].Reaction?.Expression ?? "",
                             $"结算反应[{i + 1}] ", $"结算反应[{i + 1}].反应.瞬时表情");
    }

    /// <summary>配置里所有引用"动作标识"的位置</summary>
    private static IEnumerable<IdReference> MotionReferences(PluginConfig config)
    {
        yield return new(config.Model.IdleGroup, "待机动作", "模型.待机动作");

        for (var i = 0; i < config.ComboTriggers.Count; i++)
        {
            var threshold = config.ComboTriggers[i].Threshold;
            yield return new(config.ComboTriggers[i].Reaction?.Action ?? "",
                             $"{threshold} 连击触发 ", $"连击触发[{i + 1}].反应.动作");
        }

        yield return new(config.Miss.SmallReaction?.Action ?? "", "失误（小额）", "失误触发.小额反应.动作");
        yield return new(config.Miss.BigReaction?.Action ?? "", "失误（大额）", "失误触发.大额反应.动作");

        for (var i = 0; i < config.Touch.Count; i++)
            yield return new(config.Touch[i]?.Action ?? "",
                             $"触摸反应[{i + 1}] ", $"触摸[{i + 1}].反应.动作");

        for (var i = 0; i < config.ResultRanges.Count; i++)
            yield return new(config.ResultRanges[i].Reaction?.Action ?? "",
                             $"结算反应[{i + 1}] ", $"结算反应[{i + 1}].反应.动作");
    }

    // ---------------- 失误门槛：配反了那一档就永远用不到 ----------------

    private static void CheckMiss(PluginConfig config, List<HealthIssue> issues)
    {
        var miss = config.Miss;

        // 两级门槛的高低关系不对：小额那档会被大额那档完全盖住
        if (miss.BigThreshold > 0 && miss.BigThreshold <= miss.SmallThreshold)
            issues.Add(new(HealthLevel.Warning,
                           $"失误的大额门槛（{miss.BigThreshold}）不比小额门槛（{miss.SmallThreshold}）高，" +
                           "小额那一档永远用不到",
                           "失误触发.大额门槛"));

        if (miss.SmallThreshold == 0)
            issues.Add(new(HealthLevel.Info,
                           "失误的小额门槛是 0，一断连击就会有反应（打歌时很容易被安慰过头）",
                           "失误触发.小额门槛"));
    }

    // ---------------- 连击阈值：重复、非正数、太挤 ----------------

    private static void CheckThresholds(PluginConfig config, List<HealthIssue> issues)
    {
        if (config.ComboTriggers.Count == 0) return;

        for (var i = 0; i < config.ComboTriggers.Count; i++)
        {
            var threshold = config.ComboTriggers[i].Threshold;
            if (threshold > 0) continue;

            issues.Add(new(HealthLevel.Warning,
                           $"连击阈值 {threshold} 不是正数，运行时会被忽略",
                           $"连击触发[{i + 1}].阈值"));
        }

        // 重复：同一档配了不止一次（运行时去重，只有第一个生效）
        var seen = new HashSet<int>();
        for (var i = 0; i < config.ComboTriggers.Count; i++)
        {
            var threshold = config.ComboTriggers[i].Threshold;
            if (threshold <= 0 || seen.Add(threshold)) continue;

            issues.Add(new(HealthLevel.Warning,
                           $"连击阈值 {threshold} 配了不止一次，重复的会被去掉",
                           $"连击触发[{i + 1}].阈值"));
        }

        // 太挤：两档几乎同时触发（按去重后的有效阈值两两相邻比较，指后一个）
        var sorted = config.ComboTriggers.Select(t => t.Threshold)
                                       .Where(t => t > 0).Distinct().OrderBy(t => t).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - sorted[i - 1] > TooCloseThresholdGap) continue;

            var index = IndexOfThreshold(config, sorted[i]);
            issues.Add(new(HealthLevel.Info,
                           $"连击阈值 {sorted[i - 1]} 和 {sorted[i]} 只差 {sorted[i] - sorted[i - 1]}，两档会几乎同时触发",
                           $"连击触发[{index}].阈值"));
        }
    }

    /// <summary>某个阈值在配置里是第几条（1 起）—— 用来拼 FieldPath；找不到就退回 1</summary>
    private static int IndexOfThreshold(PluginConfig config, int threshold)
    {
        for (var i = 0; i < config.ComboTriggers.Count; i++)
            if (config.ComboTriggers[i].Threshold == threshold) return i + 1;

        return 1;
    }

    // ---------------- 界面感知：四个界面各一份档位 ----------------

    private static void CheckScenes(PluginConfig config, List<HealthIssue> issues)
    {
        var scenes = config.Scenes;
        if (scenes is null) return;      // 老配置还没迁移过：没有这一段就没什么可查的

        var profiles = new (string Name, SceneProfile Profile)[]
        {
            ("主菜单", scenes.MainMenu), ("选歌", scenes.SongSelect),
            ("打歌", scenes.Playing), ("结算", scenes.Result)
        };

        if (profiles.All(p => !p.Profile.IsUsable))
            issues.Add(new(HealthLevel.Warning,
                           "四个界面都没启用，角色永远不会显示",
                           "界面感知"));


        // 打歌时必须穿透：不然悬浮窗会把鼠标从 osu 手里抢走，直接打不了
        if (scenes.Playing.Enabled && !scenes.Playing.Window.ClickThrough)
            issues.Add(new(HealthLevel.Warning,
                           "打歌界面没有开启点击穿透，会抢走 osu 的鼠标、影响打歌",
                           "界面感知.打歌.悬浮窗.点击穿透"));
    }

    // ---------------- 数据源：自动启动的开关开着、但目标不可用（第 21 条）----------------

    /// <summary>
    /// 自动启动那四个开关里的前两个。
    ///
    /// 【为什么只查"开着"的那两个】
    ///   开关关着时路径为空**是正常的** —— 用户没打算用这个功能，
    ///   报一句"没配路径"就是**假警报**（这个项目的体检最怕假警报：
    ///   整屏橙色会让人以为程序坏了，然后去改一个本来就对的东西）。
    ///
    /// 【为什么"空"和"文件不存在"要分开说】
    ///   它们是两种不同的修法：一个要去选文件，一个要去看文件是不是被挪走了。
    ///   合并成一句"路径不对"会让用户先去翻设置、再发现是文件没了。
    ///
    /// 【为什么"退出时关闭"不看】
    ///   那两个开关不依赖路径：它们关的是**我们自己启动起来的那个进程**，
    ///   而"启动过没有"是运行期的事，配置文件里没有任何东西可以据此判断。
    ///   对它报任何东西都只能是猜 
    /// </summary>
    private static void CheckDataSource(
        DataSourceConfig dataSource, HealthEnvironment env, List<HealthIssue> issues)
    {
        if (dataSource.AutoStartTosu)
            CheckAutoStartTarget("tosu", "数据源.tosu路径",
                                 dataSource.TosuPath, env.TosuExeExists, issues);

        if (dataSource.AutoStartOsu)
            CheckAutoStartTarget("osu", "数据源.osu路径",
                                 dataSource.OsuPath, env.OsuExeExists, issues);
    }

    /// <param name="fieldPath">指**路径**那一项，不是指开关 —— 用户接下来要去改的是那个框</param>
    private static void CheckAutoStartTarget(
        string what, string fieldPath, string? path, bool exists, List<HealthIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            issues.Add(new(HealthLevel.Warning,
                           $"开了「启动悬浮窗时自动启动 {what}」，但 {what} 程序路径还空着 —— 这个开关不会生效",
                           fieldPath));
            return;
        }

        if (!exists)
        {
            issues.Add(new(HealthLevel.Warning,
                           $"开了「启动悬浮窗时自动启动 {what}」，但文件不存在：{path}",
                           fieldPath));
        }
    }

    // ---------------- 表现：策略和数值对不上 ----------------

    private static void CheckPerformance(PluginConfig config, List<HealthIssue> issues)
    {
        var performance = config.Performance;

        // 「取最长」的时长是表情音效说了算的，这个数字根本用不到 → 只对「跟随表情」提这条
        if (performance.LengthStrategy == LengthStrategyKind.ExpressionFirst
            && performance.ExpressionDurationMs < TooShortExpressionMs)
            issues.Add(new(HealthLevel.Info,
                           $"时长策略是「跟随表情」，但表情持续时间只有 {performance.ExpressionDurationMs} 毫秒" +
                           $"（短于 {TooShortExpressionMs} 毫秒），表情会一闪而过",
                           "表现.表情持续时间毫秒"));
    }
}
