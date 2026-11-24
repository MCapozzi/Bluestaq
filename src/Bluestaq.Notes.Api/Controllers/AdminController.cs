using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Controllers;

/// <summary>Administrator-only endpoints. Admins also have full access through the regular /api/notes endpoints.</summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = Roles.AdministratorPolicy)]
public sealed class AdminController(NotesDbContext db) : ControllerBase
{
    [HttpGet("users")]
    [ProducesResponseType<PagedResponse<UserResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListUsers(
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, ValidationLimits.MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var total = await db.Users.CountAsync(ct);
        var items = await db.Users.AsNoTracking()
            .OrderBy(u => u.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(u => new UserResponse(u.Id, u.Username, u.CreatedAt, u.Role))
            .ToListAsync(ct);
        return Ok(new PagedResponse<UserResponse>(items, page, pageSize, total));
    }

    [HttpPut("users/{id:long}/role")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetRole(long id, SetRoleRequest request, CancellationToken ct)
    {
        if (id == User.GetUserId())
        {
            // Prevents an admin from accidentally locking the last admin out.
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "You can't change your own role.");
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        user.Role = request.Role!.Value;
        await db.SaveChangesAsync(ct);
        return Ok(new UserResponse(user.Id, user.Username, user.CreatedAt, user.Role));
    }

    /// <summary>All notes in the system, optionally filtered by owner, newest first.</summary>
    [HttpGet("notes")]
    [ProducesResponseType<PagedResponse<NoteResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListNotes(
        [FromQuery] long? userId = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, ValidationLimits.MaxPageSize)] int pageSize = 20,
        CancellationToken ct = default)
    {
        var query = db.Notes.AsNoTracking();
        if (userId is { } ownerId)
        {
            query = query.Where(n => n.OwnerId == ownerId);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(n => n.UpdatedAt).ThenByDescending(n => n.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(n => new NoteResponse(
                n.Id, n.OwnerId, n.Title, n.Content, n.CreatedByUserId, n.CreatedAt, n.UpdatedByUserId, n.UpdatedAt, n.Visibility))
            .ToListAsync(ct);
        return Ok(new PagedResponse<NoteResponse>(items, page, pageSize, total));
    }

    /// <summary>The administrator action log, newest first.</summary>
    [HttpGet("audit-log")]
    [ProducesResponseType<PagedResponse<AdminAuditEntryResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> AuditLog(
        [FromQuery] long? adminUserId = null,
        [FromQuery] long? noteId = null,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, ValidationLimits.MaxPageSize)] int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = db.AdminAuditLog.AsNoTracking();
        if (adminUserId is { } a)
        {
            query = query.Where(e => e.AdminUserId == a);
        }

        if (noteId is { } n)
        {
            query = query.Where(e => e.TargetNoteId == n);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new AdminAuditEntryResponse(
                e.Id, e.OccurredAt, e.AdminUserId, e.AdminUsername, e.Action, e.HttpMethod, e.Path, e.QueryString,
                e.TargetNoteId, e.TargetUserId, e.StatusCode, e.ClientIp, e.TraceId))
            .ToListAsync(ct);
        return Ok(new PagedResponse<AdminAuditEntryResponse>(items, page, pageSize, total));
    }
}
