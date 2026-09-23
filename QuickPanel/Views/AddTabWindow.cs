using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using QuickPanel.Models;
using QuickPanel.Services;
using Microsoft.Win32;

namespace QuickPanel.Views;

public enum AddTabFocusTarget
{
	Name,
	Url,
	Icon
}

public partial class AddTabWindow : Window, IComponentConnector
{
	private sealed record ProfileChoice(
		string RegistryId,
		string Label,
		string DisplayText,
		string Description,
		bool CreatesNew = false);

	private readonly TabIconService _previewIconService = new TabIconService();
	private readonly BrowserProfileRegistryService _profileRegistry = new BrowserProfileRegistryService();

	private IReadOnlyList<AiTab> _profileSourceTabs = Array.Empty<AiTab>();

	private DispatcherTimer? _previewDebounceTimer;

	private AddTabFocusTarget _focusTarget = AddTabFocusTarget.Name;
	private bool _isEditing;

	public AiTab? CreatedTab { get; private set; }

	public AddTabWindow()
	{
		InitializeComponent();
	}

	public void ConfigureForEdit(AiTab tab, AddTabFocusTarget focusTarget)
	{
		Title = "Edit tab";
		HeadingText.Text = "Edit tab";
		AddButton.Content = "Save";
		_focusTarget = focusTarget;
		_isEditing = true;
		NameTextBox.Text = tab.Name;
		UrlTextBox.Text = tab.Url;
		IconTextBox.Text = tab.Icon ?? string.Empty;
	}

	public void ConfigureProfiles(IEnumerable<AiTab> tabs, string? selectedProfileId = null)
	{
		_profileSourceTabs = tabs.ToArray();
		IReadOnlyList<BrowserProfileDefinition> profiles = _profileRegistry.Synchronize(_profileSourceTabs);
		var choices = profiles
			.Select(profile => new ProfileChoice(
				profile.Id,
				profile.Name,
				profile.Name + (profile.IsDefault ? " (default)" : string.Empty),
				profile.Id.Equals(BrowserProfileRegistryService.DefaultProfileId, StringComparison.OrdinalIgnoreCase)
					? "Uses the original shared Quick Panel login and cookies."
					: "Uses the isolated cookies and login saved for " + profile.Name + "."))
			.ToList();
		choices.Add(new ProfileChoice(
			RegistryId: string.Empty,
			Label: "New profile",
			DisplayText: "+ Create new profile…",
			Description: "Creates a named independent cookie jar. You will be asked for a profile name.",
			CreatesNew: true));
		ProfileComboBox.ItemsSource = choices;

		string registryProfileId;
		if (_isEditing)
		{
			registryProfileId = BrowserProfileRegistryService.GetRegistryId(selectedProfileId);
		}
		else
		{
			registryProfileId = profiles.First(profile => profile.IsDefault).Id;
		}
		ProfileComboBox.SelectedItem = choices.FirstOrDefault(choice =>
			!choice.CreatesNew && choice.RegistryId.Equals(registryProfileId, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
		UpdateProfileDescription();
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		TextBox focusBox = _focusTarget switch
		{
			AddTabFocusTarget.Url => UrlTextBox,
			AddTabFocusTarget.Icon => IconTextBox,
			_ => NameTextBox
		};
		focusBox.Focus();
		focusBox.SelectAll();
		UpdateIconPreview();
	}

	private void AddButton_Click(object sender, RoutedEventArgs e)
	{
		string text = NameTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			ShowValidationMessage("Enter a name for the tab.", NameTextBox);
			return;
		}
		if (!SettingsService.TryNormalizeUrl(UrlTextBox.Text, out string normalizedUrl))
		{
			ShowValidationMessage("Enter a valid website address.", UrlTextBox);
			return;
		}

		ProfileChoice? profileChoice = ProfileComboBox.SelectedItem as ProfileChoice;
		BrowserProfileDefinition? selectedProfile;
		if (profileChoice?.CreatesNew == true)
		{
			ProfileNameWindow dialog = new ProfileNameWindow("Create browsing profile")
			{
				Owner = this
			};
			if (dialog.ShowDialog() != true)
			{
				return;
			}
			try
			{
				selectedProfile = _profileRegistry.Create(dialog.ProfileName);
			}
			catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
			{
				ShowExternalError("Could not create profile: " + ex.Message);
				return;
			}
		}
		else
		{
			IReadOnlyList<BrowserProfileDefinition> profiles = _profileRegistry.Synchronize(_profileSourceTabs);
			selectedProfile = profiles.FirstOrDefault(profile =>
				profile.Id.Equals(profileChoice?.RegistryId ?? BrowserProfileRegistryService.DefaultProfileId, StringComparison.OrdinalIgnoreCase));
		}
		if (selectedProfile == null)
		{
			ShowExternalError("The selected browsing profile no longer exists. Choose another profile.");
			return;
		}

		string? browserProfileId = BrowserProfileRegistryService.GetBrowserProfileId(selectedProfile.Id);
		CreatedTab = new AiTab
		{
			Id = SettingsService.CreateCustomTabId(),
			Name = text,
			Url = normalizedUrl,
			Icon = NormalizeIconInput(IconTextBox.Text),
			BrowserProfileId = browserProfileId,
			BrowserProfileLabel = browserProfileId == null ? null : selectedProfile.Name,
			IsCustom = true
		};
		base.DialogResult = true;
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
	}

	private void BrowseIconButton_Click(object sender, RoutedEventArgs e)
	{
		OpenFileDialog dialog = new OpenFileDialog
		{
			Title = "Choose tab icon",
			Filter = "SVG image (*.svg)|*.svg",
			CheckFileExists = true,
			Multiselect = false
		};
		if (dialog.ShowDialog(this) == true)
		{
			IconTextBox.Text = dialog.FileName;
			IconTextBox.CaretIndex = IconTextBox.Text.Length;
		}
	}

	private static string? NormalizeIconInput(string input)
	{
		string value = input.Trim();
		return string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)
			? null
			: value;
	}

	public void ShowExternalError(string message)
	{
		ValidationMessage.Text = message;
		ValidationMessage.Visibility = Visibility.Visible;
	}

	private void ShowValidationMessage(string message, TextBox field)
	{
		ShowExternalError(message);
		field.Focus();
		field.SelectAll();
	}

	private void AnyTextBox_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (ValidationMessage != null)
		{
			ValidationMessage.Visibility = Visibility.Collapsed;
		}
		QueuePreviewUpdate();
	}

	private void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateProfileDescription();
	}

	private void UpdateProfileDescription()
	{
		if (ProfileDescriptionText != null && ProfileComboBox?.SelectedItem is ProfileChoice choice)
		{
			ProfileDescriptionText.Text = choice.Description;
		}
	}

	private void QueuePreviewUpdate()
	{
		if (_previewDebounceTimer == null)
		{
			_previewDebounceTimer = new DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(350)
			};
			_previewDebounceTimer.Tick += delegate
			{
				_previewDebounceTimer.Stop();
				UpdateIconPreview();
			};
		}
		_previewDebounceTimer.Stop();
		_previewDebounceTimer.Start();
	}

	private void UpdateIconPreview()
	{
		if (IconPreviewHost == null)
		{
			return;
		}
		string name = NameTextBox.Text.Trim();
		string icon = NormalizeIconInput(IconTextBox.Text) ?? string.Empty;
		string url = UrlTextBox.Text.Trim();
		if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(url) && string.IsNullOrEmpty(icon))
		{
			IconPreviewHost.Child = null;
			return;
		}
		AiTab previewTab = new AiTab
		{
			Name = string.IsNullOrEmpty(name) ? "?" : name,
			Url = url,
			Icon = string.IsNullOrEmpty(icon) ? null : icon,
			IsCustom = true
		};
		IconPreviewHost.Child = _previewIconService.CreateImage(previewTab, 18);
	}

	protected override void OnClosed(EventArgs e)
	{
		_previewDebounceTimer?.Stop();
		base.OnClosed(e);
	}
}
