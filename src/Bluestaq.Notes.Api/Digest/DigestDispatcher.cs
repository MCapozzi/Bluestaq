using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Digest;

/// <summary>Polls for the nightly digests to send. Does nothing when no mail server is configured.</summary>
public sealed class DigestDispatcher(
    IServiceScopeFactory scopes,
    IEmailSender sender,
    IOptions<DigestOptions> options,
    TimeProvider clock,
    ILogger<DigestDispatcher> logger) : BackgroundService
{
    private readonly DigestSchedule _schedule = new(TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!sender.IsConfigured)
        {
            logger.LogInformation("Note digest emails are disabled: {Setting} is not set.", "Digest:Smtp:Host");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendOnceAsync(stoppingToken);
                await Task.Delay(NextDelay(), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Note digest pass failed.");
                await Task.Delay(NextDelay(), clock, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    /// <summary>The poll interval, cut short at midnight so the send window is never polled past.</summary>
    public TimeSpan NextDelay()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var untilMidnight = _schedule.NextMidnight(now) - now;
        return untilMidnight < options.Value.PollInterval ? untilMidnight : options.Value.PollInterval;
    }

    public async Task<int> SendOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DigestService>().SendDueAsync(ct);
    }
}
