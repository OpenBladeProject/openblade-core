using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ListViewItem = Microsoft.UI.Xaml.Controls.ListViewItem;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private void OnCoolingPadPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0
            || args.PropertyName != nameof(CoolingPadViewModel.IsVisible))
        {
            return;
        }

        foreach (PrimaryNavigationItemOption item in PrimaryNavigationItems)
        {
            if (NavigationOrderList.ContainerFromItem(item) is ListViewItem container)
            {
                container.Visibility = NavigationOrderVisibility(item);
            }
        }
        if (ViewModel.CoolingPad.IsVisible)
        {
            return;
        }

        // Only a confirmed absence hides the accessory. Pipe failures retain the
        // confirmed presentation in CoolingPadViewModel and never reach this path.
        if (ReferenceEquals(MainNavigation.SelectedItem, CoolingPadNavigationItem)
            || CoolingPadPage.Visibility == Visibility.Visible)
        {
            MainNavigation.SelectedItem = HomeNavigationItem;
            ApplyNavigationDestination("home");
        }
    }

    private void OnNavigationOrderContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        args.ItemContainer.Visibility = NavigationOrderVisibility(args.Item as PrimaryNavigationItemOption);
    }

    private Visibility NavigationOrderVisibility(PrimaryNavigationItemOption? item) =>
        item?.Kind == PrimaryNavigationItemKind.CoolingPad && !ViewModel.CoolingPad.IsVisible
            ? Visibility.Collapsed
            : Visibility.Visible;
}
