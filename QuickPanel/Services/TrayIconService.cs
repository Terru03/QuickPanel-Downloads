using System;
using System.Drawing;
using System.Windows.Forms;

namespace QuickPanel.Services;

public sealed class TrayIconService : IDisposable
{
	private readonly Icon _applicationIcon;

	private readonly ToolStripMenuItem _startupItem;

	private readonly ToolStripMenuItem _updateItem;

	private readonly NotifyIcon _notifyIcon;

	public event Action? OpenRequested;

	public event Action? SettingsRequested;

	public event Action<bool>? StartupChanged;

	public event Action? ExitRequested;

	public TrayIconService(bool startWithWindows)
	{
		_applicationIcon = LoadApplicationIcon();
		ToolStripMenuItem toolStripMenuItem = new ToolStripMenuItem("Open");
		toolStripMenuItem.Click += delegate
		{
			this.OpenRequested?.Invoke();
		};
		ToolStripMenuItem settingsItem = new ToolStripMenuItem("Settings…");
		settingsItem.Click += delegate
		{
			this.SettingsRequested?.Invoke();
		};
		_updateItem = new ToolStripMenuItem("Update available")
		{
			Enabled = false,
			Visible = false
		};
		_startupItem = new ToolStripMenuItem("Start with Windows")
		{
			Checked = startWithWindows,
			CheckOnClick = true
		};
		_startupItem.Click += delegate
		{
			this.StartupChanged?.Invoke(_startupItem.Checked);
		};
		ToolStripMenuItem toolStripMenuItem2 = new ToolStripMenuItem("Exit");
		toolStripMenuItem2.Click += delegate
		{
			this.ExitRequested?.Invoke();
		};
		ContextMenuStrip contextMenuStrip = new ContextMenuStrip
		{
			Items =
			{
				(ToolStripItem)toolStripMenuItem,
				(ToolStripItem)settingsItem,
				(ToolStripItem)_updateItem,
				(ToolStripItem)new ToolStripSeparator(),
				(ToolStripItem)_startupItem,
				(ToolStripItem)new ToolStripSeparator(),
				(ToolStripItem)toolStripMenuItem2
			}
		};
		_notifyIcon = new NotifyIcon
		{
			ContextMenuStrip = contextMenuStrip,
			Icon = _applicationIcon,
			Text = "Quick Panel",
			Visible = true
		};
		_notifyIcon.DoubleClick += delegate
		{
			this.OpenRequested?.Invoke();
		};
	}

	public void UpdateStartupState(bool enabled)
	{
		_startupItem.Checked = enabled;
	}

	public void SetUpdateAvailable(bool available, string? label = null)
	{
		_updateItem.Text = string.IsNullOrWhiteSpace(label) ? "Update available" : label;
		_updateItem.Visible = available;
	}

	public void Dispose()
	{
		_notifyIcon.Visible = false;
		_notifyIcon.ContextMenuStrip?.Dispose();
		_notifyIcon.Dispose();
		_applicationIcon.Dispose();
	}

	private static Icon LoadApplicationIcon()
	{
		string processPath = Environment.ProcessPath;
		return (Icon)((string.IsNullOrWhiteSpace(processPath) ? null : Icon.ExtractAssociatedIcon(processPath)) ?? SystemIcons.Application).Clone();
	}
}
