using System.Windows;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;

namespace LuaToolsGui.Views;

public partial class DeviceAuthorizationWindow : Window
{
    private readonly DeviceAuthorizationService _authorization;
    private bool _checkRunning;

    public DeviceAuthorizationWindow(DeviceAuthorizationService authorization, string initialStatus)
    {
        _authorization = authorization;
        InitializeComponent();
        HardwareIdText.Text = authorization.HardwareId;
        if (string.IsNullOrWhiteSpace(authorization.HardwareId))
            CopyButton.IsEnabled = false;
        SetStatus(initialStatus, error: true);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_authorization.HardwareId)) return;
        try
        {
            Clipboard.SetText(_authorization.HardwareId);
            SetStatus(Strings.Auth_Copied, error: false);
        }
        catch
        {
            SetStatus(Strings.Auth_HwidError, error: true);
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_checkRunning) return;
        _checkRunning = true;
        CheckButton.IsEnabled = false;
        SetStatus(Strings.Auth_Checking, error: false);
        try
        {
            var result = await _authorization.CheckAsync();
            SetStatus(result.Message, error: !result.IsAuthorized);
            if (result.IsAuthorized)
                DialogResult = true;
        }
        finally
        {
            _checkRunning = false;
            if (IsVisible) CheckButton.IsEnabled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = error ? System.Windows.Media.Brushes.IndianRed : System.Windows.Media.Brushes.SeaGreen;
    }
}
