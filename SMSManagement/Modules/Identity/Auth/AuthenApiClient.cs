using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Identity.Auth;

/// <summary>
/// HTTP client for the upstream AuthenAPI. Transport errors throw
/// <see cref="AuthenApiException"/> so the authenticator can trigger fallback;
/// 401/403 are translated into a structured non-success result.
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

        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{_opts.BaseUrl.TrimEnd('/')}/api/authenticate")
        {
            Content = JsonContent.Create(new { username, password })
        };
        if (!string.IsNullOrEmpty(_opts.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "AuthenAPI transport failure for {Username}",
                Core.Security.PiiMasking.ScrubText(username));
            throw new AuthenApiException("AuthenAPI unreachable.", ex);
        }

        using (resp)
        {
            if ((int)resp.StatusCode >= 500)
                throw new AuthenApiException($"AuthenAPI returned {(int)resp.StatusCode}.");

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Fail(username, "invalid credentials");
            }

            if (!resp.IsSuccessStatusCode)
                return Fail(username, $"HTTP_{(int)resp.StatusCode}");

            var body = await resp.Content.ReadFromJsonAsync<ApiBody>(cancellationToken: ct)
                       ?? throw new AuthenApiException("AuthenAPI returned empty body.");

            return new AuthenApiResult(
                Success: body.Authenticated,
                Username: body.Username ?? username,
                DisplayName: body.DisplayName,
                Email: body.Email,
                Department: body.Department,
                Title: body.Title,
                EmployeeId: body.EmployeeId,
                Groups: body.Groups ?? Array.Empty<string>(),
                IsEnabled: body.IsEnabled,
                ErrorMessage: body.Authenticated ? null : body.Error ?? "rejected");
        }
    }

    private static AuthenApiResult Fail(string username, string reason) =>
        new(false, username, null, null, null, null, null, Array.Empty<string>(), false, reason);

    private sealed class ApiBody
    {
        [JsonPropertyName("authenticated")] public bool Authenticated { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("department")] public string? Department { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("employeeId")] public string? EmployeeId { get; set; }
        [JsonPropertyName("groups")] public string[]? Groups { get; set; }
        [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; } = true;
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
