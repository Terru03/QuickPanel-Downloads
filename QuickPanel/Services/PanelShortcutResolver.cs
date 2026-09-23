using System;

namespace QuickPanel.Services;

[Flags]
public enum PanelShortcutModifiers
{
	None = 0,
	Control = 1,
	Shift = 2,
	Alt = 4
}

public enum PanelShortcutCommand
{
	None,
	AddTab,
	RestoreClosedTab,
	CloseCurrentCustomTab,
	SelectNextTab,
	SelectPreviousTab,
	JumpToTab,
	ZoomIn,
	ZoomOut,
	ResetZoom,
	ReloadCurrentTab
}

public readonly record struct PanelShortcut(PanelShortcutCommand Command, int Argument = 0);

public static class PanelShortcutResolver
{
	private const int VirtualKeyTab = 0x09;
	private const int VirtualKeyF5 = 0x74;
	private const int VirtualKeyD0 = 0x30;
	private const int VirtualKeyD1 = 0x31;
	private const int VirtualKeyD9 = 0x39;
	private const int VirtualKeyT = 0x54;
	private const int VirtualKeyR = 0x52;
	private const int VirtualKeyW = 0x57;
	private const int VirtualKeyOemPlus = 0xBB;
	private const int VirtualKeyOemMinus = 0xBD;
	private const int VirtualKeyNumpad0 = 0x60;
	private const int VirtualKeyNumpad1 = 0x61;
	private const int VirtualKeyNumpad9 = 0x69;
	private const int VirtualKeyNumpadAdd = 0x6B;
	private const int VirtualKeyNumpadSubtract = 0x6D;

	public static bool TryResolve(int virtualKey, PanelShortcutModifiers modifiers, out PanelShortcut shortcut)
	{
		shortcut = default;
		if (virtualKey == VirtualKeyF5 && modifiers == PanelShortcutModifiers.None)
		{
			shortcut = new PanelShortcut(PanelShortcutCommand.ReloadCurrentTab);
			return true;
		}
		if ((modifiers & PanelShortcutModifiers.Control) == 0 ||
			(modifiers & PanelShortcutModifiers.Alt) != 0)
		{
			return false;
		}

		bool hasShift = (modifiers & PanelShortcutModifiers.Shift) != 0;
		switch (virtualKey)
		{
			case VirtualKeyTab:
				shortcut = new PanelShortcut(hasShift ? PanelShortcutCommand.SelectPreviousTab : PanelShortcutCommand.SelectNextTab);
				return true;
			case VirtualKeyT:
				shortcut = new PanelShortcut(hasShift ? PanelShortcutCommand.RestoreClosedTab : PanelShortcutCommand.AddTab);
				return true;
			case VirtualKeyR when !hasShift:
				shortcut = new PanelShortcut(PanelShortcutCommand.ReloadCurrentTab);
				return true;
			case VirtualKeyW when !hasShift:
				shortcut = new PanelShortcut(PanelShortcutCommand.CloseCurrentCustomTab);
				return true;
			case VirtualKeyD0:
			case VirtualKeyNumpad0:
				shortcut = new PanelShortcut(PanelShortcutCommand.ResetZoom);
				return true;
			case VirtualKeyOemPlus:
			case VirtualKeyNumpadAdd:
				shortcut = new PanelShortcut(PanelShortcutCommand.ZoomIn);
				return true;
			case VirtualKeyOemMinus:
			case VirtualKeyNumpadSubtract:
				shortcut = new PanelShortcut(PanelShortcutCommand.ZoomOut);
				return true;
		}

		if (virtualKey >= VirtualKeyD1 && virtualKey <= VirtualKeyD9)
		{
			shortcut = new PanelShortcut(PanelShortcutCommand.JumpToTab, virtualKey - VirtualKeyD1);
			return true;
		}
		if (virtualKey >= VirtualKeyNumpad1 && virtualKey <= VirtualKeyNumpad9)
		{
			shortcut = new PanelShortcut(PanelShortcutCommand.JumpToTab, virtualKey - VirtualKeyNumpad1);
			return true;
		}
		return false;
	}
}
