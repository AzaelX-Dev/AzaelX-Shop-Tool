using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuaToolsGui.Services;
using LuaToolsGui.Models;
using Microsoft.Win32;

namespace LuaToolsGui.ViewModels;

/// <summary>A selectable UI language. <see cref="Tag"/> is one of the supported BCP-47 tags: "en" or "th".</summary>
public record LanguageOption(string Display, string Tag);

/// <summary>A DNS resolution mode. <see cref="Tag"/> is the stored value and stays English ("Auto",
/// "Always", "Never") because it is matched in code; only <see cref="Display"/> is localized.</summary>
public record DnsModeOption(string Display, string Tag);

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SteamService _steam;
    private readonly HubcapService _hubcap;
    private readonly AuthService _auth;

    // ── LuaTools account authentication ─────────────────────────────
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string? _email;
    [ObservableProperty] private string? _avatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRealUser))]
    private bool _isGuest = true;

    public bool IsRealUser => !IsGuest;

    // Discord /login can provision a temporary @bot.lua.tools identity. It authenticates locally,
    // but protected downloads may require the user's fully linked lua.tools account. Surface a link
    // banner rather than letting this state fail later as a generic HTTP 401.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBotLinkBanner))]
    private bool _isBotProvisioned;

    public bool ShowBotLinkBanner => IsBotProvisioned;

    [ObservableProperty] private string? _loginRequiredMessage;

    [RelayCommand]
    private void DismissLoginRequired() => LoginRequiredMessage = null;

    [ObservableProperty] private string _codeInput = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRedeemCode))]
    private bool _isRedeemingCode;

    [ObservableProperty] private string? _codeError;

    public bool CanRedeemCode => !IsRedeemingCode;

    // ── Steam location ──────────────────────────────────────────────
    [ObservableProperty] private string _steamPath = "";
    [ObservableProperty] private bool _isSteamOverridden;
    [ObservableProperty] private string _steamSource = "";
    [ObservableProperty] private string? _steamWarning;

    // ── Install behavior ────────────────────────────────────────────
    /// <summary>Auto Update Apps (Don't Lock Manifests). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _autoUpdateApps;

    partial void OnAutoUpdateAppsChanged(bool value) => _settings.AutoUpdateApps = value;

    /// <summary>Donate spare Steam decryption keys to the community pool. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _donateKeys;

    partial void OnDonateKeysChanged(bool value) => _settings.DonateKeys = value;

    // ── Startup behavior ────────────────────────────────────────────
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "AzaelX-Tools";
    private const string LegacyRunValueName = "LuaTools";

    /// <summary>Launch the app on Windows sign-in (writes HKCU …\Run). Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        _settings.StartWithWindows = value;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            if (value)
                // --minimized: start silently in the tray on sign-in without stealing focus.
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* registry write blocked. Setting is still saved, just not applied this run */ }
    }

    /// <summary>Minimize to the system tray instead of the taskbar. Persisted via SettingsService.</summary>
    [ObservableProperty] private bool _minimizeToTray;

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settings.MinimizeToTray = value;
        if (!value) RequestShowWindow?.Invoke(); // turning it off restores the window if it's hidden in the tray
    }

    /// <summary>Set by App: restore the main window from the tray (used when Minimize-to-tray is turned off).</summary>
    public Action? RequestShowWindow { get; set; }

    // ── Appearance ──────────────────────────────────────────────────
    [ObservableProperty] private bool _isDarkTheme = true;

    partial void OnIsDarkThemeChanged(bool value)
    {
        string theme = value ? "Dark" : "Light";
        _settings.ThemeMode = theme;
        AppThemeService.Apply(theme);
    }

    // ── Language ────────────────────────────────────────────────────
    /// <summary>Only English and Thai are offered in the UI language selector.</summary>
    public ObservableCollection<LanguageOption> LanguageOptions { get; } =
    [
        new("ไทย", "th"),
        new("English", "en"),
    ];

    [ObservableProperty] private LanguageOption _selectedLanguage = null!;

    // ── DNS ─────────────────────────────────────────────────────────
    /// <summary>How host names are resolved. See <see cref="Services.AppHttp"/>.</summary>
    public ObservableCollection<DnsModeOption> DnsModeOptions { get; } =
    [
        new(Resources.Strings.Settings_Dns_Auto, "Auto"),
        new(Resources.Strings.Settings_Dns_Always, "Always"),
        new(Resources.Strings.Settings_Dns_Never, "Never"),
    ];

    [ObservableProperty] private DnsModeOption _selectedDnsMode = null!;

    // Takes effect on new connections; pooled ones turn over within AppHttp's connection lifetime, so
    // there is no restart prompt here (unlike the language switch, which is baked in at parse time).
    partial void OnSelectedDnsModeChanged(DnsModeOption value)
    {
        if (value is not null) _settings.DnsMode = value.Tag;
    }

    private bool _suppressLanguagePrompt; // true during ctor init so we don't prompt on first bind

    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        if (value is null || _suppressLanguagePrompt) return;
        _settings.Language = value.Tag;
        // The whole UI is built with parse-time x:Static resources, so a relaunch is needed to re-read it.
        RequestRestartPrompt?.Invoke();
    }

    // ── Hubcap API key ──────────────────────────────────────────────
    /// <summary>The key the user is typing/pasting. Starts blank. The saved key is never shown back.</summary>
    [ObservableProperty] private string _hubcapKeyInput = "";

    /// <summary>Status line under the key box (usage/expiry on success, or an error). Null = hidden.</summary>
    [ObservableProperty] private string? _hubcapKeyStatus;

    /// <summary>Color for the status line: red on error, green on success.</summary>
    [ObservableProperty] private string _hubcapKeyStatusColor = "#22c55e";
    /// <summary>True when a key is saved in settings (drives the "Clear" button + "configured" label).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    private bool _hubcapIsKeyConfigured;

    /// <summary>True while validating (disables the buttons via <see cref="CanEditHubcapKey"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditHubcapKey))]
    private bool _isValidatingHubcapKey;

    public bool CanEditHubcapKey => !IsValidatingHubcapKey;

    [ObservableProperty] private bool _isRefreshingHubcapStats;

    /// <summary>Latest stats for the saved key. Null = not loaded / no key / error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HubcapStatsDisplay))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsText))]
    [NotifyPropertyChangedFor(nameof(HubcapStatsPending))]
    [NotifyPropertyChangedFor(nameof(HubcapUsagePercent))]
    [NotifyPropertyChangedFor(nameof(ShowHubcapStats))]
    private HubcapStats? _hubcapStats;

    /// <summary>"12 / 25 requests today" or "12 / 25 requests today · expires 2026-08-15".</summary>
    public string? HubcapStatsDisplay =>
        HubcapStats is not null ? FormatHubcapStats(HubcapStats) : null;

    /// <summary>Progress bar fill 0.0–1.0.</summary>
    public double HubcapUsagePercent =>
        HubcapStats is { DailyLimit: > 0 } ? (double)HubcapStats.DailyUsage / HubcapStats.DailyLimit : 0;

    /// <summary>Show the usage card whenever a key is configured. It renders a loading state until stats
    /// arrive (never blank), so the section can't look broken while the fetch is in flight or after a
    /// transient failure (the key input is collapsed once configured, so this card is the only content).</summary>
    public bool ShowHubcapStats => HubcapIsKeyConfigured;

    /// <summary>Stats not yet loaded for a configured key → show the "Loading…" placeholder + indeterminate bar.</summary>
    public bool HubcapStatsPending => HubcapIsKeyConfigured && HubcapStats is null;

    /// <summary>Placeholder text shown until real stats load.</summary>
    public string HubcapStatsText => HubcapStatsDisplay ?? Resources.Strings.Common_Loading;

    /// <summary>Countdown to the provider's daily-limit reset at midnight EST (fixed UTC-5).
    /// This is intentionally separate from ApiKeyExpiresAt, which is the key's expiry date.</summary>
    [ObservableProperty] private string _hubcapResetCountdown = "";
    private readonly DispatcherTimer _hubcapResetTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private void UpdateHubcapResetCountdown()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var estOffset = TimeSpan.FromHours(-5); // Provider says Midnight EST (UTC-5), fixed offset.
        var estNow = nowUtc.ToOffset(estOffset);
        var nextMidnightEst = new DateTimeOffset(
            estNow.Year, estNow.Month, estNow.Day, 0, 0, 0, estOffset).AddDays(1);
        var remaining = nextMidnightEst.UtcDateTime - nowUtc.UtcDateTime;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        var countdown = $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        HubcapResetCountdown = string.Format(Resources.Strings.Settings_HubcapResetCountdown, countdown);
    }

    /// <summary>Set by App: actually relaunch the app (used after a language change).</summary>
    public Action? RequestRestart { get; set; }

    /// <summary>Show the "language changed. Restart now?" toast. Wired here so the VM stays UI-agnostic;
    /// App provides the toast + restart action.</summary>
    public Action? RequestRestartPrompt { get; set; }

    public SettingsViewModel(SettingsService settings, SteamService steam,
        HubcapService hubcap, AuthService auth)
    {
        _settings = settings;
        _steam = steam;
        _hubcap = hubcap;
        _auth = auth;
        _auth.AuthStateChanged += OnAuthStateChanged;
        SyncAuthState();
        UpdateHubcapResetCountdown();
        _hubcapResetTimer.Tick += (_, _) => UpdateHubcapResetCountdown();
        _hubcapResetTimer.Start();
        RefreshSteam();
        _autoUpdateApps = settings.AutoUpdateApps; // init from saved value (default ON) without triggering Save
        _donateKeys = settings.DonateKeys;
        _startWithWindows = settings.StartWithWindows; // default OFF. Init without triggering the registry write
        _minimizeToTray = settings.MinimizeToTray;
        _isDarkTheme = settings.ThemeMode == "Dark"; // initialize without triggering a settings write
        _hubcapIsKeyConfigured = !string.IsNullOrEmpty(settings.HubcapApiKey);

        // Keep the selector aligned with the effective UI culture. Older settings may contain a removed
        // language tag, and a fresh install has no saved choice; normalize either case to English/Thai.
        _suppressLanguagePrompt = true;
        string activeTag = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("th", StringComparison.OrdinalIgnoreCase)
            ? "th"
            : "en";
        string? savedTag = LanguageOptions.FirstOrDefault(
            o => o.Tag.Equals(settings.Language, StringComparison.OrdinalIgnoreCase))?.Tag;
        string selectedTag = savedTag ?? activeTag;
        _selectedLanguage = LanguageOptions.First(o => o.Tag == selectedTag);
        if (!string.Equals(settings.Language, selectedTag, StringComparison.OrdinalIgnoreCase))
            _settings.Language = selectedTag;
        _suppressLanguagePrompt = false;

        // Assign the backing field, not the property: going through the setter would fire the change
        // handler and write the default straight back to disk on first open.
        _selectedDnsMode = DnsModeOptions.FirstOrDefault(o => o.Tag == settings.DnsMode) ?? DnsModeOptions[0];
    }

    private void OnAuthStateChanged()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(SyncAuthState));
            return;
        }
        SyncAuthState();
    }

    private void SyncAuthState()
    {
        IsGuest = _auth.IsGuest;
        DisplayName = _auth.DisplayName;
        Email = _auth.Email;
        AvatarUrl = _auth.AvatarUrl;
        IsBotProvisioned = _auth.IsBotProvisioned;
        if (!IsGuest) LoginRequiredMessage = null;
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        CodeError = null;
        try
        {
            await _auth.SignInAsync();
            SyncAuthState();
        }
        catch (Exception ex)
        {
            CodeError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SignInWithCodeAsync()
    {
        string code = CodeInput.Trim();
        if (code.Length != 6) return;

        IsRedeemingCode = true;
        CodeError = null;
        try
        {
            await _auth.SignInWithCodeAsync(code);
            CodeInput = "";
            SyncAuthState();
        }
        catch (Exception ex)
        {
            CodeError = ex.Message;
        }
        finally
        {
            IsRedeemingCode = false;
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        _auth.SignOut();
        SyncAuthState();
    }

    private void RefreshSteam()
    {
        string? path = _steam.EffectivePath;
        IsSteamOverridden = _steam.IsOverridden;
        SteamPath = path ?? Resources.Strings.Settings_SteamNotFound;
        SteamSource = IsSteamOverridden ? Resources.Strings.Settings_SteamSource_Custom
            : path is null ? Resources.Strings.Settings_SteamSource_NotFound
            : Resources.Strings.Settings_SteamSource_Auto;
        SteamWarning = path is not null && !_steam.IsValid ? Resources.Strings.Settings_SteamWarning_NoExe : null;
    }

    [RelayCommand]
    private void OverrideSteamFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Resources.Strings.Settings_ChooseSteamFolder,
            InitialDirectory = _steam.EffectivePath ?? "",
        };
        if (dialog.ShowDialog() == true)
        {
            _settings.SteamPathOverride = dialog.FolderName;
            RefreshSteam();
        }
    }

    [RelayCommand]
    private void ClearSteamOverride()
    {
        _settings.SteamPathOverride = null;
        RefreshSteam();
    }

    [RelayCommand]
    private void OpenSteamFolder()
    {
        string? path = _steam.EffectivePath;
        if (path is not null && System.IO.Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWebsite() =>
        Process.Start(new ProcessStartInfo(AppConfig.ApiBaseUrl) { UseShellExecute = true });

    [RelayCommand]
    private void OpenHubcap() =>
        Process.Start(new ProcessStartInfo(AppConfig.HubcapBaseUrl) { UseShellExecute = true });

    // ── Hubcap key management ───────────────────────────────────────

    /// <summary>Called by the View when loaded. Auto-refreshes stats if a key is saved.</summary>
    public void OnViewLoaded()
    {
        if (HubcapIsKeyConfigured)
            RefreshHubcapStatsCommand.Execute(null);
    }

    /// <summary>Re-fetch usage stats for the saved key. Silent no-op if no key is saved.</summary>
    [RelayCommand]
    private async Task RefreshHubcapStatsAsync()
    {
        string? key = _settings.HubcapApiKey;
        if (string.IsNullOrEmpty(key)) return;

        IsRefreshingHubcapStats = true;
        try
        {
            HubcapStats = await _hubcap.GetStatsAsync(key);
        }
        catch
        {
            HubcapStats = null;
        }
        finally
        {
            IsRefreshingHubcapStats = false;
        }
    }

    /// <summary>Validate the typed key (format first, then a live stats call) and, if good, save it.</summary>
    [RelayCommand]
    private async Task ValidateAndSaveHubcapKeyAsync()
    {
        string key = HubcapKeyInput.Trim();

        if (!HubcapService.IsValidKeyFormat(key))
        {
            ShowHubcapStatus(Resources.Strings.Settings_HubcapKeyBad, isError: true);
            return;
        }

        IsValidatingHubcapKey = true;
        try
        {
            var stats = await _hubcap.GetStatsAsync(key);
            if (stats is null)
            {
                // Could be a bad/expired key (401), or a network problem. Both surface as null.
                ShowHubcapStatus(Resources.Strings.Settings_HubcapKeyError, isError: true);
                return;
            }

            _settings.HubcapApiKey = key;
            HubcapIsKeyConfigured = true;
            HubcapKeyInput = "";
            HubcapKeyStatus = null;
            HubcapStats = stats;
        }
        finally
        {
            IsValidatingHubcapKey = false;
        }
    }

    /// <summary>Forget the saved key and reset the status line.</summary>
    [RelayCommand]
    private void ClearHubcapKey()
    {
        _settings.HubcapApiKey = null;
        HubcapIsKeyConfigured = false;
        HubcapKeyInput = "";
        HubcapKeyStatus = null;
        HubcapStats = null;
    }

    private void ShowHubcapStatus(string text, bool isError)
    {
        HubcapKeyStatus = text;
        HubcapKeyStatusColor = isError ? "#f87171" : "#22c55e";
    }

    /// <summary>"4/10 today". Appends "· expires yyyy-MM-dd" when the key has an expiry.</summary>
    private static string FormatHubcapStats(HubcapStats stats)
    {
        string usage = string.Format(Resources.Strings.Settings_HubcapKeyOk, stats.DailyUsage, stats.DailyLimit);
        if (DateTimeOffset.TryParse(stats.ApiKeyExpiresAt, out var expiry))
            return string.Format(Resources.Strings.Settings_HubcapKeyOkExpiry, stats.DailyUsage, stats.DailyLimit,
                expiry.ToString("yyyy-MM-dd"));
        return usage;
    }
}
