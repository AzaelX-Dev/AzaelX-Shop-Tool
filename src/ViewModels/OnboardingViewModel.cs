using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Models;
using LuaToolsGui.Services;

namespace LuaToolsGui.ViewModels;

/// <summary>
/// First-run welcome overlay. Shown once (gated by <see cref="CacheService.OnboardingComplete"/>) over the
/// whole app: offers to apply the recommended unlocker mode, then dismisses.
/// </summary>
public partial class OnboardingViewModel : ObservableObject
{
    private readonly CacheService _cache;
    private readonly SettingsService _settings;
    private readonly UnlockerService _unlocker;
    private readonly SteamService _steam;
    private readonly ToastService _toast;

    public OnboardingViewModel(CacheService cache, SettingsService settings,
        UnlockerService unlocker, SteamService steam, ToastService toast)
    {
        _cache = cache;
        _settings = settings;
        _unlocker = unlocker;
        _steam = steam;
        _toast = toast;
    }

    /// <summary>Whether the overlay is visible. Set true by App on a fresh first launch.</summary>
    [ObservableProperty] private bool _isOpen;

    /// <summary>Set by App: refresh the Home dashboard after onboarding applies its actions.</summary>
    public Func<Task>? RefreshHome { get; set; }

    // The choice defaults to "yes".
    [ObservableProperty] private bool _applyRecommended = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    private bool _isBusy;
    public bool NotBusy => !IsBusy;

    [RelayCommand]
    private void SetApplyRecommended(bool value) => ApplyRecommended = value;

    /// <summary>"Let's go!". Mark onboarding done, close the overlay immediately, then apply the chosen
    /// actions in the background. Closing first means a long-running (Steam-restarting) install can never
    /// leave the welcome dialog stuck.</summary>
    [RelayCommand]
    private void Finish()
    {
        if (IsBusy) return;
        bool applyRecommended = ApplyRecommended;

        _cache.OnboardingComplete = true; // seen, never show again
        IsOpen = false;                    // dismiss now, before any slow work

        if (applyRecommended)
            _ = ApplyChoicesAsync();
    }

    /// <summary>Applies the recommended settings in the background after the overlay is closed.</summary>
    private async Task ApplyChoicesAsync()
    {
        IsBusy = true;
        _toast.Show(Resources.Strings.Onboarding_Title, Resources.Strings.Onboarding_Applying);
        bool steamStopped = false;
        try
        {
            await Task.Run(_steam.StopSteam);
            steamStopped = true;
            var result = await _unlocker.InstallAsync(UnlockerMode.Bst); // the Recommended mode
            if (!result.Success)
                _toast.Show(Resources.Strings.Onboarding_Title, result.Error ?? "", error: true);
        }
        catch (Exception ex)
        {
            _toast.Show(Resources.Strings.Onboarding_Title, ex.Message, error: true);
        }
        finally
        {
            try
            {
                if (steamStopped)
                    await Task.Run(_steam.StartSteam);
            }
            catch (Exception ex)
            {
                _toast.Show(Resources.Strings.Onboarding_Title, ex.Message, error: true);
            }
            finally
            {
                IsBusy = false;
                // Refresh the Home dashboard so the mode reflects what we just installed.
                if (RefreshHome is not null)
                    try { await RefreshHome(); } catch { /* best effort */ }
            }
        }
    }
}
