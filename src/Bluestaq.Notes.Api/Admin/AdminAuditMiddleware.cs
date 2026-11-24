using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Bluestaq.Notes.Api.Admin;

/// <summary>Audits every request made by an administrator, including failed ones, after the response is produced.</summary>
public sealed class AdminAuditMiddleware(RequestDelegate next, TimeProvider clock)
{
    /// <summary>
    /// Set by endpoints where the caller becomes known during the request (e.g. login), as (userId, username).
    /// </summary>
    public const string ActorItemKey = "AdminAudit.Actor";

    public async Task InvokeAsync(HttpContext context, IAdminAuditLog auditLog)
    {
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (ResolveAdmin(context) is var (adminId, adminName))
            {
                await auditLog.RecordAsync(BuildEntry(context, adminId, adminName, failed), CancellationToken.None);
            }
        }
    }

    private static (long Id, string Username)? ResolveAdmin(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.User.IsAdministrator())
        {
            return (context.User.GetUserId(), context.User.Identity.Name ?? "");
        }

        return context.Items.TryGetValue(ActorItemKey, out var actor) && actor is (long id, string name) ? (id, name) : null;
    }

    private AdminAuditEntry BuildEntry(HttpContext context, long adminId, string adminName, bool failed)
    {
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        var routeId = TryGetLong(context.GetRouteValue("id"));
        var isUserRoute = action?.ControllerName == "Admin" && context.Request.Path.StartsWithSegments("/api/admin/users");

        return new AdminAuditEntry
        {
            OccurredAt = clock.GetUtcNow().UtcDateTime,
            AdminUserId = adminId,
            AdminUsername = adminName,
            Action = action is null ? "(none)" : $"{action.ControllerName}.{action.ActionName}",
            HttpMethod = context.Request.Method,
            Path = Truncate(context.Request.Path.Value ?? "/", 500)!,
            QueryString = Truncate(context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null, 1000),
            TargetNoteId = isUserRoute ? null : routeId,
            TargetUserId = isUserRoute ? routeId : TryGetLong(context.Request.Query["userId"].FirstOrDefault()),
            StatusCode = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode,
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
            TraceId = context.TraceIdentifier,
        };
    }

    private static long? TryGetLong(object? value) => long.TryParse(value?.ToString(), out var n) ? n : null;

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } && value.Length > max ? value[..max] : value;
}
