using System.Windows;
using System.Windows.Controls;

namespace QuickPanel;

public partial class MainWindow
{
    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(ContextMenu),
            ContextMenu.OpenedEvent,
            new RoutedEventHandler(ForceQuickPanelContextMenuStyle));
    }

    private static void ForceQuickPanelContextMenuStyle(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu || Application.Current == null)
        {
            return;
        }

        if (Application.Current.TryFindResource(typeof(ContextMenu)) is Style contextMenuStyle)
        {
            contextMenu.Style = contextMenuStyle;
        }

        if (Application.Current.TryFindResource(typeof(MenuItem)) is not Style menuItemStyle)
        {
            return;
        }

        ApplyMenuItemStyle(contextMenu.Items, menuItemStyle);
    }

    private static void ApplyMenuItemStyle(ItemCollection items, Style menuItemStyle)
    {
        foreach (object item in items)
        {
            if (item is not MenuItem menuItem)
            {
                continue;
            }

            menuItem.Style = menuItemStyle;

            if (menuItem.HasItems)
            {
                ApplyMenuItemStyle(menuItem.Items, menuItemStyle);
            }
        }
    }
}
