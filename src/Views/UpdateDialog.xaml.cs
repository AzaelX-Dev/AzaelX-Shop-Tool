using System.Windows;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using Velopack;

namespace LuaToolsGui.Views;

/// <summary>Consent-first update UI: the package is downloaded only after the user clicks Update Now.</summary>
public partial class UpdateDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateService _updates;
    private bool _downloadInProgress;

    public UpdateDialog(UpdateService updates, UpdateInfo updateInfo, string currentVersion)
    {
        _updates = updates;
        InitializeComponent();
        CurrentVersionValue.Text = currentVersion;
        NewVersionValue.Text = updateInfo.TargetFullRelease.Version.ToString();
        StatusText.Text = string.Empty;
        Closing += (_, e) =>
        {
            // Do not allow the dialog to disappear while a package is being prepared. The user chose
            // Update Now, and closing mid-download could leave the experience looking completed.
            if (_downloadInProgress) e.Cancel = true;
        };
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadInProgress) return;
        Close();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadInProgress) return;
        _downloadInProgress = true;
        UpdateButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ProgressText.Text = string.Format(System.Globalization.CultureInfo.CurrentUICulture,
            Strings.Update_ProgressFormat, 0);
        StatusText.Text = Strings.Update_Downloading;

        try
        {
            if (!_updates.HasStagedUpdate)
            {
                IProgress<int> progress = new Progress<int>(value =>
                {
                    int bounded = Math.Clamp(value, 0, 100);
                    DownloadProgress.Value = bounded;
                    ProgressText.Text = string.Format(System.Globalization.CultureInfo.CurrentUICulture,
                        Strings.Update_ProgressFormat, bounded);
                });

                await _updates.DownloadAndStageAsync(value => progress.Report(value));
            }

            DownloadProgress.Value = 100;
            ProgressText.Text = string.Format(System.Globalization.CultureInfo.CurrentUICulture,
                Strings.Update_ProgressFormat, 100);
            StatusText.Text = Strings.Update_ReadyToRestart;
            await Task.Delay(750);

            string[]? restartArgs = Program.SessionTrayLock ? ["--tray-locked"] : null;
            _updates.ApplyStagedAndRestart(restartArgs);
        }
        catch
        {
            _downloadInProgress = false;
            StatusText.Text = Strings.Update_DownloadFailed;
            UpdateButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }
}
