using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using LuaToolsGui.Models;
using LuaToolsGui.Services.Downloads;

namespace LuaToolsGui.Services;

public class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public record DownloadedFile(string FilePath, string FileName);

/// <summary>Typed client for public Steam/LuaTools endpoints and LuaTools-account-protected operations.</summary>
public class LuaToolsApiClient(SteamAppInfoCache appInfo, CoverCache covers, AuthService auth)
{
    private readonly HttpClient _http = AppHttp.Create(
        TimeSpan.FromMinutes(5), // large manifest zips on slow connections
        new Uri(AppConfig.ApiBaseUrl));

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // ── Endpoints ───────────────────────────────────────────────────

    public async Task<List<SteamSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Calls Steam's public store-search directly.
        var url = $"{AppConfig.SteamStoreSearchUrl}?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        var res = await _http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return [];

        var data = await ReadJsonAsync<SteamStoreSearchResponse>(res, ct);
        return (data?.Items ?? [])
            .Take(8)
            .Select(i => new SteamSearchResult
            {
                AppId = i.Id,
                Name = i.Name,
                Icon = i.TinyImage ?? $"https://cdn.cloudflare.steamstatic.com/steam/apps/{i.Id}/capsule_sm_120.jpg",
            })
            .ToList();
    }

    private const int TopSellerCandidateLimit = 50;

    private static readonly Regex TopSellerRowRegex = new(
        """<a\b(?=[^>]*\bclass\s*=\s*["'][^"']*\bsearch_result_row\b[^"']*["'])[^>]*>(.*?)</a>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TopSellerAppIdRegex = new(
        """\bdata-ds-appid\s*=\s*["'](?<id>\d+)["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TopSellerNameRegex = new(
        """<span\b[^>]*class\s*=\s*["'][^"']*\btitle\b[^"']*["'][^>]*>(?<name>.*?)</span>""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Steam's featured "top sellers" + "new releases" lists for the Add page.
    /// The featured endpoint can return a short Top Sellers list in some regions or sessions. In that
    /// case, supplement it with Steam's public global-top-sellers search results so the Add page can show
    /// up to 12 valid games after filtering hardware app IDs. This is best-effort and never blocks installs.</summary>
    public async Task<(List<SteamFeaturedItem> TopSellers, List<SteamFeaturedItem> NewReleases)> GetFeaturedAsync(
        CancellationToken ct = default)
    {
        List<SteamFeaturedItem> top = [];
        List<SteamFeaturedItem> fresh = [];
        try
        {
            using var res = await _http.GetAsync($"{AppConfig.SteamFeaturedUrl}?cc=us&l=english", ct);
            if (res.IsSuccessStatusCode)
            {
                var data = await ReadJsonAsync<SteamFeaturedResponse>(res, ct);

                // Steam's featuredcategories can repeat appids; keep only real games and preserve order.
                static List<SteamFeaturedItem> Clean(SteamFeaturedCategory? c, int limit) =>
                    (c?.Items ?? [])
                        .Where(i => i.Type == 0 && i.Id > 0 && !string.IsNullOrWhiteSpace(i.LargeCapsuleImage))
                        .DistinctBy(i => i.Id)
                        .Take(limit)
                        .ToList();

                top = Clean(data?.TopSellers, TopSellerCandidateLimit);
                fresh = Clean(data?.NewReleases, 20);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* Keep the Add page usable when Steam's featured endpoint is unavailable. */ }

        // The home-page featured bucket is often only 4–10 entries. Fill from Steam's own global
        // Top Sellers search, then deduplicate so no game is shown twice.
        if (top.Count < TopSellerCandidateLimit)
        {
            var extra = await GetGlobalTopSellerFallbackAsync(ct);
            var seen = top.Select(i => i.Id).ToHashSet();
            foreach (var item in extra)
            {
                if (!seen.Add(item.Id)) continue;
                top.Add(item);
                if (top.Count >= TopSellerCandidateLimit) break;
            }
        }

        return (top.Take(TopSellerCandidateLimit).ToList(), fresh.Take(20).ToList());
    }

    private async Task<List<SteamFeaturedItem>> GetGlobalTopSellerFallbackAsync(CancellationToken ct)
    {
        try
        {
            const string url = "https://store.steampowered.com/search/results/?query=&start=0&count=50&dynamic_data=&sort_by=_ASC&snr=1_7_7_7000_7&filter=globaltopsellers&category1=998&infinite=1&cc=US&l=english";
            using var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return [];
            var payload = await ReadJsonAsync<SteamSearchResultsHtml>(res, ct);
            if (string.IsNullOrWhiteSpace(payload?.ResultsHtml)) return [];

            var items = new List<SteamFeaturedItem>(TopSellerCandidateLimit);
            foreach (Match row in TopSellerRowRegex.Matches(payload.ResultsHtml))
            {
                var idMatch = TopSellerAppIdRegex.Match(row.Value);
                var nameMatch = TopSellerNameRegex.Match(row.Groups[1].Value);
                if (!idMatch.Success || !long.TryParse(idMatch.Groups["id"].Value, out long appId) || appId <= 0 || !nameMatch.Success)
                    continue;

                string name = WebUtility.HtmlDecode(Regex.Replace(nameMatch.Groups["name"].Value, "<[^>]+>", string.Empty)).Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // Use Steam's standard horizontal header artwork for rows that do not carry the
                // featuredcategories capsule URL. The featured list is still preferred for duplicates.
                items.Add(new SteamFeaturedItem
                {
                    Id = appId,
                    Name = name,
                    Type = 0,
                    LargeCapsuleImage = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg"
                });
                if (items.Count >= TopSellerCandidateLimit) break;
            }
            return items.DistinctBy(i => i.Id).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    /// <summary>Public endpoint.</summary>
    /// <summary>Game metadata straight from Steam's appdetails (cached to details\&lt;appid&gt;.json via the
    /// throttle, interactive priority), no lua.tools proxy. ANY fetch path funnels through here (normal /
    /// DLC / fast), so this is also where the header image gets warmed into covers\.</summary>
    public async Task<GameDetails?> GetDetailsAsync(string appid, CancellationToken ct = default)
    {
        if (!long.TryParse(appid, out long id)) return null;
        var details = await appInfo.ResolveGameDetailsAsync(id, ct);
        if (details is { HeaderImage: { Length: > 0 } img })
            _ = covers.EnsureAsync(id, img, CancellationToken.None); // warm the cover cache (best-effort)
        return details;
    }

    /// <summary>
    /// Checks which standard LuaTools manifest sources currently have a manifest for this app.
    /// This is the public manifest-backend check used by the original source chooser; downloads themselves
    /// still require the LuaTools account token through the protected API.
    /// </summary>
    public async Task<Dictionary<string, string>> CheckSourcesAsync(string appid, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{AppConfig.ManifestBackendUrl}/check_apis?appid={Uri.EscapeDataString(appid)}");
            req.Headers.TryAddWithoutValidation("User-Agent", AppConfig.ManifestBackendUserAgent);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return [];
            return await ReadJsonAsync<Dictionary<string, string>>(res, ct) ?? [];
        }
        catch
        {
            // The source checker is informational. If it is offline, the rest of the Add page remains usable.
            return [];
        }
    }

    /// <summary>Standard LuaTools daily usage. Null when signed out or the service is unavailable.</summary>
    public async Task<StandardUsage?> GetStandardUsageAsync(CancellationToken ct = default)
    {
        try
        {
            var todayUtc = DateTime.UtcNow.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            string url = $"{AppConfig.SupabaseUrl}/rest/v1/user_downloads" +
                         $"?select=appid&downloaded_at=gte.{Uri.EscapeDataString(todayUtc)}";
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("apikey", AppConfig.SupabaseAnonKey);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());
            req.Headers.TryAddWithoutValidation("Prefer", "count=exact");
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            string? range = res.Content.Headers.TryGetValues("Content-Range", out var values) ? values.FirstOrDefault()
                          : (res.Headers.TryGetValues("Content-Range", out var headerValues) ? headerValues.FirstOrDefault() : null);
            int used = 0;
            if (range is not null && range.Split('/') is [_, var countText] && int.TryParse(countText, out int count))
                used = count;
            return new StandardUsage(used, AppConfig.DailyDownloadLimit);
        }
        catch { return null; }
    }

    public async Task<SupporterStatus?> GetSupporterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await SendAsync(HttpMethod.Get, "/api/me/supporter-status", ct);
            return await ReadJsonAsync<SupporterStatus>(res, ct);
        }
        catch { return null; }
    }

    public Task<DownloadedFile> DownloadManifestAsync(
        string appid, string source, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/manifest/download?appid={Uri.EscapeDataString(appid)}&source={Uri.EscapeDataString(source)}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.zip", progress, ct);
    }

    public async Task<DlcInfo?> GetDlcInfoAsync(string appid, string baseAppId, CancellationToken ct = default)
    {
        var res = await SendAsync(HttpMethod.Get, $"/api/dlc/info?appid={appid}&base={baseAppId}", ct);
        return await ReadJsonAsync<DlcInfo>(res, ct);
    }

    /// <summary>
    /// One depot's raw <c>.manifest</c> by id, so a depot download no longer depends on Steam happening
    /// to have the file in its depotcache.
    /// </summary>
    /// <remarks>
    /// The response is raw bytes on success, or a JSON error otherwise, which
    /// <see cref="SendAsync"/> turns into an <see cref="ApiException"/>.
    ///
    /// <para>This one writes no history row and does NOT consume the daily download cap. It is instead
    /// limited to 120 requests per 10 minutes per user, and only cache misses count. A large game is
    /// ~20 depots, comfortably inside that — which is why manifests are fetched lazily per depot at
    /// download time rather than eagerly when the picker opens.</para>
    /// </remarks>
    public Task<DownloadedFile> DownloadDepotManifestAsync(
        long depotId, string manifestId,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
        => DownloadFileAsync($"/api/givemethemanifestpunk/{depotId}/{manifestId}",
                             $"{depotId}_{manifestId}.manifest", progress, ct);

    public Task<DownloadedFile> GenerateDlcAsync(
        string appid, string baseAppId, string? gameName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        string url = $"/api/dlc/generate?appid={appid}&base={baseAppId}";
        if (!string.IsNullOrEmpty(gameName)) url += $"&game_name={Uri.EscapeDataString(gameName)}";
        return DownloadFileAsync(url, $"{appid}.lua", progress, ct);
    }

    // ── Denuvo fixes ────────────────────────────────────────────────

    /// <summary>Public. Every game that has at least one Denuvo fix, plus the tag catalogue.</summary>
    public async Task<DenuvoListingsResponse?> GetDenuvoListingsAsync(CancellationToken ct = default)
    {
        var res = await _http.GetAsync("/api/denuvo/listings", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await ReadJsonAsync<DenuvoListingsResponse>(res, ct);
    }

    /// <summary>Public. One game's fixes (id/title/desc/tags + which download slots exist).</summary>
    public async Task<DenuvoFixesResponse?> GetDenuvoFixesAsync(string appid, CancellationToken ct = default)
    {
        var res = await _http.GetAsync($"/api/denuvo/fixes?appid={Uri.EscapeDataString(appid)}", ct);
        if (!res.IsSuccessStatusCode) return null; // 404 = no fixes for this appid
        return await ReadJsonAsync<DenuvoFixesResponse>(res, ct);
    }

    /// <summary>
    /// Download a fix's "manifest" or "fix" slot. The endpoint returns a short-lived signed R2 URL;
    /// access and usage limits remain enforced by the API.
    /// </summary>
    public async Task<DownloadedFile> DownloadDenuvoAsync(
        string fixId, string slot, string fallbackName,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        // 1. Ask the API for a signed URL. Permission and usage checks remain server-side.
        var res = await SendAsync(HttpMethod.Get,
            $"/api/denuvo/download?fix={Uri.EscapeDataString(fixId)}&slot={Uri.EscapeDataString(slot)}", ct);
        var signed = await ReadJsonAsync<DenuvoDownloadResponse>(res, ct);
        if (string.IsNullOrWhiteSpace(signed?.Url))
            throw new ApiException(Resources.Strings.Api_Err_EmptyDownloadLink);

        // 2. Fetch the file from R2; the signed URL carries the required credentials.
        return await DownloadFromUrlAsync(signed.Url, fallbackName, progress, ct);
    }

    // ── Plumbing ────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        // This client method is used for LuaTools account-protected endpoints. Each request gets a
        // fresh short-lived access token; signed file URLs use DownloadFromUrlAsync and never receive it.
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await auth.GetValidAccessTokenAsync());
        var res = await _http.SendAsync(req, completion, ct);
        if (res.IsSuccessStatusCode) return res;

        string message = res.StatusCode == HttpStatusCode.Unauthorized
            ? Resources.Strings.Api_Err_Unauthorized
            : string.Format(Resources.Strings.Api_Err_RequestFailed, (int)res.StatusCode);
        try
        {
            var err = JsonSerializer.Deserialize<ApiError>(await res.Content.ReadAsStringAsync(ct), JsonOpts);
            // Preserve a useful server-side reason, but replace a generic "Unauthorized" with the
            // actionable account-link guidance. Never include tokens or credential values in errors.
            if (!string.IsNullOrWhiteSpace(err?.Error))
            {
                bool genericUnauthorized = res.StatusCode == HttpStatusCode.Unauthorized
                    && err.Error.Equals("Unauthorized", StringComparison.OrdinalIgnoreCase);
                if (!genericUnauthorized) message = err.Error;
            }
        }
        catch { /* non-JSON error body */ }

        throw new ApiException(message, res.StatusCode);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct) =>
        JsonSerializer.Deserialize<T>(await res.Content.ReadAsStringAsync(ct), JsonOpts);

    private async Task<DownloadedFile> DownloadFileAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var res = await SendAsync(HttpMethod.Get, url, ct, HttpCompletionOption.ResponseHeadersRead);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }

    /// <summary>Download a file from an absolute URL (e.g. a signed R2 link).</summary>
    private async Task<DownloadedFile> DownloadFromUrlAsync(
        string url, string fallbackName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // New request (not via SendAsync) so the absolute URL is not prefixed.
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new ApiException(string.Format(Resources.Strings.Api_Err_DownloadFailed, (int)res.StatusCode), res.StatusCode);
        return await HttpFileDownloader.SaveResponseAsync(res, fallbackName, progress, ct);
    }
}
