using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SMSManagement.Infrastructure.Configuration;

/// <summary>
/// Configuration source backed by the <c>SystemSettings</c> table. It is
/// layered ON TOP of appsettings.json / environment variables, so a value an
/// admin saves in <c>/Admin/Settings</c> overrides the file default for the
/// whole process — <c>IOptions&lt;T&gt;</c> everywhere then reflects it.
///
/// Saving settings triggers <see cref="Microsoft.Extensions.Configuration.IConfigurationRoot.Reload"/>
/// so the change is picked up live by <c>IOptionsSnapshot</c> / <c>IOptionsMonitor</c>
/// consumers without a restart.
/// </summary>
public sealed class SystemSettingsConfigurationSource : IConfigurationSource
{
    private readonly string _connectionString;

    public SystemSettingsConfigurationSource(string connectionString)
        => _connectionString = connectionString;

    public IConfigurationProvider Build(IConfigurationBuilder builder)
        => new SystemSettingsConfigurationProvider(_connectionString);
}

public sealed class SystemSettingsConfigurationProvider : ConfigurationProvider
{
    private readonly string _connectionString;

    public SystemSettingsConfigurationProvider(string connectionString)
        => _connectionString = connectionString;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Short connect timeout — a slow/unreachable DB must not stall
            // startup; the file-based defaults still apply if this fails.
            var csb = new SqlConnectionStringBuilder(_connectionString) { ConnectTimeout = 5 };
            using var conn = new SqlConnection(csb.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT [Key], [ValueJson] FROM [SystemSettings]";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (string.IsNullOrWhiteSpace(key)) continue;
                var json = reader.IsDBNull(1) ? "null" : reader.GetString(1);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    Flatten(key, doc.RootElement, data);
                }
                catch (JsonException)
                {
                    // Stored value isn't valid JSON — use the raw text as-is.
                    data[key] = json;
                }
            }
        }
        catch
        {
            // Table missing (first run, pre-migration), DB unreachable, or a
            // non-SqlServer test database — fall back to file configuration.
        }
        Data = data;
    }

    /// <summary>Expands a JSON value into flat <c>Section:Key</c> entries so a
    /// stored object/array still binds to its options class.</summary>
    private static void Flatten(string prefix, JsonElement el, IDictionary<string, string?> data)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                    Flatten($"{prefix}:{p.Name}", p.Value, data);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in el.EnumerateArray())
                    Flatten($"{prefix}:{i++}", item, data);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                // Leave unset so the file/default value keeps winning.
                break;
            case JsonValueKind.String:
                data[prefix] = el.GetString();
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                data[prefix] = el.GetBoolean() ? "true" : "false";
                break;
            default: // Number
                data[prefix] = el.GetRawText();
                break;
        }
    }
}

public static class SystemSettingsConfigurationExtensions
{
    public static IConfigurationBuilder AddSystemSettings(
        this IConfigurationBuilder builder, string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            builder.Add(new SystemSettingsConfigurationSource(connectionString));
        return builder;
    }
}
