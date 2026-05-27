using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// HTTP client for the upstream AuthenAPI. Per spec:
///   POST {BaseUrl}/api/ldap/authenticate
///   Headers: X-API-Key: &lt;ApiKey&gt;
///   Body:    { "username": "...", "password": "..." }
///   Reply:   { "success": bool, "message": "...",
///              "data": { samAccountName, displayName, email, department,
///                        title, employeeId, groups[], isEnabled } }
///
/// Transport-level errors (timeout, DNS, 5xx) throw <see cref="AuthenApiException"/>
/// so the authenticator can trigger offline fallback. 401/403/4xx are translated
/// into a non-success <see cref="AuthenApiResult"/> (= wrong password).
/// </summary>
public sealed class AuthenApiClient : IAuthenApiClient
{
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<AuthenApiOptions> _opts;
    private readonly ILogger<AuthenApiClient> _log;

    public AuthenApiClient(HttpClient http, IOptionsMonitor<AuthenApiOptions> opts, ILogger<AuthenApiClient> log)
    {
        _http = http;
        _opts = opts;
        _log = log;
    }

    public async Task<AuthenApiResult> AuthenticateAsync(
        string username, string password, CancellationToken ct)
    {
        var opts = _opts.CurrentValue;
        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
            throw new AuthenApiException("AuthenAPI BaseUrl not configured.");

        var url = BuildUrl(opts.BaseUrl, opts.AuthenticatePath);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { username, password })
        };
        if (!string.IsNullOrEmpty(opts.ApiKey))
            req.Headers.Add("X-API-Key", opts.ApiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine user-initiated cancellation — let it propagate.
            throw;
        }
        catch (Exception ex)
        {
            // Includes Polly TimeoutRejectedException and the OperationCanceledException
            // Polly raises internally when an attempt times out. Logged at Error so the
            // ErrorLogs admin viewer captures the URL alongside the Polly OnTimeout
            // event — without the URL the operator can't tell whether it's a misconfig
            // or a real outage.
            _log.LogError(ex, "AuthenAPI transport failure calling {Url}.", url);
            throw new AuthenApiException($"AuthenAPI unreachable ({url}): {ex.Message}", ex);
        }

        using (resp)
        {
            if ((int)resp.StatusCode >= 500)
            {
                _log.LogWarning("AuthenAPI {Url} returned {Status}.", url, (int)resp.StatusCode);
                throw new AuthenApiException($"AuthenAPI returned {(int)resp.StatusCode}.");
            }

            // Even non-2xx responses may carry a structured envelope; try to parse it.
            ApiEnvelope? envelope = null;
            try
            {
                envelope = await resp.Content.ReadFromJsonAsync<ApiEnvelope>(cancellationToken: ct);
            }
            catch
            {
                // Non-JSON 4xx — treat as a generic rejection.
            }

            if (envelope is null)
            {
                // 404 here typically means BaseUrl + AuthenticatePath don't line up.
                // Surface the URL in logs so the operator can spot misconfig fast.
                _log.LogWarning(
                    "AuthenAPI {Url} returned HTTP {Status} with no envelope — check BaseUrl/AuthenticatePath.",
                    url, (int)resp.StatusCode);
                return Fail(username, $"HTTP_{(int)resp.StatusCode}");
            }

            if (!envelope.Success || envelope.Data is null)
                return Fail(username, envelope.Message ?? "rejected");

            var d = envelope.Data;
            return new AuthenApiResult(
                Success: true,
                Username: d.SamAccountName ?? username,
                DisplayName: d.DisplayName,
                Email: d.Email,
                Department: d.Department,
                Title: d.Title,
                EmployeeId: d.EmployeeId,
                Groups: d.Groups ?? Array.Empty<string>(),
                IsEnabled: d.IsEnabled,
                ErrorMessage: null);
        }
    }

    private static AuthenApiResult Fail(string username, string reason) =>
        new(false, username, null, null, null, null, null, Array.Empty<string>(), false, reason);

    /// <summary>
    /// Combines BaseUrl + AuthenticatePath, tolerating an overlapping leading segment
    /// (a common misconfig). All of these produce the same URL:
    ///
    ///   BaseUrl=https://host           Path=/api/ldap/authenticate
    ///   BaseUrl=https://host/api       Path=/api/ldap/authenticate   ← user's config
    ///   BaseUrl=https://host/api       Path=/ldap/authenticate
    ///   BaseUrl=https://host/api/      Path=ldap/authenticate
    ///
    /// → https://host/api/ldap/authenticate
    /// </summary>
    public static string BuildUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = string.IsNullOrEmpty(path) ? string.Empty
              : path.StartsWith('/') ? path : "/" + path;

        if (!Uri.TryCreate(b, UriKind.Absolute, out var uri)) return b + p;

        var basePath = uri.AbsolutePath.TrimEnd('/');
        // If AuthenticatePath repeats the BaseUrl's path prefix, drop the duplicate.
        if (basePath.Length > 0
            && p.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            p = p.Substring(basePath.Length);
        }
        return b + p;
    }

    // ---- response shape ----
    private sealed class ApiEnvelope
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")]    public ApiData? Data { get; set; }
    }

    private sealed class ApiData
    {
        [JsonPropertyName("samAccountName")] public string? SamAccountName { get; set; }
        [JsonPropertyName("displayName")]    public string? DisplayName { get; set; }
        [JsonPropertyName("email")]          public string? Email { get; set; }
        [JsonPropertyName("department")]     public string? Department { get; set; }
        [JsonPropertyName("title")]          public string? Title { get; set; }
        [JsonPropertyName("employeeId")]     public string? EmployeeId { get; set; }
        [JsonPropertyName("groups")]         public string[]? Groups { get; set; }
        [JsonPropertyName("isEnabled")]      public bool IsEnabled { get; set; } = true;
    }
}
