// ============================================================
// DataAndFilesView.xaml.cs —— 「数据与档案」的 Tab 容器
//
// 它只做一件事：**把二级页造出来塞进 Tab**。
//
// 没有单独的 ViewModel —— 它自己不含任何状态（页面的状态各在自己的 VM 里）。
// 硬给它造一个空 VM 只是为了让"每页一个 VM"这条规则看起来很整齐，
// 那是形式主义，不是设计。
// ============================================================
using System.Windows.Controls;

namespace OsuLive2dOverlay;

public partial class DataAndFilesView : UserControl
{
    public DataAndFilesView(ConfigStore store)
    {
        InitializeComponent();

        // 各页的 View + 自己的 ViewModel（都从同一个配置门面读）
        HealthTab.Content = new HealthCheckView(new HealthCheckViewModel(store));
        LogTab.Content = new LogView(new LogViewModel(store));

        // "现在有没有悬浮窗在跑"也是**注入**的（同下面 RunTimeTab 的理由）：
        // 「程序与数据源」页要用它把"已保存"那句提示说得准（地址与端口是启动时读的，
        // 悬浮窗正跑着的时候改动还没生效）。
        LocationTab.Content = new FileLocationView(
            new FileLocationViewModel(store, () => App.Overlay is not null));

        ProfileTab.Content = new ProfileView(new ProfileViewModel(store));

        // 待保存的窗口调整是**全程序共用一份**（App.WindowAdjustments）——
        // 注入进去而不是让 VM 自己去摸静态成员：这样它能在测试里被喂一份假的
        RunTimeTab.Content = new RunTimeView(new RunTimeViewModel(store, App.WindowAdjustments));
    }
}
