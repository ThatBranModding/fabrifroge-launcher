using Avalonia.Controls;
using Avalonia.Interactivity;

namespace FabriFrogeLauncher;

public enum LauncherUpdateChoice
{
    Update,
    Exit
}

public partial class LauncherUpdateDialog : Window
{
    private bool _allowClose;

    public LauncherUpdateDialog(string latestVersion, string? error)
    {
        InitializeComponent();
        VersionText.Text = latestVersion;
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorText.Text = $"Update failed: {error}";
            ErrorText.IsVisible = true;
        }

        Closing += (_, e) =>
        {
            if (!_allowClose) e.Cancel = true;
        };
    }

    private void UpdateNow_Click(object? sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close(LauncherUpdateChoice.Update);
    }

    private void Exit_Click(object? sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close(LauncherUpdateChoice.Exit);
    }
}
