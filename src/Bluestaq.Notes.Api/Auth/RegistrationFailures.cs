using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Auth;

public sealed class RegistrationOptions
{
    public const string SectionName = "Registration";
    public const string SupportEmail = "Support@BluestaqNotes.com";

    /// <summary>Failures in a row after which the user is told to email support instead of retrying.</summary>
    public int SupportThreshold { get; set; } = 3;

    /// <summary>How long a client's failure count is kept without another failure.</summary>
    public TimeSpan FailureMemory { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Counts registration failures in a row per client, on this instance.</summary>
public sealed class RegistrationFailures(IMemoryCache cache, IOptions<RegistrationOptions> options)
{
    private readonly Lock _lock = new();

    /// <returns>The client's failures in a row, including this one.</returns>
    public int Record(string client)
    {
        lock (_lock)
        {
            var failures = cache.Get<int>(Key(client)) + 1;
            cache.Set(Key(client), failures, new MemoryCacheEntryOptions { SlidingExpiration = options.Value.FailureMemory });
            return failures;
        }
    }

    public bool NeedsSupport(int failures) => failures >= options.Value.SupportThreshold;

    public void Reset(string client) => cache.Remove(Key(client));

    private static string Key(string client) => $"registration-failures:{client}";
}
