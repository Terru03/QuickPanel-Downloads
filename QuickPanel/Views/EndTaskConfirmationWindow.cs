using System;
using System.Windows;
using System.Windows.Input;
using QuickPanel.Services;

namespace QuickPanel.Views;

public partial class EndTaskConfirmationWindow : Window
{
	public EndTaskConfirmationWindow(string processName, int processId)
	{
		InitializeComponent();
		ProcessNameText.Text = string.IsNullOrWhiteSpace(processName) ? "Selected process" : processName;
		ProcessIdText.Text = "Process ID " + processId;
	}

	private void Window_Loaded(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		WindowAppearanceService.Apply(this);
		CancelButton.Focus();
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		DialogResult = false;
	}

	private void ConfirmButton_Click(object sender, RoutedEventArgs e)
	{
		_ = sender;
		_ = e;
		DialogResult = true;
	}

	private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		_ = sender;
		if (e.Key != Key.Escape) return;
		DialogResult = false;
		e.Handled = true;
	}

	private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		_ = sender;
		if (e.ButtonState == MouseButtonState.Pressed) DragMove();
	}
}
