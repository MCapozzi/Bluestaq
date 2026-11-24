namespace Bluestaq.Notes.Api.RateLimiting;

/// <summary>Per-client-IP fixed window limit applied to anonymous credential endpoints (login/register).</summary>
public sealed class AuthRateLimitOptions
{
    public const string SectionName = "RateLimiting:Auth";
    public const string PolicyName = "auth";

    public int PermitLimit { get; set; } = 10;
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
