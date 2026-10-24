using System.IO;
using Velopack;
using Velopack.Sources;

namespace LuaToolsGui.Services;

/// <summary>
/// User-consented app updates via Velopack + the public AzaelX release repository.
/// Diagnostics are written outside the install folder so they survive Velopack updates.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager[] _managers = AppConfig.GithubReleasesRepos
        .Select(repo => new UpdateManager(
            new GithubSource(repo, accessToken: null, prerelease: false,
                downloader: new ProxiedFileDownloader())))
        .ToArray();

    private UpdateManager? _availableManager;
    private UpdateInfo? _availableInfo;
    private UpdateManager? _stagedManager;
    private UpdateInfo? _stagedInfo;

    public static string DiagnosticLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppConfig.AppDataDirectoryName, "logs", "update.log");

    /// <summary>Write best-effort updater diagnostics; logging must never break startup.</summary>
    public static void WriteDiagnostic(string message)
    {
        try
        {
            string path = DiagnosticLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic logging is best effort only.
        }
    }

    /// <summary>True only for an installed Velopack build; developer/unpacked runs skip update checks.</summary>
    public bool IsInstalled => _managers.Length > 0 && _managers[0].IsInstalled;

    /// <summary>True after an update has downloaded successfully and is ready to apply.</summary>
    public bool HasStagedUpdate => _stagedManager is not null && _stagedInfo is not null;

    /// <summary>
    /// Checks the configured public release source without downloading a package. All skip conditions,
    /// successful checks, and exceptions are logged to %AppData%\Azaelx-Gui\logs\update.log so an
    /// updater failure is no longer silently indistinguishable from "no update available".
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync()
    {
        _availableManager = null;
        _availableInfo = null;

        bool installed;
        try
        {
            installed = IsInstalled;
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Could not determine Velopack install state: {ex}");
            return null;
        }

        WriteDiagnostic($"Check started. IsInstalled={installed}; sources={string.Join(",", AppConfig.GithubReleasesRepos)}");
        if (!installed)
        {
            // Do not silently skip the check solely because the installation marker looks unusual.
            // CheckForUpdatesAsync will throw NotInstalledException for a true unpackaged run; logging
            // that exception is more diagnosable and lets valid installed setups still check updates.
            WriteDiagnostic("Warning: Velopack IsInstalled is false; attempting the check anyway so any locator failure is recorded.");
        }

        for (int i = 0; i < _managers.Length; i++)
        {
            var manager = _managers[i];
            string repo = i < AppConfig.GithubReleasesRepos.Length ? AppConfig.GithubReleasesRepos[i] : "(unknown source)";
            string currentVersion;
            try { currentVersion = manager.CurrentVersion?.ToString() ?? "(unknown)"; }
            catch { currentVersion = "(unavailable)"; }

            try
            {
                WriteDiagnostic($"Checking repository {repo}; installedVersion={currentVersion}.");
                var info = await manager.CheckForUpdatesAsync();
                if (info is null)
                {
                    WriteDiagnostic($"Repository {repo} returned no update; installedVersion={currentVersion}.");
                    return null;
                }

                _availableManager = manager;
                _availableInfo = info;
                WriteDiagnostic($"Update found from {repo}: targetVersion={info.TargetFullRelease.Version}.");
                return info;
            }
            catch (Exception ex)
            {
                WriteDiagnostic($"Update check failed for {repo}: {ex}");
            }
        }

        WriteDiagnostic("Update check ended without a result: every configured repository failed.");
        return null;
    }

    /// <summary>Downloads only after explicit user consent, reporting progress in the range 0..100.</summary>
    public async Task DownloadAndStageAsync(Action<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_availableManager is null || _availableInfo is null)
            throw new InvalidOperationException("CheckForUpdateAsync must find an update before downloading.");

        try
        {
            WriteDiagnostic($"Download started for {_availableInfo.TargetFullRelease.Version}.");
            await _availableManager.DownloadUpdatesAsync(_availableInfo, progress, cancellationToken);
            _stagedManager = _availableManager;
            _stagedInfo = _availableInfo;
            WriteDiagnostic($"Download/staging completed for {_stagedInfo.TargetFullRelease.Version}.");
        }
        catch (Exception ex)
        {
            WriteDiagnostic($"Download/staging failed: {ex}");
            throw;
        }
    }

    /// <summary>Apply the downloaded update and restart this application into the new version.</summary>
    public void ApplyStagedAndRestart(string[]? restartArgs = null)
    {
        if (_stagedManager is null || _stagedInfo is null)
            throw new InvalidOperationException("There is no downloaded update ready to apply.");

        WriteDiagnostic($"Applying {_stagedInfo.TargetFullRelease.Version} and restarting.");
        _stagedManager.ApplyUpdatesAndRestart(_stagedInfo, restartArgs);
    }
}
