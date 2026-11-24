using System.Security.Claims;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Auth;

public static class Roles
{
    public const string Administrator = nameof(UserRole.Administrator);
    public const string AdministratorPolicy = "Administrator";

    public static bool IsAdministrator(this ClaimsPrincipal user) => user.IsInRole(Administrator);
}
