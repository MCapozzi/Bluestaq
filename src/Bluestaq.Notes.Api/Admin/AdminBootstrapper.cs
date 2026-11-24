using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Admin;

public sealed class AdminBootstrapOptions
{
    public const string SectionName = "Admin";

    public string? BootstrapUsername { get; set; }
    public string? BootstrapPassword { get; set; }
}

/// <summary>
/// Ensures the configured bootstrap account exists and is an administrator. Runs once at startup. An existing
/// non-administrator account is only promoted if the configured password matches it, so whoever self-registered the
/// username can't gain administrator access.
/// </summary>
public static class AdminBootstrapper
{
    public static async Task<User?> EnsureAsync(
        NotesDbContext db, IPasswordHasher<User> hasher, AdminBootstrapOptions options, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapUsername))
        {
            return null;
        }

        var username = options.BootstrapUsername.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username, ct);
        if (user is null)
        {
            if (string.IsNullOrEmpty(options.BootstrapPassword) || options.BootstrapPassword.Length < Contracts.ValidationLimits.PasswordMinLength)
            {
                throw new InvalidOperationException(
                    $"Admin:BootstrapPassword (at least {Contracts.ValidationLimits.PasswordMinLength} characters) is required to create '{username}'.");
            }

            user = new User { Username = username, PasswordHash = "", CreatedAt = clock.GetUtcNow().UtcDateTime };
            user.PasswordHash = hasher.HashPassword(user, options.BootstrapPassword);
            db.Users.Add(user);
        }

        else if (user.Role != UserRole.Administrator
            && (string.IsNullOrEmpty(options.BootstrapPassword)
                || hasher.VerifyHashedPassword(user, user.PasswordHash, options.BootstrapPassword) == PasswordVerificationResult.Failed))
        {
            throw new InvalidOperationException(
                $"'{username}' already exists as a regular user and Admin:BootstrapPassword doesn't match it; refusing to promote it.");
        }

        user.Role = UserRole.Administrator;
        await db.SaveChangesAsync(ct);
        return user;
    }
}
