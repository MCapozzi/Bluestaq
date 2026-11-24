using Bluestaq.Notes.Api.Domain;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;

namespace Bluestaq.Notes.Api.Admin;

public sealed class AdminAuditOptions
{
    public const string SectionName = "AdminAudit";

    /// <summary>Rolling file path; a date is inserted before the extension (admin-audit-20260101.log).</summary>
    public string LogFilePath { get; set; } = Path.Combine("logs", "admin-audit-.log");

    public int RetainedFileCount { get; set; } = 90;
}

/// <summary>Writes admin audit entries as JSON lines to a dedicated daily-rolling file, separate from app logs.</summary>
public sealed class AdminAuditFileLog : IDisposable
{
    private readonly Logger _logger;

    public AdminAuditFileLog(IOptions<AdminAuditOptions> options)
    {
        // Serilog swallows sink failures; surface them so a broken audit file isn't silent.
        Serilog.Debugging.SelfLog.Enable(Console.Error);
        _logger = new LoggerConfiguration()
            .WriteTo.File(
                new CompactJsonFormatter(),
                options.Value.LogFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: options.Value.RetainedFileCount,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();
    }

    public void Write(AdminAuditEntry e) =>
        _logger
            .ForContext("OccurredAt", e.OccurredAt)
            .ForContext("TargetNoteId", e.TargetNoteId)
            .ForContext("TargetUserId", e.TargetUserId)
            .ForContext("ClientIp", e.ClientIp)
            .ForContext("TraceId", e.TraceId)
            .Information(
                "Admin {AdminUsername} ({AdminUserId}) {Action} {HttpMethod} {Path}{QueryString} -> {StatusCode}",
                e.AdminUsername, e.AdminUserId, e.Action, e.HttpMethod, e.Path, e.QueryString, e.StatusCode);

    public void Dispose() => _logger.Dispose();
}
