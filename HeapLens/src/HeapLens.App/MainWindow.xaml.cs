using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HeapLens.Core;

namespace HeapLens.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        Closed += (_, _) => _vm.Close();
    }

    /// <summary>双击类型行 → 切到"对象与保留路径"页并加载实例。</summary>
    private void TypesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TypesGrid.SelectedItem is TypeStat type && _vm.ShowInstancesCommand.CanExecute(type))
        {
            _vm.ShowInstancesCommand.Execute(type);
            if (TabControlInstance() is { } tabs) tabs.SelectedIndex = 2;
        }
    }

    // ---------- 调用链页 ----------

    /// <summary>双击类型 → 在下方列出该类型的实例，并在右侧生成持有链合并树。</summary>
    private void ChainTypesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ChainTypesGrid.SelectedItem is TypeStat type) _ = _vm.LoadChainInstancesAsync(type);
    }

    /// <summary>双击实例 → 立即显示调用链（选中即显示，双击仅聚焦）。</summary>
    private void ChainInstancesGrid_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: InstanceInfo inst }) _vm.ShowChainForAddress(inst.Address);
    }

    /// <summary>树节点首次展开时懒加载下级引用。</summary>
    private void ChainTreeItem_OnExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { DataContext: ChainTreeNode node }) _vm.LoadChildren(node);
    }

    /// <summary>树节点选中 → 右侧显示其调用链。</summary>
    private void ChainTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ChainTreeNode node) _vm.OnChainNodeSelected(node);
    }

    private TabControl? TabControlInstance()
    {
        DependencyObject? node = TypesGrid;
        while (node != null)
        {
            if (node is TabControl tc) return tc;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
