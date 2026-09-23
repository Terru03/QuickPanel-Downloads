using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace QuickPanel;

internal static class SettingsPanelSeparation
{
	[ModuleInitializer]
	internal static void RegisterSettingsPanelSeparation()
	{
		EventManager.RegisterClassHandler(
			typeof(ContextMenu),
			ContextMenu.OpenedEvent,
			new RoutedEventHandler(ContextMenu_Opened));
	}

	private static void ContextMenu_Opened(object sender, RoutedEventArgs e)
	{
		if (sender is not ContextMenu contextMenu)
		{
			return;
		}

		int defaultPanelsHeaderIndex = -1;
		for (int index = 0; index < contextMenu.Items.Count; index++)
		{
			if (contextMenu.Items[index] is MenuItem header &&
				!header.IsEnabled &&
				string.Equals(header.Header as string, "Default panels", StringComparison.Ordinal))
			{
				defaultPanelsHeaderIndex = index;
				break;
			}
		}
		if (defaultPanelsHeaderIndex < 0)
		{
			return;
		}

		for (int index = defaultPanelsHeaderIndex + 1; index < contextMenu.Items.Count; index++)
		{
			if (contextMenu.Items[index] is Separator)
			{
				break;
			}
			if (contextMenu.Items[index] is MenuItem item &&
				string.Equals(item.Header as string, "Settings", StringComparison.Ordinal))
			{
				contextMenu.Items.RemoveAt(index);
				break;
			}
		}
	}
}
