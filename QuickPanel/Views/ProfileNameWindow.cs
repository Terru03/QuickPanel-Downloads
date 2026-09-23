using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickPanel.Views;

public sealed class ProfileNameWindow : Window
{
	private readonly TextBox _nameTextBox;
	private readonly TextBlock _validationText;

	public ProfileNameWindow(string heading, string? initialName = null)
	{
		Title = heading;
		Width = 390;
		Height = 190;
		ResizeMode = ResizeMode.NoResize;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		WindowStyle = WindowStyle.ToolWindow;
		ShowInTaskbar = false;
		Background = TryBrush("PanelChromeBrush", Brushes.White);
		Foreground = TryBrush("PanelTextBrush", Brushes.Black);

		Grid root = new()
		{
			Margin = new Thickness(18)
		};
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

		TextBlock title = new()
		{
			Text = heading,
			FontSize = 16,
			FontWeight = FontWeights.SemiBold
		};
		root.Children.Add(title);

		_nameTextBox = new TextBox
		{
			Text = initialName ?? string.Empty,
			Margin = new Thickness(0, 12, 0, 0),
			Height = 32,
			Padding = new Thickness(8, 5, 8, 5),
			MaxLength = 40,
			VerticalContentAlignment = VerticalAlignment.Center
		};
		Grid.SetRow(_nameTextBox, 1);
		root.Children.Add(_nameTextBox);

		_validationText = new TextBlock
		{
			Margin = new Thickness(0, 5, 0, 0),
			Foreground = Brushes.IndianRed,
			Visibility = Visibility.Collapsed
		};
		Grid.SetRow(_validationText, 2);
		root.Children.Add(_validationText);

		StackPanel buttons = new()
		{
			Orientation = Orientation.Horizontal,
			HorizontalAlignment = HorizontalAlignment.Right,
			Margin = new Thickness(0, 15, 0, 0)
		};
		Button cancel = new()
		{
			Content = "Cancel",
			MinWidth = 76,
			Height = 30,
			Margin = new Thickness(0, 0, 8, 0),
			IsCancel = true
		};
		Button save = new()
		{
			Content = "Save",
			MinWidth = 76,
			Height = 30,
			IsDefault = true
		};
		save.Click += Save_Click;
		buttons.Children.Add(cancel);
		buttons.Children.Add(save);
		Grid.SetRow(buttons, 3);
		root.Children.Add(buttons);

		Content = root;
		Loaded += (_, _) =>
		{
			_nameTextBox.Focus();
			_nameTextBox.SelectAll();
		};
		PreviewKeyDown += (_, e) =>
		{
			if (e.Key == Key.Escape)
			{
				DialogResult = false;
				e.Handled = true;
			}
		};
	}

	public string ProfileName { get; private set; } = string.Empty;

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		string value = _nameTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(value))
		{
			_validationText.Text = "Enter a profile name.";
			_validationText.Visibility = Visibility.Visible;
			_nameTextBox.Focus();
			return;
		}
		ProfileName = value;
		DialogResult = true;
	}

	private static Brush TryBrush(string resourceKey, Brush fallback)
	{
		try
		{
			return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
		}
		catch (InvalidOperationException)
		{
			return fallback;
		}
	}
}
