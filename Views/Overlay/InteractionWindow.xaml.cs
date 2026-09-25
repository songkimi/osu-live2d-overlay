// ============================================================
// InteractionWindow.xaml.cs —— 影子窗口的代码后置
//
// 它只做一件事：**接住鼠标，并把它变成"拖动窗口"的动作**。
// 拖动只会移动真正的悬浮窗（由外面订阅 Dragged 事件完成）。
//
// 两个踩过的坑，都已修在下面（都跟"这个窗口自己也会动"有关）：
//   ① 它必须**不抢焦点**（WS_EX_NOACTIVATE）。否则一点它就激活，
//      全屏的 osu 立刻失焦并自动最小化 —— 用户点一下游戏就缩下去了。
//   ② 拖动的增量必须用**屏幕坐标**算。用窗口内坐标的话，"窗口自己移动"
//      会被算进增量里，表现就是拖起来疯狂来回跳。
// ============================================================
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace OsuLive2dOverlay;

public partial class InteractionWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // 不出现在 Alt+Tab 切换列表里

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>鼠标拖了多远（dx, dy）—— 外面拿它去移动真正的悬浮窗</summary>
    public event Action<double, double>? Dragged;

    /// <summary>
    /// 拖动结束（鼠标松开）。
    /// 外面用它来判定"要不要吸到屏幕边上" —— **吸附必须放在松手这一刻**：
    /// 拖动过程中实时吸会让窗口"粘"在半路、和鼠标脱节，手感很怪。
    /// </summary>
    public event Action? DragFinished;

    private Point _lastScreen;      // 上一次的**屏幕**坐标
    private bool _dragging;

    public InteractionWindow()
    {
        InitializeComponent();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 不抢焦点 + 不进 Alt+Tab：它是个"影子"，用户不该感觉到它存在。
        // 收鼠标事件不需要焦点 —— 这两个样式正好把"能点"和"会抢焦点"分开。
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
    private bool _moved;
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _lastScreen = PointToScreen(e.GetPosition(this));
        _dragging = CaptureMouse();     // 捕获之后即使鼠标移出窗口也能继续拖
        DebugLog.Write($"影子窗口：鼠标按下（捕获={_dragging}）");
        _moved = false;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;

        // 关键：用屏幕坐标算增量。
        // 这个窗口自己也在跟着移动，用窗口内坐标会把"窗口移动"一起算进去 —— 来回跳就是这个原因。
        var now = PointToScreen(e.GetPosition(this));
        var dx = now.X - _lastScreen.X;
        var dy = now.Y - _lastScreen.Y;
        if (dx == 0 && dy == 0) return;

        _lastScreen = now;
        Dragged?.Invoke(dx, dy);
        _moved = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        var wasDragging = _dragging;
        _dragging = false;
        ReleaseMouseCapture();

        if (wasDragging && _moved) DragFinished?.Invoke();
    }
}
