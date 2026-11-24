using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Contracts;

public sealed record SetRoleRequest([Required, EnumDataType(typeof(UserRole))] UserRole? Role);

public sealed record AdminAuditEntryResponse(
    long Id,
    DateTime OccurredAt,
    long AdminUserId,
    string AdminUsername,
    string Action,
    string HttpMethod,
    string Path,
    string? QueryString,
    long? TargetNoteId,
    long? TargetUserId,
    int StatusCode,
    string? ClientIp,
    string? TraceId);
