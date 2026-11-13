using Serilog;
using Serilog.Events;

namespace SMSManagement.Modules.Core.Logging;

/// <summary>
/// Plain-text, daily-rolling log files — the thing to open when the site
/// shows "HTTP Error 500.30" and there is no UI to look at.
///
///   logs\startup-YYYYMMDD.log  crashes before the app finished starting
///   logs\app-YYYYMMDD.log      everything after that
///
/// Configured by the <c>Logging:File</c> section (Enabled, Directory,
/// RetainedDays). Directory defaults to <c>logs</c> next to the app; a
/// relative path is taken from the app folder. If the folder can't be
/// created or written (IIS app-pool identity lacks rights) file logging is
/// skipped with a console warning — it must never be the reason the app
/// fails to start.
/// </summary>
public static class FileLogging
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}";
    private const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>Logger used until the host builds its real one. Reads no
    /// configuration — config loading is one of the things that can fail.</summary>
    public static Serilog.Core.Logger CreateBootstrapLogger()
    {
        var lc = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.Console();
        var dir = TryEnsure(Path.Combine(AppContext.BaseDirectory, "logs"));
        if (dir is not null)
            lc.WriteTo.File(Path.Combine(dir, "startup-.log"), outputTemplate: Template,
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30, shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1));
        return lc.CreateLogger();
    }

    /// <summary>Adds the app-log file sink to the host's logger.</summary>
    public static LoggerConfiguration AddFileSink(this LoggerConfiguration lc, IConfiguration cfg, string contentRoot)
    {
        var section = cfg.GetSection("Logging:File");
        if (!section.GetValue("Enabled", true)) return lc;

        var configured = section["Directory"];
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(contentRoot, "logs")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
        dir = TryEnsure(dir);
        if (dir is null) return lc;

        var days = Math.Clamp(section.GetValue("RetainedDays", 30), 1, 365);
        return lc.WriteTo.File(Path.Combine(dir, "app-.log"), outputTemplate: Template,
            rollingInterval: RollingInterval.Day, retainedFileCountLimit: days,
            fileSizeLimitBytes: MaxFileBytes, rollOnFileSizeLimit: true,
            // IIS overlapped recycle runs two processes on the same file.
            shared: true, flushToDiskInterval: TimeSpan.FromSeconds(1));
    }

    /// <summary>Creates the folder and proves it's writable; null if not.</summary>
    private static string? TryEnsure(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return dir;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"File logging disabled: cannot write to '{dir}': {ex.Message}");
            return null;
        }
    }
}
