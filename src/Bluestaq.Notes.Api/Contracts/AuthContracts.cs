using System.ComponentModel.DataAnnotations;
using static Bluestaq.Notes.Api.Contracts.ValidationLimits;

namespace Bluestaq.Notes.Api.Contracts;

public sealed record RegisterRequest(
    [Required, StringLength(UsernameMaxLength, MinimumLength = UsernameMinLength), RegularExpression(UsernamePattern)]
    string Username,
    [Required, StringLength(PasswordMaxLength, MinimumLength = PasswordMinLength)]
    string Password);

public sealed record LoginRequest(
    [Required] string Username,
    [Required] string Password);

public sealed record UserResponse(long Id, string Username, DateTime CreatedAt);

public sealed record SessionResponse(string Token, DateTime ExpiresAt, UserResponse User);
