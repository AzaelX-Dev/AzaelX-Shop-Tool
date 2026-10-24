using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using Velopack;

namespace LuaToolsGui;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // MUST run before any WPF/UI work: handles Velopack install/update hooks,
        // then no-ops on a normal launch.
        VelopackApp.Build().Run();

        if (!MigrateLegacyAppData()) return;

        // Set the UI culture before any WPF element is created, so x:Static resource lookups (which
        // resolve once at parse time) pick up the right language from the first frame.
        ApplyUiCulture();

        // Register the luatools:// protocol handler so browser links can open the app.
        Services.ProtocolService.Register();

        // Check if we were launched with a luatools:// protocol URL.
        string? protocolUrl = null;
        bool startMinimized = false;
        bool trayLocked = false;
        if (args is { Length: > 0 })
        {
            foreach (var arg in args)
            {
                if (arg.StartsWith("luatools://", StringComparison.OrdinalIgnoreCase))
                {
                    protocolUrl = arg;
                }
                else if (arg.Equals("--minimized", StringComparison.OrdinalIgnoreCase))
                {
                    startMinimized = true;
                }
                else if (arg.Equals("--tray-locked", StringComparison.OrdinalIgnoreCase))
                {
                    trayLocked = true;
                }
            }
        }

        // Only one instance may run: copies sharing %AppData%\Azaelx-Gui could lock each other's files
        // (e.g. a token write failing on login).
        using var single = new Mutex(initiallyOwned: true, "AzaelX-Tools.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // Launched from a browser protocol link → write the URL to a temp file so the live
            // instance can pick it up, then signal it to surface and handle the URL.
            if (protocolUrl is not null)
                Services.ProtocolService.WritePending(protocolUrl);

            // Already running (possibly hidden in the tray). Poke the live instance to surface its
            // window, then bow out. This is what users expect when they relaunch the app.
            // Exception: a --minimized relaunch explicitly wants to stay silent (this is what our
            // Steam DLL hijack passes on every launch: steam.exe loads it into more than one
            // process/thread per boot, so DllMain and this whole Main() can legitimately run more
            // than once in the same Steam startup). Don't surface the window for that duplicate.
            if (!startMinimized)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var signal))
                    {
                        signal.Set();
                        signal.Dispose();
                    }
                }
                catch { /* best effort. If signalling fails, just exit quietly */ }
            }

            // A --tray-locked relaunch (the loader passes this) asks the live instance to switch close-to-tray
            // on for the session. Otherwise, if the app was already open WITHOUT the flag when the loader
            // ran, it would stay killable by a window close (this second process exits without the live one
            // ever learning about the flag).
            if (trayLocked)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(EnableTrayLockEventName, out var tl))
                    {
                        tl.Set();
                        tl.Dispose();
                    }
                }
                catch { /* best effort */ }
            }
            return;
        }

        // First instance: store the URL so App.OnStartup can route it.
        StartupUrl = protocolUrl;
        StartMinimized = startMinimized;
        SessionTrayLock = trayLocked;

        // The live instance listens on this named event; a second launch sets it (above) to ask us to
        // restore/focus the main window. Created before App.Run so it exists before any relaunch races us.
        ShowWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        EnableTrayLockSignal = new EventWaitHandle(false, EventResetMode.AutoReset, EnableTrayLockEventName);

        var app = new App();
        app.InitializeComponent();
        app.Run();

        ShowWindowSignal.Dispose();
        EnableTrayLockSignal.Dispose();
    }

    /// <summary>Named event a second instance sets to ask the running one to surface its window.</summary>
    private const string ShowWindowEventName = "AzaelX-Tools.ShowWindow";

    /// <summary>Owned by the live instance; App wires a listener that restores the window when it's set.</summary>
    internal static EventWaitHandle? ShowWindowSignal;

    /// <summary>Named event a second (--tray-locked) instance sets to ask the running one to enable
    /// close-to-tray for the session, so the loader's protection applies even to an already-open app.</summary>
    private const string EnableTrayLockEventName = "AzaelX-Tools.EnableTrayLock";

    /// <summary>Owned by the live instance; App wires a listener that sets SessionTrayLock when it's set.</summary>
    internal static EventWaitHandle? EnableTrayLockSignal;

    /// <summary>Protocol URL passed via command line on first launch; consumed by App.OnStartup.</summary>
    internal static string? StartupUrl;

    /// <summary>True when the app was launched with --minimized (from the loader DLL).</summary>
    internal static bool StartMinimized;

    /// <summary>True when launched with --tray-locked (the loader DLL passes this): the window's close
    /// button minimizes to the tray for THIS session regardless of the saved MinimizeToTray setting, so the
    /// backend can't be killed by an accidental window close. Only the tray "Exit" item fully quits. This
    /// is in-memory only and never written to settings, so a normal launch keeps the user's real setting.</summary>
    internal static bool SessionTrayLock;

    // The only supported UI languages. Keep this in sync with SettingsViewModel.LanguageOptions
    // and the available localization resources (Strings.resx + Strings.th.resx).
    private static readonly string[] SupportedLanguages = ["en", "th"];

    /// <summary>Resolve the UI language (saved setting → OS display language → English) and apply it to
    /// the thread + WPF before any UI is built.</summary>
    private static void ApplyUiCulture()
    {
        try
        {
            string tag = ResolveLanguageTag();
            var culture = new CultureInfo(tag);

            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;

            // Make WPF date/number formatting (and any xml:lang-aware rendering) follow the UI culture.
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
        catch
        {
            // Never let a culture problem stop the app from launching. Fall through to default (English).
        }
    }

    /// <summary>Saved language from settings.json if set + supported; else the OS display language if
    /// supported; else "en".</summary>
    private static string ResolveLanguageTag()
    {
        string? saved = ReadSavedLanguage();
        if (saved is not null && IsSupported(saved))
            return saved.Equals("th", StringComparison.OrdinalIgnoreCase) ? "th" : "en";

        return MatchOsLanguage(CultureInfo.InstalledUICulture);
    }

    /// <summary>Map the Windows display culture to one of the two shipped UI languages.
    /// Thai locales use Thai; every other locale uses English. Internal-visible for tests.</summary>
    internal static string MatchOsLanguage(CultureInfo os)
    {
        return os.TwoLetterISOLanguageName.Equals("th", StringComparison.OrdinalIgnoreCase)
            ? "th"
            : "en";
    }

    private static bool IsSupported(string tag) =>
        SupportedLanguages.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Read just the "Language" key from %AppData%\Azaelx-Gui\settings.json without spinning up
    /// the DI container (this runs before App is constructed).</summary>
    private static string? ReadSavedLanguage()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppConfig.AppDataDirectoryName, "settings.json");
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Language", out var el) && el.ValueKind == JsonValueKind.String)
            {
                string? v = el.GetString();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { /* corrupt/missing → fall back to OS */ }
        return null;
    }

    private static bool MigrateLegacyAppData()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string newRoot = Path.Combine(appData, AppConfig.AppDataDirectoryName);
        string migrationMarker = Path.Combine(newRoot, ".legacy-data-migrated");
        if (File.Exists(migrationMarker)) return true;

        string[] legacyRoots = AppConfig.LegacyAppDataDirectoryNames
            .Select(name => Path.Combine(appData, name))
            .Where(Directory.Exists)
            .ToArray();
        if (legacyRoots.Length == 0) return true;

        try
        {
            foreach (string legacyRoot in legacyRoots)
            {
                foreach (string source in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(legacyRoot, source);
                    string destination = Path.Combine(newRoot, relativePath);
                    if (File.Exists(destination)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(source, destination);
                }
            }
            Directory.CreateDirectory(newRoot);
            File.WriteAllText(migrationMarker, DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not copy existing settings and data into the new {AppConfig.ProductName} folder.\n\n{ex.Message}",
                AppConfig.ProductName,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }
}
