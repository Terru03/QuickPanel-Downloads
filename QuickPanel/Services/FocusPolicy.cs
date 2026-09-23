namespace QuickPanel.Services;

public static class FocusPolicy
{
	private const int VirtualKeySpace = 0x20;
	private const int VirtualKeyD0 = 0x30;
	private const int VirtualKeyD9 = 0x39;
	private const int VirtualKeyA = 0x41;
	private const int VirtualKeyZ = 0x5A;
	private const int VirtualKeyNumpad0 = 0x60;
	private const int VirtualKeyNumpad9 = 0x69;
	private const int VirtualKeyOem1 = 0xBA;
	private const int VirtualKeyOem8 = 0xDF;

	public static bool ShouldFocusChromeAfterTabSwitch(bool isNativeTab)
	{
		return !isNativeTab;
	}

	public static bool ShouldFocusPageForKey(int virtualKey, PanelShortcutModifiers modifiers)
	{
		return ShouldFocusPageForTyping(
			isAppEditingControlFocused: false,
			isSelectedBrowserTab: true,
			isBrowserAlreadyFocused: false,
			virtualKey,
			modifiers);
	}

	public static bool ShouldFocusPageForTyping(
		bool isAppEditingControlFocused,
		bool isSelectedBrowserTab,
		bool isBrowserAlreadyFocused,
		int virtualKey,
		PanelShortcutModifiers modifiers)
	{
		if (isAppEditingControlFocused || !isSelectedBrowserTab || isBrowserAlreadyFocused)
		{
			return false;
		}
		if ((modifiers & (PanelShortcutModifiers.Control | PanelShortcutModifiers.Alt)) != 0)
		{
			return false;
		}
		return virtualKey == VirtualKeySpace ||
			(virtualKey >= VirtualKeyD0 && virtualKey <= VirtualKeyD9) ||
			(virtualKey >= VirtualKeyA && virtualKey <= VirtualKeyZ) ||
			(virtualKey >= VirtualKeyNumpad0 && virtualKey <= VirtualKeyNumpad9) ||
			(virtualKey >= VirtualKeyOem1 && virtualKey <= VirtualKeyOem8);
	}
}
