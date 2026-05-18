namespace SMSManagement.Infrastructure.Configuration;

public sealed class CorsAppOptions
{
    /// <summary>Comma-separated list of origins permitted to call the API
    /// from a browser. Empty (default) disables CORS for safety.</summary>
    public string AllowedOrigins { get; init; } = string.Empty;

    public string[] Parse() =>
        string.IsNullOrWhiteSpace(AllowedOrigins)
            ? Array.Empty<string>()
            : AllowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
