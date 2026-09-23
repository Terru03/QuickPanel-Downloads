using System;

namespace QuickPanel.Services;

public readonly record struct TabContextMenuState(
	bool CanDuplicateTab,
	bool CanEditCustomTab,
	bool CanTogglePin,
	bool CanCloseTab,
	bool CanReopenClosedTab,
	bool CanOpenExternally,
	bool CanCopyUrl,
	bool CanResetZoom,
	bool CanClearThisTabData,
	bool IsPinned);

public static class TabContextMenuStateFactory
{
	public static TabContextMenuState Create(bool isCustomWebTab, bool hasClosedTabs, bool hasUrl, double zoomFactor, bool isPinned)
	{
		return new TabContextMenuState(
			CanDuplicateTab: hasUrl,
			CanEditCustomTab: isCustomWebTab,
			CanTogglePin: hasUrl,
			CanCloseTab: isCustomWebTab,
			CanReopenClosedTab: hasClosedTabs,
			CanOpenExternally: hasUrl,
			CanCopyUrl: hasUrl,
			CanResetZoom: Math.Abs(zoomFactor - 1.0) >= 0.001,
			CanClearThisTabData: false,
			IsPinned: isPinned);
	}
}
