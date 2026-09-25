// ============================================================
// AppView.xaml.cs —— 「应用」板块的容器
//
// 自己不含状态：只把各二级页造出来塞进 Tab（和「数据与档案」那个容器同一套做法）。
// 每页拿自己的 ViewModel，都用同一个配置门面。
// ============================================================
using System.Windows.Controls;

namespace OsuLive2dOverlay;

public partial class AppView : UserControl
{
    public AppView(ConfigStore store)
    {
        InitializeComponent();

        GeneralTab.Content = new GeneralView(new GeneralViewModel(store));
        UpdateTab.Content = new UpdateView();          // 占位：只有一个置灰按钮（见那一页的说明）
        AboutTab.Content = new AboutView(new AboutViewModel(store));
    }
}
