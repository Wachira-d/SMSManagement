using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SMSManagement.Modules.Notifications.Relay;

/// <summary>The relay's response envelope, reduced to what we read.</summary>
public sealed record RelayResponse(
    HttpStatusCode StatusCode,
    bool Success,
    string? Message,
    string? Error,
    JsonElement? Data,
    TimeSpan? RetryAfter,
    string RawBody)
{
    public bool IsSuccessStatus => (int)StatusCode is >= 200 and < 300;

    /// <summary>"message — error" when the relay gave both, for LastError.</summary>
    public string Describe()
    {
        var parts = new[] { Message, Error }.Where(s => !string.IsNullOrWhiteSpace(s));
        var text = string.Join(" — ", parts);
        return $"HTTP {(int)StatusCode}" + (text.Length > 0 ? $": {text}" : string.Empty);
    }
}

/// <summary>
/// Thin typed HTTP client for EmailRelayAPI's client endpoints. Reads
/// <see cref="EmailRelayOptions"/> on every call so the admin page can repoint
/// it live. Never throws for an HTTP error status — it returns the response —
/// but network failures and timeouts do propagate so callers can tell "the
/// relay said no" from "we never reached the relay".
/// </summary>
public sealed class EmailRelayClient
{
    /// <summary>Named IHttpClientFactory client carrying the resilience pipeline.</summary>
    public const string HttpClientName = "email-relay";
    public const string SendPath = "/api/v1/emails";
    public const string MePath = "/api/v1/me";

    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<EmailRelayOptions> _opts;
    private readonly TimeProvider _clock;

    /// <summary>Singleton-safe: an HttpClient is created per call from the
    /// factory (cheap — handlers are pooled and rotated), rather than one
    /// typed client captured for the app's lifetime, which would never pick
    /// up a DNS change for the relay host.</summary>
    public EmailRelayClient(IHttpClientFactory http, IOptionsMonitor<EmailRelayOptions> opts, TimeProvider clock)
    {
        _http = http;
        _opts = opts;
        _clock = clock;
    }

    /// <summary>True when there is enough configuration to make a call.</summary>
    public bool IsConfigured(out string? problem)
    {
        var o = _opts.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.BaseUrl))
            problem = "Email:Relay:BaseUrl is not set.";
        else if (!Uri.TryCreate(o.BaseUrl.Trim(), UriKind.Absolute, out var u)
                 || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            problem = $"Email:Relay:BaseUrl '{o.BaseUrl}' is not an absolute http(s) URL.";
        else if (string.IsNullOrWhiteSpace(o.ApiKey))
            problem = "Email:Relay:ApiKey is not set.";
        else
            problem = null;
        return problem is null;
    }

    public Task<RelayResponse> SendAsync(object payload, CancellationToken ct)
        => SendCoreAsync(HttpMethod.Post, SendPath, JsonSerializer.SerializeToUtf8Bytes(payload, WireJson), ct);

    public Task<RelayResponse> GetMessageAsync(string relayMessageId, CancellationToken ct)
        => SendCoreAsync(HttpMethod.Get, $"{SendPath}/{Uri.EscapeDataString(relayMessageId)}", null, ct);

    public Task<RelayResponse> GetMeAsync(CancellationToken ct)
        => SendCoreAsync(HttpMethod.Get, MePath, null, ct);

    private async Task<RelayResponse> SendCoreAsync(
        HttpMethod method, string path, byte[]? body, CancellationToken ct)
    {
        var o = _opts.CurrentValue;
        // A BaseUrl with a sub-path (an IIS virtual directory) must keep it,
        // so join onto the trailing-slashed root instead of letting Uri
        // treat the last segment as a file name and drop it.
        var root = new Uri(o.BaseUrl.Trim().TrimEnd('/') + "/");
        var uri = new Uri(root, path.TrimStart('/'));

        using var req = new HttpRequestMessage(method, uri);
        req.Headers.TryAddWithoutValidation("X-API-Key", o.ApiKey);
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
            req.Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new("application/json") { CharSet = "utf-8" } }
            };

        if (!string.IsNullOrEmpty(o.SigningSecret))
        {
            // Signed over the exact bytes we send, and over the API path the
            // relay sees — not uri.AbsolutePath, which would include a
            // virtual-directory prefix the relay strips into PathBase.
            var ts = _clock.GetUtcNow().ToUnixTimeSeconds();
            req.Headers.TryAddWithoutValidation("X-Timestamp", ts.ToString());
            req.Headers.TryAddWithoutValidation("X-Signature",
                RelaySignature.SignRequest(o.SigningSecret, ts, method.Method, path, body ?? Array.Empty<byte>()));
        }

        // Email:Relay:TimeoutSeconds caps the whole call, retries included,
        // and is read live. Expiry surfaces as an OperationCanceledException
        // while the caller's own token is still uncancelled — callers treat
        // that combination as a failure, not a shutdown.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 5, 300)));

        var http = _http.CreateClient(HttpClientName);
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return Parse(resp, raw);
    }

    private static RelayResponse Parse(HttpResponseMessage resp, string raw)
    {
        var retryAfter = resp.Headers.RetryAfter?.Delta
            ?? (resp.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);

        bool success = false;
        string? message = null, error = null;
        JsonElement? data = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
                message = Str(root, "message");
                error = Str(root, "error");
                if (root.TryGetProperty("data", out var dEl) && dEl.ValueKind != JsonValueKind.Null)
                    data = dEl.Clone();
            }
        }
        catch (JsonException)
        {
            // Not the relay's envelope — an IIS error page, a proxy, etc.
            message = raw.Length > 300 ? raw[..300] : raw;
        }
        return new RelayResponse(resp.StatusCode, success, message, error, data, retryAfter, raw);
    }

    internal static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
