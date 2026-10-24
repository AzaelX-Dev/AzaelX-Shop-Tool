using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using LuaToolsGui.Resources;

namespace LuaToolsGui.Services;

/// <summary>Checks the current Windows installation ID against the maintainer's Pastebin allowlist.
/// This is a lightweight distribution gate, not tamper-proof licensing or a server-side security boundary.</summary>
public sealed class DeviceAuthorizationService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public string HardwareId { get; }
    public string? HardwareIdError { get; }

    public DeviceAuthorizationService()
    {
        try
        {
            HardwareId = CreateHardwareId();
        }
        catch
        {
            HardwareId = string.Empty;
            HardwareIdError = Strings.Auth_HwidError;
        }
    }

    /// <summary>Fail-closed check: network errors, an empty Pastebin, and an unmatched ID do not authorize.</summary>
    public async Task<DeviceAuthorizationResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(HardwareId))
            return new(false, HardwareIdError ?? Strings.Auth_HwidError);

        try
        {
            using var response = await _http.GetAsync(AppConfig.DeviceAuthorizationRawUrl, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
                return new(false, Strings.Auth_EmptyList);

            string expected = NormalizeId(HardwareId);
            bool allowed = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeAllowlistLine)
                .Any(candidate => candidate.Length > 0 && string.Equals(candidate, expected, StringComparison.Ordinal));

            return allowed
                ? new(true, Strings.Auth_Approved)
                : new(false, Strings.Auth_Pending);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, Strings.Auth_ConnectionError);
        }
        catch
        {
            return new(false, Strings.Auth_ConnectionError);
        }
    }

    private static string CreateHardwareId()
    {
        // MachineGuid is local to this Windows installation. Only a one-way hash leaves this method;
        // the raw registry value is never displayed, transmitted, or written to a file.
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        string? machineGuid = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrWhiteSpace(machineGuid))
            throw new InvalidOperationException("Windows MachineGuid is unavailable.");

        byte[] bytes = Encoding.UTF8.GetBytes("AzaelX-Tools-HWID-v1:" + machineGuid.Trim());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string NormalizeAllowlistLine(string line)
    {
        string value = line.Trim().TrimStart('\uFEFF');
        if (value.Length == 0 || value.StartsWith('#') || value.StartsWith("//", StringComparison.Ordinal))
            return string.Empty;

        if (value.StartsWith("HWID:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("HWID=", StringComparison.OrdinalIgnoreCase))
            value = value[5..].Trim();

        return NormalizeId(value);
    }

    private static string NormalizeId(string value) =>
        new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public void Dispose() => _http.Dispose();
}

public sealed record DeviceAuthorizationResult(bool IsAuthorized, string Message);
