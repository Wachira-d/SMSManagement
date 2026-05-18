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
    private readonly AuthenApiOptions _opts;
    private readonly ILogger<AuthenApiClient> _log;

    public AuthenApiClient(HttpClient http, IOptions<AuthenApiOptions> opts, ILogger<AuthenApiClient> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<AuthenApiResult> AuthenticateAsync(
        string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.BaseUrl))
            throw new AuthenApiException("AuthenAPI BaseUrl not configured.");

        var url = $"{_opts.BaseUrl.TrimEnd('/')}{_opts.AuthenticatePath}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new { username, password })
        };
        if (!string.IsNullOrEmpty(_opts.ApiKey))
            req.Headers.Add("X-API-Key", _opts.ApiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "AuthenAPI transport failure.");
            throw new AuthenApiException("AuthenAPI unreachable.", ex);
        }

        using (resp)
        {
            if ((int)resp.StatusCode >= 500)
                throw new AuthenApiException($"AuthenAPI returned {(int)resp.StatusCode}.");

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
                return Fail(username, $"HTTP_{(int)resp.StatusCode}");

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
