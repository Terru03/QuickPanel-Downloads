using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickPanel;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class SettingsWindow
{
	private ScrollViewer? _profilesPage;
	private StackPanel? _profilesListPanel;
	private bool _profilesUiInitialized;

	private void InitializeProfilesPage()
	{
		if (_profilesUiInitialized || GeneralPage?.Parent is not Grid pageHost)
		{
			return;
		}
		_profilesUiInitialized = true;

		ListBoxItem navItem = new()
		{
			Content = "Profiles",
			Tag = "Profiles"
		};
		int insertIndex = SettingsCategoryList.Items.Count;
		for (int i = 0; i < SettingsCategoryList.Items.Count; i++)
		{
			if (SettingsCategoryList.Items[i] is ListBoxItem item && string.Equals(item.Tag?.ToString(), "Privacy", StringComparison.Ordinal))
			{
				insertIndex = i;
				break;
			}
		}
		SettingsCategoryList.Items.Insert(insertIndex, navItem);

		_profilesListPanel = new StackPanel();
		_profilesPage = new ScrollViewer
		{
			Visibility = Visibility.Collapsed,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			Focusable = false,
			Content = _profilesListPanel
		};
		pageHost.Children.Add(_profilesPage);

		SettingsCategoryList.SelectionChanged += ProfilesCategory_SelectionChanged;
		SettingsSearchTextBox.TextChanged += ProfilesSearchTextBox_TextChanged;
	}

	private void ProfilesCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_profilesPage == null || SettingsCategoryList.SelectedItem is not ListBoxItem item)
		{
			return;
		}
		bool selected = string.Equals(item.Tag?.ToString(), "Profiles", StringComparison.Ordinal);
		_profilesPage.Visibility = selected && string.IsNullOrWhiteSpace(SettingsSearchTextBox.Text)
			? Visibility.Visible
			: Visibility.Collapsed;
		if (selected)
		{
			RenderProfiles();
		}
	}

	private void ProfilesSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_profilesPage == null)
		{
			return;
		}
		bool profilesSelected = SettingsCategoryList.SelectedItem is ListBoxItem item &&
			string.Equals(item.Tag?.ToString(), "Profiles", StringComparison.Ordinal);
		_profilesPage.Visibility = profilesSelected && string.IsNullOrWhiteSpace(SettingsSearchTextBox.Text)
			? Visibility.Visible
			: Visibility.Collapsed;
	}

	private void RenderProfiles()
	{
		if (_profilesListPanel == null)
		{
			return;
		}
		_profilesListPanel.Children.Clear();

		Grid header = new();
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		StackPanel heading = new();
		heading.Children.Add(new TextBlock
		{
			Text = "Profiles",
			FontSize = 18,
			FontWeight = FontWeights.SemiBold
		});
		heading.Children.Add(new TextBlock
		{
			Text = "Reuse separate logins without creating throwaway WebView2 profiles.",
			Margin = new Thickness(0, 3, 12, 5),
			FontSize = 11,
			Foreground = GetBrush("PanelMutedTextBrush", Brushes.Gray),
			TextWrapping = TextWrapping.Wrap
		});
		header.Children.Add(heading);
		Button createButton = CreateActionButton("+ New profile");
		createButton.VerticalAlignment = VerticalAlignment.Top;
		createButton.Click += CreateProfileButton_Click;
		Grid.SetColumn(createButton, 1);
		header.Children.Add(createButton);
		_profilesListPanel.Children.Add(header);

		if (Owner is not MainWindow owner)
		{
			_profilesListPanel.Children.Add(new TextBlock
			{
				Text = "Profile management is unavailable because the main panel is not attached.",
				Margin = new Thickness(0, 12, 0, 0),
				Foreground = Brushes.IndianRed,
				TextWrapping = TextWrapping.Wrap
			});
			return;
		}

		foreach (BrowserProfileUsageSnapshot profile in owner.GetBrowserProfileUsageSnapshot())
		{
			_profilesListPanel.Children.Add(CreateProfileCard(owner, profile));
		}
	}

	private Border CreateProfileCard(MainWindow owner, BrowserProfileUsageSnapshot profile)
	{
		Border card = new()
		{
			Margin = new Thickness(0, 8, 0, 0),
			Padding = new Thickness(14, 12, 14, 12),
			Background = GetBrush("PanelSurfaceBrush", Brushes.Transparent),
			BorderBrush = GetBrush("PanelBorderSubtleBrush", Brushes.Gray),
			BorderThickness = new Thickness(1),
			CornerRadius = new CornerRadius(8)
		};
		StackPanel body = new();
		card.Child = body;

		StackPanel titleRow = new()
		{
			Orientation = Orientation.Horizontal
		};
		titleRow.Children.Add(new TextBlock
		{
			Text = profile.Name,
			FontSize = 13,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		});
		if (profile.IsDefault)
		{
			Border badge = new()
			{
				Margin = new Thickness(8, 0, 0, 0),
				Padding = new Thickness(7, 2, 7, 2),
				Background = GetBrush("PanelAccentTintBrush", Brushes.Transparent),
				CornerRadius = new CornerRadius(5),
				Child = new TextBlock
				{
					Text = "Default",
					FontSize = 10,
					Foreground = GetBrush("PanelAccentBrush", Brushes.DodgerBlue)
				}
			};
			titleRow.Children.Add(badge);
		}
		body.Children.Add(titleRow);
		body.Children.Add(new TextBlock
		{
			Text = profile.TabCount + (profile.TabCount == 1 ? " tab" : " tabs") + "  ·  " + profile.Id,
			Margin = new Thickness(0, 4, 0, 0),
			FontSize = 10.5,
			Foreground = GetBrush("PanelMutedTextBrush", Brushes.Gray),
			TextTrimming = TextTrimming.CharacterEllipsis
		});

		WrapPanel actions = new()
		{
			Margin = new Thickness(0, 10, 0, 0)
		};
		Button rename = CreateActionButton("Rename");
		rename.IsEnabled = !profile.IsLegacySharedProfile;
		rename.ToolTip = profile.IsLegacySharedProfile ? "The original shared profile keeps its compatibility name." : "Rename this profile without changing its ID or login state.";
		rename.Click += (_, _) => RenameProfile(owner, profile);
		actions.Children.Add(rename);

		if (!profile.IsDefault)
		{
			Button makeDefault = CreateActionButton("Make default");
			makeDefault.Margin = new Thickness(6, 0, 0, 0);
			makeDefault.Click += (_, _) => SetDefaultProfile(owner, profile);
			actions.Children.Add(makeDefault);
		}

		Button openFolder = CreateActionButton("Open folder");
		openFolder.Margin = new Thickness(6, 0, 0, 0);
		openFolder.Click += (_, _) => OpenProfileFolder(profile);
		actions.Children.Add(openFolder);

		Button delete = CreateActionButton("Delete");
		delete.Margin = new Thickness(6, 0, 0, 0);
		delete.IsEnabled = !profile.IsDefault && !profile.IsLegacySharedProfile;
		delete.ToolTip = profile.IsDefault
			? "Make another profile the default first."
			: profile.IsLegacySharedProfile
				? "The original shared WebView2 profile is retained for compatibility."
				: "Delete this profile and its browser data.";
		delete.Click += (_, _) => DeleteProfile(owner, profile);
		actions.Children.Add(delete);
		body.Children.Add(actions);
		return card;
	}

	private void CreateProfileButton_Click(object sender, RoutedEventArgs e)
	{
		if (Owner is not MainWindow owner)
		{
			return;
		}
		ProfileNameWindow dialog = new("Create browsing profile")
		{
			Owner = this
		};
		if (dialog.ShowDialog() == true)
		{
			owner.CreateManagedBrowserProfile(dialog.ProfileName);
			RenderProfiles();
		}
	}

	private void RenameProfile(MainWindow owner, BrowserProfileUsageSnapshot profile)
	{
		ProfileNameWindow dialog = new("Rename browsing profile", profile.Name)
		{
			Owner = this
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		if (!owner.RenameManagedBrowserProfile(profile.Id, dialog.ProfileName, out string? error))
		{
			MessageBox.Show(this, error ?? "Could not rename profile.", "Profile rename failed", MessageBoxButton.OK, MessageBoxImage.Error);
		}
		RenderProfiles();
	}

	private void SetDefaultProfile(MainWindow owner, BrowserProfileUsageSnapshot profile)
	{
		if (!owner.SetManagedDefaultBrowserProfile(profile.Id, out string? error))
		{
			MessageBox.Show(this, error ?? "Could not set default profile.", "Profile update failed", MessageBoxButton.OK, MessageBoxImage.Error);
		}
		RenderProfiles();
	}

	private void DeleteProfile(MainWindow owner, BrowserProfileUsageSnapshot profile)
	{
		BrowserProfileDeleteMode mode = BrowserProfileDeleteMode.CloseTabs;
		if (profile.TabCount > 0)
		{
			MessageBoxResult choice = MessageBox.Show(
				this,
				"This profile is used by " + profile.TabCount + (profile.TabCount == 1 ? " tab." : " tabs.") + Environment.NewLine + Environment.NewLine +
				"Yes  — move those tabs to the current default profile and delete" + Environment.NewLine +
				"No   — close those tabs and delete" + Environment.NewLine +
				"Cancel — keep the profile",
				"Delete " + profile.Name + "?",
				MessageBoxButton.YesNoCancel,
				MessageBoxImage.Warning,
				MessageBoxResult.Cancel);
			if (choice == MessageBoxResult.Cancel)
			{
				return;
			}
			mode = choice == MessageBoxResult.Yes
				? BrowserProfileDeleteMode.MoveTabsToDefault
				: BrowserProfileDeleteMode.CloseTabs;
		}
		else if (MessageBox.Show(
			this,
			"Delete " + profile.Name + " and its local WebView2 browser data?",
			"Delete profile?",
			MessageBoxButton.YesNo,
			MessageBoxImage.Warning,
			MessageBoxResult.No) != MessageBoxResult.Yes)
		{
			return;
		}

		if (!owner.DeleteManagedBrowserProfile(profile.Id, mode, out string? error))
		{
			MessageBox.Show(this, error ?? "Could not delete profile.", "Profile deletion failed", MessageBoxButton.OK, MessageBoxImage.Error);
		}
		RenderProfiles();
	}

	private void OpenProfileFolder(BrowserProfileUsageSnapshot profile)
	{
		try
		{
			Directory.CreateDirectory(profile.DataPath);
			Process.Start(new ProcessStartInfo
			{
				FileName = profile.DataPath,
				UseShellExecute = true
			});
		}
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
		{
			MessageBox.Show(this, ex.Message, "Could not open profile folder", MessageBoxButton.OK, MessageBoxImage.Error);
		}
	}

	private Button CreateActionButton(string label)
	{
		Button button = new()
		{
			Content = label,
			Height = 30,
			Padding = new Thickness(11, 0, 11, 0),
			MinWidth = 72
		};
		if (TryFindResource("SettingsButtonStyle") is Style style)
		{
			button.Style = style;
		}
		return button;
	}

	private Brush GetBrush(string key, Brush fallback)
	{
		return TryFindResource(key) as Brush ?? fallback;
	}
}
