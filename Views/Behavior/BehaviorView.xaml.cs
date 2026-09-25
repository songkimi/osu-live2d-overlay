// ============================================================
// BehaviorView.xaml.cs —— 「行为」页
//
// 这一页的 View 只做两件"只有 View 能做"的事，其余全是绑定：
//   ① 把清单的选中接给预览（点一行 → 播一次）
//   ② 预览的生命周期：**露出来才起、切走就挂起**（定稿 §1.2 的懒加载约定）
//
// 【为什么预览要在这里推，而不是让 PreviewPane 自己监听清单】
//   预览要显示的是"**刚才点的那一行**"—— 那是**页面的状态**，不是预览的状态。
//   让预览反过来去猜页面选了什么，就又多了一份要同步的真相
//   （这条和「联动与窗口」页里的说明是同一条理由）。
//
// 【为什么两个事件各管各的】
//   TabControl 里两个 ListBox 的 SelectedItem 绑的是**两个属性**（表情/动作），
//   所以"点表情"和"点动作"是两条独立的路 —— 换 Tab 本身不会触发任何播放。
// ============================================================
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace OsuLive2dOverlay;

public partial class BehaviorView : UserControl
{
    private readonly BehaviorViewModel _viewModel;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// 鼠标左键**按下那一刻**，清单里选中的是哪一行。
    /// 松手时用它判断"这一下是不是点在已经选中的行上" —— 是的话要**补播一次**：
    /// 选中项没变就不会有 `PropertyChanged`，光靠属性通知是实现不了"再看一次"的。
    /// （"再播一次"是"播一次"语义下必然会有的需求；老的"保持"语义里点同一行没有意义。）
    /// </summary>
    private object? _selectionAtMouseDown;

    public BehaviorView(BehaviorViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.PreviewRequested += OnPreviewRequested;
        viewModel.PersistentChanged += OnPersistentChanged;
        viewModel.Notify += ShowToast;

        _toastTimer.Tick += (_, _) => HideToast();

        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SettingIndex.Rebuild(this);

    /// <summary>
    /// 页面被换掉：把挂在 `ConfigStore` 上的订阅摘掉。
    ///
    /// ★ 2026-09-23：页面是**每次点导航都新建**的，而 `ConfigStore` 活到程序退出 ——
    ///   订阅了不摘就是"每切一次页留一份"（体检页那份最实在：它每次都会重扫模型）。
    ///   这一页先做对，其余几页的同类订阅还没补（定稿决策 56 附近记着）。
    /// </summary>
    private void OnPageUnloaded(object sender, RoutedEventArgs e) => _viewModel.Detach();

    // ------------------------------------------------------------
    // 预览的生命周期
    // ------------------------------------------------------------

    private async void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            // **懒加载**：第一次真被看见时才起那只 WebView2（启一次约一秒、还有一份内存）。
            // 配置从 `_viewModel` 那边拿 —— 预览区自己不认识 ConfigStore，
            // "换了配置要不要重建"由它做一次引用比较（见 PreviewPane 的说明）。
            await Preview.EnsureStartedAsync(_viewModel.Config, _viewModel.DebugMode);
            PushToPreview();
        }

        // 切走那边**不用做事**：页面被换掉时预览区自己会在 `Unloaded` 里释放 WebView2
        //（★ 2026-09-23：反复切页掉帧就是因为原来只"挂起"、实例一直在攒）。
    }

    /// <summary>
    /// 把"角色该站在哪、框子多大"发给预览。
    ///
    /// 站位取「打歌」那一档（理由见 `BehaviorViewModel.PreviewPlacement`），
    /// 而且**和悬浮窗走同一个取值方法** —— 预览另算一套的话，
    /// "看到的"和"跑到"的迟早对不上。
    /// </summary>
    private void PushToPreview()
    {
        // 尺寸留空 = "用程序默认尺寸"。这里必须按**默认尺寸**画框，
        // 而不是显示一个用户没设过的数字（默认尺寸的唯一出处见 OverlayWindow.DefaultWidth）。
        var width = _viewModel.PreviewWindowWidth > 0 ? _viewModel.PreviewWindowWidth : OverlayWindow.DefaultWidth;
        var height = _viewModel.PreviewWindowHeight > 0 ? _viewModel.PreviewWindowHeight : OverlayWindow.DefaultHeight;

        // 角色**一直显示**：这一页要看的是"标识在角色身上是什么样"，
        // 不是"这个界面该不该显示角色"（那是「联动与窗口」页的事）。
        Preview.ShowPlacement(GameScene.Playing, _viewModel.PreviewPlacement, width, height);
    }

    // ------------------------------------------------------------
    // 清单 → 预览
    // ------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            // 键盘上下键换行也会走到这里（鼠标换行同样是先改选中项）
            case nameof(BehaviorViewModel.SelectedExpression):
                Play(_viewModel.SelectedExpression);
                break;

            case nameof(BehaviorViewModel.SelectedMotion):
                Play(_viewModel.SelectedMotion);
                break;
        }
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
        => _selectionAtMouseDown = (sender as ListBox)?.SelectedItem;

    private void OnRowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;

        // 选中项变了 → 那一次已经由 `PropertyChanged` 播过了，这里别播第二遍
        if (!ReferenceEquals(list.SelectedItem, _selectionAtMouseDown)) return;

        Play(list.SelectedItem as IdentifierRow);
    }

    /// <summary>
    /// 播清单里的一行：表情发 `expression`（带参数）、动作发 `motion`。
    /// 按"这一行属于哪个清单"来分 —— 两个清单装的是同一个 record 类型，
    /// 所以要靠集合本身来认亲，不能靠类型。
    /// </summary>
    private void Play(IdentifierRow? row)
    {
        if (row is null) return;

        if (_viewModel.Expressions.Contains(row))
            // 参数一起发过去：页面拿到就能直接往模型上写，不用自己去读 exp3 文件
            Preview.PreviewExpression(row.Id, row.Parameters);
        else
            Preview.PreviewMotion(row.Id);
    }

    /// <summary>
    /// 点了一张规则小卡片 → 试播。
    ///
    /// 表情和动作是**两条独立的消息**（§3.1 的解耦：表情没播出来，动作照样做），
    /// 所以这里分开发；语音不发 —— 预览区按设计不播声音。
    /// </summary>
    private void OnPreviewRequested(PreviewRequest request)
    {
        if (request.Expression.Length > 0)
            Preview.PreviewExpression(request.Expression, request.Parameters);

        if (request.Action.Length > 0)
            Preview.PreviewMotion(request.Action);
    }

    /// <summary>
    /// 选语音目录（只读框 + 浏览）。
    /// 用 `OpenFolderDialog`（.NET 8+ 自带）而不是自己 P/Invoke Shell ——
    /// 手打路径太容易错（少个斜杠、全角字符），而这类错**不报错、只是没声音**。
    /// </summary>
    private void OnBrowseVoiceFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选语音目录" };

        if (dialog.ShowDialog() == true)
            _viewModel.VoiceDirectory = dialog.FolderName;
    }

    /// <summary>常驻组件勾选 → 让预览**立刻**重发一次常驻层（不等保存、不等重开页面）</summary>
    private void OnPersistentChanged() => Preview.UpdatePersistentLayers();

    // ------------------------------------------------------------

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastPanel.Visibility = Visibility.Visible;

        _toastTimer.Stop();

        // 错误类不自动消失（定稿 §4）—— 要用户真的看见
        if (message.Contains("失败", StringComparison.Ordinal)) return;

        _toastTimer.Start();
    }

    private void OnToastClick(object sender, MouseButtonEventArgs e) => HideToast();

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastPanel.Visibility = Visibility.Collapsed;
    }
}
