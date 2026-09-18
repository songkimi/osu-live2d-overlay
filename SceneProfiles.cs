// ============================================================
// SceneProfiles.cs —— 界面档位：四个界面各一份"显示什么、显示在哪、能不能点"
//
// 【为什么会有这个文件】
//   原来角色只有一份全局配置（模型、缩放、上下位置），所以不管在选歌、打歌还是结算，
//   角色都长一样、都站在同一个地方。可现实里的诉求是相反的：
//     选歌：角色好好站着、还能拖动窗口、想给它配个斜角
//     打歌：必须点击穿透（不然鼠标点不到谱面），而且要挪到不挡谱面的角落
//     结算：位置可能跟打歌一样，但表现可以不一样
//   于是配置从"一份"变成"每个界面各一份"。
//
// 【谁在用它 —— 三个使用场景，决定了这里只有三个能力】
//   ① 服务运行时：拿当前场景那一份 → 看它能不能用
//        Get(场景) + IsUsable
//   ② 设置界面（配置/预览）：直接拿 SceneProfiles 里的某一份来绑定、来预览
//        不走 Get —— 用户没启用就整块置灰、模拟台上不显示角色
//   ③ 设置界面那个按钮：把刚配好的一份填给**还没配置**的界面
//        ApplyToUnconfigured(来源)
//
//   所以这里刻意没有"从 A 界面复制到 B 界面"这类方法 —— 没有使用者的 API 不写。
//
// 【每个字段的"生效方式"（定稿 §1.1 的三分类，落到这份档位上只有两类）】
//     · 窗口状态（Window）—— **服务运行中可改**
//         按 [临时允许交互] 热键解除穿透 → 拖动窗口 → 停服务时问"是否保存"（§3.6.2）
//     · 其余字段 —— **需重载**：服务运行时置灰 + 锁图标，必须先停服务
//   这不是文档上的分类，是**结构本身**：除了 Window，其它字段运行期一律不准动。
//
// 【两个"位置"千万别混】（定稿 §3.6.0：同一界面出现两个"位置"，用户 100% 会拖错）
//   悬浮窗位置 = 在**屏幕上**（窗口挪到哪）→ 运行期拖动改
//   角色站位   = 在**悬浮窗里**（角色在画布内的位置/大小/角度）→ 配置界面改
// ============================================================
using System.Text.Json.Serialization;

namespace OsuLive2dOverlay;

/// <summary>
/// 窗口状态（定稿里的叫法）：**服务运行中唯一允许改动的部分**。
/// 类名用 Placement 而不是 State，是为了避开 WPF 自带的 System.Windows.WindowState ——
/// 这个类要在 OverlayWindow（它本身就是个 Window）里用，同名会读得人发懵。
///
/// 为什么聚成一个类而不是把字段平铺在档位上：
///   定稿 §3.6.2 把"运行期调整"定义成"当前界面的窗口状态 = 位置 + 尺寸 + 交互性"，
///   聚起来之后"哪部分能热改"在类型上就看得见 —— 运行时能碰的只有这个对象。
/// </summary>
public sealed class WindowPlacement
{
    /// <summary>
    /// 所在显示器（空 = 主显示器）。
    /// 为什么现在就要有这个字段（定稿 §3.6.4）：多显示器 + 不同缩放（100% / 150%）时，
    /// 只存坐标会跑到别的屏幕上去 —— "我明明保存了位置，第二天打开跑到别的屏幕去了"。
    /// </summary>
    [JsonPropertyName("显示器")] public string Display { get; set; } = "";

    /// <summary>
    /// 逻辑坐标（WPF 的 DIP），不是物理像素 —— 换 DPI 才不会跑偏。
    ///
    /// **null = 用户没设过**：这时窗口保持当前位置不动（程序自己的默认摆放逻辑在
    /// OverlayWindow.OnSourceInitialized 里）。为什么用 null 而不是 0：
    /// 屏幕坐标 0 是合法位置（主显示器左上角），拿它当"没设过"的暗号会出错。
    /// </summary>
    [JsonPropertyName("X")] public double? X { get; set; }
    [JsonPropertyName("Y")] public double? Y { get; set; }

    /// <summary>
    /// 窗口尺寸。**null = 用户没设过** → 保持窗口现在的尺寸不动
    /// （初始尺寸由 OverlayWindow.xaml 里的 430×620 说了算）。
    /// 和 X / Y 一个道理：只有设过的值才覆盖 —— 否则用户在调整模式里缩放过的窗口，
    /// 每次切界面都会被改回来。
    /// </summary>
    [JsonPropertyName("宽")] public double? Width { get; set; }
    [JsonPropertyName("高")] public double? Height { get; set; }

    /// <summary>
    /// 点击穿透（是否让鼠标穿过去）。**按界面各一份**：
    ///   · 打歌 = true（不能交互）—— 否则窗口会抢走 osu! 的鼠标，谱面就点不了了
    ///   · 选歌 / 结算 = false（可交互）—— 空闲的时候用户是有需求的：拖窗口、点角色
    /// 属性默认给 false（可交互），**打歌那一份在 SceneProfiles 里单独设成 true**。
    /// 运行期可以按热键临时切；切过的状态在停服务时记下来，当这个界面的默认交互模式。
    /// </summary>
    [JsonPropertyName("点击穿透")] public bool ClickThrough { get; set; }

    /// <summary>
    /// 拖动窗口时是否自动吸附到屏幕边缘。
    /// 吸附的**距离**是全局设置（定稿「悬浮窗与显示」那一块），这里只管"这个界面吸不吸"。
    /// </summary>
    [JsonPropertyName("自动吸附")] public bool SnapToEdges { get; set; } = true;

    /// <summary>把别人那份窗口状态搬进来：自己这个对象不换，只换里面的值</summary>
    public void CopyFrom(WindowPlacement other)
    {
        Display = other.Display;
        X = other.X;
        Y = other.Y;
        Width = other.Width;
        Height = other.Height;
        ClickThrough = other.ClickThrough;
        SnapToEdges = other.SnapToEdges;
    }
}

/// <summary>
/// 角色**在悬浮窗里**的位置、大小、角度。**需重载**：运行期不允许拖人物。
///
/// 为什么运行期只能拖窗口、不能拖人物（定稿 §3.6.1）：
///   用户运行时的真实意图几乎总是"这窗口挡住我了，挪开" —— 不是"我要调整角色的站位"。
///   如果运行期拖动改的是人物，用户会想：我只是想把窗口挪开，人物怎么跑到角落去了。
///
/// **位置用"比例"而不是像素**（和原来的全局视图参数同一套语义）：
///   X = 相对画布宽度的偏移（0 = 水平居中），Y = 相对画布高度的偏移（0 = 底部贴齐）。
///   这样窗口尺寸一变，角色不会跑出画面 —— 换成像素就得在每次改窗口大小时重新算。
/// </summary>
public sealed class CharacterPlacement
{
    /// <summary>水平偏移，相对画布宽度（0 = 居中；正数右移）</summary>
    [JsonPropertyName("X")] public double X { get; set; }

    /// <summary>垂直偏移，相对画布高度（0 = 底部贴齐；正数下移）</summary>
    [JsonPropertyName("Y")] public double Y { get; set; }

    /// <summary>缩放倍数（1 = 模型刚好完整装进画布）</summary>
    [JsonPropertyName("缩放")] public double Scale { get; set; } = 1.0;

    /// <summary>角度（度）。选歌界面想斜着站就靠它（不必是 90°）</summary>
    [JsonPropertyName("角度")] public double AngleDegrees { get; set; }

    /// <summary>把别人那份站位搬进来：自己这个对象不换，只换里面的值</summary>
    public void CopyFrom(CharacterPlacement other)
    {
        X = other.X;
        Y = other.Y;
        Scale = other.Scale;
        AngleDegrees = other.AngleDegrees;
    }
}

/// <summary>一个界面的档位：这个界面显示什么、显示在哪、能不能点</summary>
public sealed class SceneProfile
{
    /// <summary>【需重载】这个界面要不要启用界面感知</summary>
    [JsonPropertyName("启用")] public bool Enabled { get; set; }

    /// <summary>【需重载】模型入口文件名。空 = 这一档没有模型（＝没配置）</summary>
    [JsonPropertyName("模型")] public string Model { get; set; } = "";

    /// <summary>【运行期可改】窗口状态：位置 / 尺寸 / 穿透 / 吸附</summary>
    [JsonPropertyName("悬浮窗")] public WindowPlacement Window { get; set; } = new();

    /// <summary>【需重载】角色在窗口里的站位</summary>
    [JsonPropertyName("站位")] public CharacterPlacement Character { get; set; } = new();

    /// <summary>
    /// 【需重载】这个界面允不允许点击角色（触摸反应）。
    /// 首版不做触摸，先留位并写明"后续版本" —— 留个位置才能记住这条链还没走完。
    /// </summary>
    [JsonPropertyName("允许触摸")] public bool TouchEnabled { get; set; }

    /// <summary>
    /// 这一档能不能显示 —— 服务运行时拿它做最后一道判断。
    /// 两个条件缺一不可：启用了，而且真的配了模型
    /// （空白字符不算配："看起来配了"不等于"配了"）。
    /// </summary>
    [JsonIgnore] public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// 把别人的内容搬进**自己**：自己这个对象不换，只换里面的值。
    ///
    /// · 嵌套对象逐字段复制，Window / Character 本身保持同一个对象
    ///   （设置界面可能已经绑定着它们，换掉引用等于把绑定弄丢）
    /// · 来源的嵌套对象是 null 时，自己那部分**保持原样** ——
    ///   配置是界面生成的，但手改 JSON 时什么都可能出现，坏数据不该冲掉好设置
    /// </summary>
    public void CopyFrom(SceneProfile other)
    {
        Enabled = other.Enabled;
        Model = other.Model;
        TouchEnabled = other.TouchEnabled;

        if (other.Window is not null)
        {
            Window ??= new WindowPlacement();
            Window.CopyFrom(other.Window);
        }

        if (other.Character is not null)
        {
            Character ??= new CharacterPlacement();
            Character.CopyFrom(other.Character);
        }
    }
}

/// <summary>四个界面各一份档位</summary>
public sealed class SceneProfiles
{
    [JsonPropertyName("主菜单")] public SceneProfile MainMenu { get; set; } = new();
    [JsonPropertyName("选歌")] public SceneProfile SongSelect { get; set; } = new();
    [JsonPropertyName("结算")] public SceneProfile Result { get; set; } = new();

    /// <summary>
    /// 打歌界面：默认**穿透**（定稿 §四：打歌＝穿透，选歌 / 结算＝可交互）。
    /// 这是唯一一个默认值和别人不一样的界面 —— 因为只有它是"必须不能交互"的。
    /// </summary>
    [JsonPropertyName("打歌")] public SceneProfile Playing { get; set; } =
        new SceneProfile { Window = new WindowPlacement { ClickThrough = true } };

    /// <summary>
    /// 服务运行时用：现在在哪个界面 → 用哪一份。
    ///
    /// Unknown（osu 没开 / 连不上）和认不出的枚举值都给 null —— 不知道在哪，就不显示角色。
    /// 这是白名单哲学：宁可少显示，也不乱显示。
    ///
    /// 注意它**不回答**"用户配了没有"：用户没配的那个界面，返回的是一份
    /// IsUsable == false 的档位，而不是 null。这两件事不能混 ——
    /// "这个场景没有档位"和"这一档还没配好"，在设置界面上是完全不同的两种表现。
    /// </summary>
    public SceneProfile? Get(GameScene scene) => scene switch
    {
        GameScene.MainMenu => MainMenu,
        GameScene.SongSelect => SongSelect,
        GameScene.Playing => Playing,
        GameScene.Result => Result,
        _ => null
    };

    /// <summary>
    /// 设置界面里那个按钮：把 source 这一份，填给**还没配置**的界面。
    ///
    /// · 已经配好的界面一律不碰 —— 那是用户自己调出来的，一键覆盖会让人白干
    /// · 来源自己就没配置（IsUsable == false）→ 什么都不做：
    ///   拿一份空配置去覆盖别人没有意义
    /// · 来源是 Unknown / 认不出的值 → 同样什么都不做
    ///
    /// 实现上用 Get 的返回值判空，而不是自己列一遍"哪些值不算数"：
    /// 同一份知识写在两处早晚会不一致，而 Get 是唯一知道"哪些场景有档位"的地方。
    /// </summary>
    public void ApplyToUnconfigured(GameScene source)
    {
        var src = Get(source);
        if (src is null || !src.IsUsable) return;

        foreach (var scene in Enum.GetValues<GameScene>())
        {
            if (scene == source) continue;

            var dst = Get(scene);
            if (dst is null || dst.IsUsable) continue;     // 没有档位、或用户已经配好了 → 不动

            dst.CopyFrom(src);
        }
    }
}
