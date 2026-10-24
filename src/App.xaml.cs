using System.Windows;
using System.Reflection;
using System.Windows.Threading;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LuaToolsGui;

public partial class App : Application
{
    // Shared entry point for the localhost update endpoint. This is wired after the main window exists,
    // and always marshals UI work back to the WPF dispatcher. The flow remains the current, consent-first
    // app updater; legacy silent staging/plugin-update APIs are deliberately not used here.
    internal static Func<Task>? RunUpdateFlow { get; private set; }

    private readonly IHost _host;

    // True when the app was cold-started solely to run a silent install AND MinimizeToTray is off,
    // which means we auto-exit after the balloon so we don't leave a ghost tray icon behind.
    private bool _exitAfterSilentInstall;
    private bool _hostStarted;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<SettingsService>();
                services.AddSingleton<AuthService>();
                services.AddSingleton<DeviceAuthorizationService>();
                services.AddSingleton<CacheService>();
                services.AddSingleton<SteamService>();
                services.AddSingleton<SteamAppListCache>();
                services.AddSingleton<SteamAppInfoCache>();
                services.AddSingleton<CoverCache>();
                services.AddSingleton<ToastService>();
                services.AddSingleton<SteamDepotInfo>();
                services.AddSingleton<LuaVault>();
                services.AddSingleton<Services.AppInfo.LaunchModStore>();
                services.AddSingleton<Services.AppInfo.LaunchOptionsService>();
                services.AddSingleton<LuaInstaller>();
                services.AddSingleton<SteamLibraryService>();
                services.AddSingleton<DonateKeysService>();
                services.AddSingleton<AnalyticsService>();
                services.AddSingleton<GithubProxy>();
                services.AddSingleton<HardwareAppIdService>();
                services.AddSingleton<SteamlessService>();
                services.AddSingleton<SteamAutoCrackService>();
                services.AddSingleton<DepotDownloaderService>();
                services.AddSingleton<DepotCacheMigrationService>();
                services.AddSingleton<AppliedFixIndexService>();
                services.AddSingleton<UnlockerService>();
                services.AddTransient<DropInstallViewModel>(); // one per page (Home, Add)
                services.AddSingleton<LuaToolsApiClient>();
                services.AddSingleton<HubcapService>();
                services.AddSingleton<UpdateService>();
                // Central download queue: the hosted lifetime runs the scheduler pump, and view models
                // resolve the same instance to enqueue and observe.
                services.AddSingleton<Services.Downloads.DownloadQueue>();
                services.AddHostedService(sp => sp.GetRequiredService<Services.Downloads.DownloadQueue>());
                services.AddSingleton<Services.Downloads.ManifestJobFactory>();
                services.AddSingleton<DownloadViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ManageViewModel>();
                services.AddSingleton<BuildsViewModel>();
                services.AddTransient<LaunchOptionsViewModel>(); // one per dialog
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<ModeViewModel>();
                services.AddSingleton<FixesViewModel>();
                services.AddSingleton<DownloadsViewModel>();
                services.AddSingleton<OnboardingViewModel>();
                services.AddSingleton<MainViewModel>();
                // Pages resolved by NavigationView via the DI service provider.
                services.AddSingleton<HomeView>();
                services.AddSingleton<DownloadView>();
                services.AddSingleton<DownloadsView>();
                services.AddSingleton<ManageView>();
                services.AddSingleton<BuildsView>();
                services.AddSingleton<ModeView>();
                services.AddSingleton<FixesView>();
                services.AddSingleton<SettingsView>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    private UpdateService Updates => _host.Services.GetRequiredService<UpdateService>();

    /// <summary>
    /// Warn when Steam has overwritten launch options we'd applied, and offer to put them back.
    ///
    /// <para>
    /// Steam rebuilds appinfo.vdf from PICS on login, app updates and store browsing. It did so twice
    /// while this feature was being written, so an applied edit is not permanent. Re-applying is offered
    /// but never automatic: it closes Steam, which is not something to do behind the user's back at
    /// startup. Costs nothing when no launch options have been edited (the store short-circuits on empty).
    /// </para>
    /// </summary>
    private async Task CheckLaunchOptionDriftAsync()
    {
        try
        {
            var launch = _host.Services.GetRequiredService<Services.AppInfo.LaunchOptionsService>();
            if (launch.Store.IsEmpty) return;

            // Indexing the ~373 MB cache takes a couple of seconds, never on the UI thread.
            var drifted = await Task.Run(launch.FindDrifted);
            if (drifted.Count == 0) return;

            var toast = _host.Services.GetRequiredService<ToastService>();
            Dispatcher.Invoke(() => toast.ShowAction(
                LuaToolsGui.Resources.Strings.Launch_Drift_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_Drift_Body, drifted.Count),
                LuaToolsGui.Resources.Strings.Launch_Drift_Action,
                () => _ = ReapplyDriftedAsync(launch, drifted, toast)));
        }
        catch
        {
            // Cache locked/unreadable: nothing actionable, and this must never block startup.
        }
    }

    /// <summary>
    /// The drift notice's "Re-apply" button: confirm, then write the staged edits back into appinfo.
    ///
    /// <para>
    /// The write runs OFF the UI thread. Unlike the launch-options dialog (which is modal, so its own
    /// synchronous apply merely blocks a window that's already blocking), this fires with the main window
    /// live, and <c>Apply</c> indexes a ~373 MB file, copies a backup and rewrites it. On the UI thread
    /// that's a multi-second freeze of the whole app.
    /// </para>
    /// </summary>
    private static async Task ReapplyDriftedAsync(
        Services.AppInfo.LaunchOptionsService launch, IReadOnlyList<int> drifted, ToastService toast)
    {
        // Same wording as the dialog's own prompt: closing Steam should never read as a different
        // decision depending on where it was triggered from.
        if (MessageBox.Show(
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Body,
                LuaToolsGui.Resources.Strings.Launch_ApplyNow_Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var result = await Task.Run(() => launch.Reapply(drifted));

        if (result.Ok)
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                result.SteamWasRunning
                    ? LuaToolsGui.Resources.Strings.Launch_Applied_Restarted
                    : LuaToolsGui.Resources.Strings.Launch_Applied);
        else
            toast.Show(LuaToolsGui.Resources.Strings.Launch_Title,
                string.Format(LuaToolsGui.Resources.Strings.Launch_ApplyFailed, result.Error), error: true);
    }

    private async Task CheckAndPromptForUpdateAsync(MainWindow owner, MainViewModel main)
    {
        UpdateService.WriteDiagnostic($"Startup update flow entered. IsVisible={owner.IsVisible}; OnboardingOpen={main.Onboarding.IsOpen}; Silent={Program.StartMinimized || Program.SessionTrayLock}.");
        try
        {
            // Let the main window render. Do not wait indefinitely for onboarding to close: doing so
            // could prevent update checks forever on a first-run or stale onboarding state.
            await Task.Delay(1500);

            if (!owner.IsVisible || Dispatcher.HasShutdownStarted)
            {
                UpdateService.WriteDiagnostic($"Startup update flow aborted before check. IsVisible={owner.IsVisible}; DispatcherShutdown={Dispatcher.HasShutdownStarted}.");
                return;
            }

            if (main.Onboarding.IsOpen)
                UpdateService.WriteDiagnostic("Onboarding is still open; proceeding with the update check instead of blocking indefinitely.");

            var update = await Updates.CheckForUpdateAsync();
            if (update is null)
            {
                UpdateService.WriteDiagnostic("No update dialog shown because CheckForUpdateAsync returned null. See preceding lines for the reason.");
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (!owner.IsVisible)
                {
                    UpdateService.WriteDiagnostic("Update was found but the main window was no longer visible; dialog not shown.");
                    return;
                }
                string currentVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                    ?? "?";
                int plus = currentVersion.IndexOf('+');
                if (plus >= 0) currentVersion = currentVersion[..plus];

                UpdateService.WriteDiagnostic($"Showing update dialog. Current={currentVersion}; target={update.TargetFullRelease.Version}.");
                var dialog = new UpdateDialog(Updates, update, currentVersion) { Owner = owner };
                dialog.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            UpdateService.WriteDiagnostic($"Unhandled startup update flow error: {ex}");
            // Update checks must never stop the main UI or prevent the user from working offline.
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply the saved appearance before any user-facing window is created.
        AppThemeService.Apply(_host.Services.GetRequiredService<SettingsService>().ThemeMode);

        // Point the shared HTTP handler at the DNS setting before anything makes a request. It calls
        // this per connection rather than reading it now, so the order is not load-bearing — but doing
        // it first means the very first call of the session already honours the user's choice.
        var dnsSettings = _host.Services.GetRequiredService<SettingsService>();
        AppHttp.ModeProvider = () => dnsSettings.DnsMode;

        // Device authorization runs BEFORE hosted services start. This keeps a pending device from
        // starting background download queues or executing a luatools:// install URL while unapproved.
        var deviceAuthorization = _host.Services.GetRequiredService<DeviceAuthorizationService>();
        var authorizationCheck = await deviceAuthorization.CheckAsync();
        if (!authorizationCheck.IsAuthorized)
        {
            var authorizationWindow = new DeviceAuthorizationWindow(deviceAuthorization, authorizationCheck.Message);
            bool? approved = authorizationWindow.ShowDialog();
            if (approved != true)
            {
                Shutdown();
                return;
            }
        }

        // Legacy cleanup: older builds staged downloads in ~/Downloads/LuaTools (they now stage in
        // %TEMP% and self-delete). Remove any leftovers from that user-visible folder, best-effort.
        // Also sweep the current %TEMP% staging folder: a crash mid-download, or an overwrite confirm
        // the user never answered, leaves a staged zip nobody will ever delete.
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string legacy = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LuaTools");
                if (System.IO.Directory.Exists(legacy)) System.IO.Directory.Delete(legacy, recursive: true);
            }
            catch { /* best effort, never block startup on cleanup */ }

            Services.Downloads.HttpFileDownloader.SweepStale();
        });

        await _host.StartAsync();
        _hostStarted = true;

        // Restore a LuaTools account session before ViewModels are created. A missing or expired session
        // simply leaves the user in guest mode; authentication must not prevent the app from starting.
        try { await _host.Services.GetRequiredService<AuthService>().InitializeAsync(); }
        catch (Exception ex) { UpdateService.WriteDiagnostic($"LuaTools auth restore failed; continuing as guest: {ex.Message}"); }

        // Rewrite any pre-3-mode SelectedMode BEFORE anything reads it. UnlockerService.SelectedMode
        // would otherwise parse a legacy value to null and quietly present an unconfigured app. Users
        // whose old mode value was retired have no mode now, so
        // onboarding is forced back open: OnboardingComplete is a permanent flag that every existing
        // user already has set, and clearing SelectedMode alone would leave them with no mode AND no
        // overlay explaining why.
        if (ModeMigration.Apply(_host.Services.GetRequiredService<SettingsService>()))
            _host.Services.GetRequiredService<CacheService>().OnboardingComplete = false;

        // Point OST/BST at config/stplug-in so lua writes hot-reload. Must run AFTER the migration
        // above, which is what makes SelectedMode parse. The app no longer tells anyone to restart
        // Steam for a lua change, so this registration is what makes that promise true — and it
        // previously only ever ran during a mode install through this app.
        _host.Services.GetRequiredService<UnlockerService>().EnsureLuaPathRegistered();

        var main = _host.Services.GetRequiredService<MainViewModel>();
        var settingsVm = _host.Services.GetRequiredService<SettingsViewModel>();

        // Changing the language needs a relaunch (x:Static resources resolve at parse time).
        settingsVm.RequestRestart = RelaunchApp;

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window; // the authorization dialog, when shown, is never the app's main window

        // The old localhost endpoint may request an update check from a background HTTP thread. Reuse
        // the same current consent-first dialog flow as startup, with all WPF access on the dispatcher.
        RunUpdateFlow = () => Dispatcher.CheckAccess()
            ? CheckAndPromptForUpdateAsync(window, main)
            : Dispatcher.InvokeAsync(() => CheckAndPromptForUpdateAsync(window, main)).Task.Unwrap();

        // Turning off "Minimize to tray" while hidden in the tray → bring the window back.
        settingsVm.RequestShowWindow = () => Dispatcher.Invoke(window.RestoreFromTray);

        // Relaunching the app (single-instance) signals this event → surface the existing window and
        // check for any protocol URL a second instance wrote. AutoReset + executeOnlyOnce:false so it
        // keeps firing for every relaunch.
        if (Program.ShowWindowSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.ShowWindowSignal,
                (_, _) => Dispatcher.Invoke(() =>
                {
                    // A silent install relaunch stays headless: don't surface the window for it.
                    string? pending = ProtocolService.TryReadPending();
                    bool silent = pending is not null && ProtocolService.Parse(pending).Silent;
                    if (!silent)
                        window.RestoreFromTray();
                    if (pending is not null)
                        HandleProtocolUrl(pending);
                }),
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        // A --tray-locked relaunch (the loader) signals this → enable close-to-tray for the session even if
        // this instance was started without the flag. Idempotent; keeps firing for every relaunch.
        if (Program.EnableTrayLockSignal is not null)
            System.Threading.ThreadPool.RegisterWaitForSingleObject(
                Program.EnableTrayLockSignal,
                (_, _) => Program.SessionTrayLock = true,
                null, System.Threading.Timeout.Infinite, executeOnlyOnce: false);

        var toast = _host.Services.GetRequiredService<ToastService>();
        toast.Attach(window.RootSnackbar); // wire the presenter before anything can raise a toast

        // Language changed → persistent toast offering an immediate relaunch.
        settingsVm.RequestRestartPrompt = () => Dispatcher.Invoke(() =>
            toast.ShowAction(
                LuaToolsGui.Resources.Strings.Lang_Changed_Title,
                LuaToolsGui.Resources.Strings.Lang_Changed_Body,
                LuaToolsGui.Resources.Strings.Lang_Changed_Restart,
                () => settingsVm.RequestRestart?.Invoke()));

        // App updates are checked while the main UI is available. The user chooses Update Now or Later;
        // no package downloads before consent, and no browser/GitHub page is opened.
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        var fixes = _host.Services.GetRequiredService<FixesViewModel>();

        Func<Task> navigateToSignIn = () => Dispatcher.InvokeAsync(() =>
        {
            settingsVm.LoginRequiredMessage = LuaToolsGui.Resources.Strings.Settings_LoginRequired;
            window.NavigateToSettings();
        }).Task;
        download.RequestSignIn = navigateToSignIn;
        fixes.RequestSignIn = navigateToSignIn;

        var manage = _host.Services.GetRequiredService<ManageViewModel>();

        // Manage page "Update" → go to the Add page pre-seeded with that appid.
        manage.NavigateToAdd = appId =>
            Dispatcher.Invoke(() => { window.NavigateToAdd(); download.SeedSearch(appId); });

        // Manage flyout "Manage Build" → go to the Builds page with that game selected.
        var builds = _host.Services.GetRequiredService<BuildsViewModel>();
        manage.NavigateToBuilds = appId =>
            Dispatcher.Invoke(() => { window.NavigateToBuilds(); _ = builds.SelectAppAsync(appId); });

        // Manage flyout "Launch options…" → modal editor over Steam's appinfo cache.
        manage.OpenLaunchOptions = (appId, name) => Dispatcher.Invoke(() =>
        {
            var dialog = new LaunchOptionsDialog(
                _host.Services.GetRequiredService<LaunchOptionsViewModel>(), appId, name)
            { Owner = window };
            dialog.ShowDialog();
        });

        // Steam regenerates appinfo.vdf from PICS, wiping launch edits. Check once at startup and
        // OFFER to re-apply, never silently, since applying closes Steam.
        _ = CheckLaunchOptionDriftAsync();

        // Home "recently added" + Add install banner "Reveal" → go to Manage and open that game's detail.
        Action<long> openInManage = appId =>
            Dispatcher.Invoke(() => { window.NavigateToManage(); _ = manage.OpenDetailForAppIdAsync(appId); });
        var home = _host.Services.GetRequiredService<HomeViewModel>();
        home.NavigateToGame = openInManage;

        // Deliberately NO queue-wide completion toast here. Every entry point already reports its own
        // outcome: Fixes toasts from ManifestJobFactory, the Add page shows its InstallStatus banner, and
        // a silent install pops a tray balloon. A global toast on top of those double-notifies the user.

        // The Downloads tab's "Review" button on an item waiting for an overwrite confirmation: the
        // overlay lives on the Add page, so send the user there.
        _host.Services.GetRequiredService<DownloadsViewModel>().RevealItem = _ => window.NavigateToAdd();

        download.NavigateToGame = openInManage;
        builds.NavigateToManage = openInManage; // Builds "Manage" button: the reverse of "Manage Build"
        // Depot download queues one item covering the whole selection; show the user where it went.
        builds.RequestShowDownloads = () => Dispatcher.Invoke(window.NavigateToDownloads);

        // Dragging a SteamDB / Steam store link onto either drop box installs that appid. Routed through
        // HandleProtocolUrl rather than calling ProtocolInstall directly, so a dropped link and
        // luatools://install/<id> are literally the same path and can't drift apart later.
        // DropInstallViewModel is transient, so Home and Add each hold their own instance.
        Func<long, Task> installByAppId = appId =>
        {
            Dispatcher.Invoke(() => HandleProtocolUrl($"luatools://install/{appId}"));
            return Task.CompletedTask;
        };
        home.Drop.InstallByAppId = installByAppId;
        download.Drop.InstallByAppId = installByAppId;

        // Home dashboard cells → section navigation.
        home.NavigateToManage = () => Dispatcher.Invoke(window.NavigateToManage);
        home.NavigateToSettings = () => Dispatcher.Invoke(window.NavigateToSettings);
        home.NavigateToMode = () => Dispatcher.Invoke(window.NavigateToMode);

        // Onboarding finished applying its actions → refresh the Home dashboard.
        main.Onboarding.RefreshHome = () => Dispatcher.Invoke(() => home.LoadAsync());

        // Any game added (drag-drop, Add page, Fixes) → refresh the library views live.
        var luaInstaller = _host.Services.GetRequiredService<LuaInstaller>();
        var appInfo = _host.Services.GetRequiredService<SteamAppInfoCache>();
        luaInstaller.Installed += appId => Dispatcher.InvokeAsync(async () =>
        {
            _ = manage.LoadAsync();            // re-scan so Manage updates too if it's the visible page
            _ = builds.LoadAsync();            // a newly installed lua is a new variant in the vault
            await home.RefreshLibraryAsync();  // game appears (its cover may lag for newer titles)

            // Newer titles have no guessable header URL: the classic CDN path 404s and the real header is
            // a content-hashed store_item_assets URL that only comes from appdetails. Warm that game's
            // details at interactive priority (retries past throttling), then refresh again so its cover
            // fills in instead of staying blank until an app restart.
            if (await appInfo.EnsureFullDetailsAsync(appId))
                await home.RefreshLibraryAsync();
        });

        // Handle a protocol URL from the command line (first launch) or from a temp file left by a
        // second instance that exited before the signal listener was wired up.
        string? url = Program.StartupUrl ?? ProtocolService.TryReadPending();

        // A silent install launch (luatools://install/silent/<id>) runs headless: stay in the tray and
        // never surface the window. The window's Loaded handler won't fire when we skip Show().
        bool silentStartup = (url is not null && ProtocolService.Parse(url).Silent) || Program.StartMinimized;

        // Auto-exit after a silent install only when this was a COLD launch for it (StartupUrl came on the
        // command line, not from an already-running second instance) AND the user doesn't keep a tray app
        // around. Otherwise the app was already living somewhere and must stay.
        _exitAfterSilentInstall = silentStartup
            && Program.StartupUrl is not null
            && !settingsVm.MinimizeToTray;

        if (silentStartup)
        {
            window.StartSilent();
        }
        else
        {
            window.Show();

            // First-run onboarding: show the welcome overlay on a fresh install. Skip it (and mark done)
            // when the user already has a managed mode selected. Marking done here is permanent, so
            // switching mode later never re-triggers onboarding (only ModeMigration ever clears it again).
            var cache = _host.Services.GetRequiredService<CacheService>();
            if (!cache.OnboardingComplete)
            {
                var unlocker = _host.Services.GetRequiredService<UnlockerService>();
                // Custom deliberately doesn't count: a first-run user can't meaningfully choose "I'll
                // manage it myself" before they've been shown what the options are.
                bool configured = unlocker.SelectedMode is (UnlockerMode.Ost or UnlockerMode.Bst);
                if (configured) cache.OnboardingComplete = true;
                else main.Onboarding.IsOpen = true;
            }
        }

        if (url is not null)
            HandleProtocolUrl(url);

        // Every visible launch checks for application updates. Silent protocol/loader launches skip
        // the dialog so background installs remain non-interactive. Log skips for diagnosis.
        if (!silentStartup)
            _ = CheckAndPromptForUpdateAsync(window, main);
        else
            UpdateService.WriteDiagnostic($"Update check skipped for silent startup. ProtocolUrlPresent={url is not null}; StartMinimized={Program.StartMinimized}.");

        // Background, non-blocking key donation (runs only when the setting is on; silent + deduped).
        _ = _host.Services.GetRequiredService<DonateKeysService>().SendPendingKeysIfEnabledAsync();

        // Anonymous app-launch ping (Umami). Fire-and-forget; never blocks.
        _ = _host.Services.GetRequiredService<AnalyticsService>().TrackAppLaunchAsync();

        // Warm the hardware-appid blacklist (refreshes from GitHub if the cache is stale). Fire-and-forget.
        _ = _host.Services.GetRequiredService<HardwareAppIdService>().EnsureFreshAsync();

        // Rescue manifests this app used to write into config\depotcache, which Steam never reads. Silent,
        // idempotent, and costs nothing once the folder is gone. See DepotCacheMigrationService.
        _ = _host.Services.GetRequiredService<DepotCacheMigrationService>().RunAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_hostStarted)
            await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }

    /// <summary>Relaunch the app (used after a language change). The single-instance mutex is released
    /// only when THIS process exits, so we start the new instance via a short delayed shell command. By
    /// the time it launches the exe, our mutex is free and the new instance won't bow out.</summary>
    private void RelaunchApp()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                // cmd: wait ~1.2s for this process's mutex to release, then start the exe detached.
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { /* if relaunch fails, the user can reopen manually */ }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>Route a luatools:// protocol URL to the appropriate page and action.</summary>
    private void HandleProtocolUrl(string url)
    {
        var (action, appId, silent) = ProtocolService.Parse(url);
        if (action is null || appId is null) return;

        var window = _host.Services.GetRequiredService<MainWindow>();
        var download = _host.Services.GetRequiredService<DownloadViewModel>();
        var manage = _host.Services.GetRequiredService<ManageViewModel>();
        var fixes = _host.Services.GetRequiredService<FixesViewModel>();

        switch (action)
        {
            case "game":
                window.NavigateToAdd();
                download.SeedSearch(appId.Value);
                break;
            case "install":
                if (silent)
                {
                    // Headless: don't navigate or surface; install in the background, then a tray balloon.
                    _ = download.ProtocolInstall(appId.Value,
                        (msg, error) => Dispatcher.Invoke(() =>
                        {
                            window.ShowInstallNotification(msg, error);
                            // Cold launch + no tray app wanted → exit once the balloon has had time to show.
                            // ProtocolInstall already awaited this item's completion, but other queued
                            // downloads may remain; exiting now would cancel them mid-flight.
                            var queue = _host.Services.GetRequiredService<Services.Downloads.DownloadQueue>();
                            if (_exitAfterSilentInstall && queue.ActiveCount == 0)
                                _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
                        }));
                }
                else
                {
                    window.NavigateToAdd();
                    _ = download.ProtocolInstall(appId.Value);
                }
                break;
            case "manage":
                window.NavigateToManage();
                _ = manage.OpenDetailForAppIdAsync(appId.Value);
                break;
            case "fix":
                window.NavigateToFixes();
                _ = fixes.OpenForAppIdAsync(appId.Value);
                break;
        }
    }
}
