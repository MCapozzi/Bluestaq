using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Digest;

/// <summary>Periodically sends due note digests. Does nothing when no mail server is configured.</summary>
public sealed class DigestDispatcher(
    IServiceScopeFactory scopes,
    IEmailSender sender,
    IOptions<DigestOptions> options,
    TimeProvider clock,
    ILogger<DigestDispatcher> logger) : BackgroundService
{
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
                await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Note digest pass failed.");
                await Task.Delay(options.Value.PollInterval, clock, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
            }
        }
    }

    public async Task<int> SendOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DigestService>().SendDueAsync(ct);
    }
}
