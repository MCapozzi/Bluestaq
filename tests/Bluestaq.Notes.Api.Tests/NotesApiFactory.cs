using System.Collections.Concurrent;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Digest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MySql;

namespace Bluestaq.Notes.Api.Tests;

/// <summary>Hosts the API in-memory against a throwaway MySQL container.</summary>
public sealed class NotesApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const int RateLimitPermits = 100_000;
    public const string AdminUsername = "rootadmin";
    public const string AdminPassword = "admin-test-password";

    public string AdminAuditDirectory { get; } = Path.Combine(Path.GetTempPath(), $"bluestaq-admin-audit-{Guid.NewGuid():N}");

    public CapturingEmailSender Emails { get; } = new();

    private readonly MySqlContainer _mysql = new MySqlBuilder(Environment.GetEnvironmentVariable("NOTES_TEST_MYSQL_IMAGE") ?? "mysql:8.4").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Notes", _mysql.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", RateLimitPermits.ToString());
        builder.UseSetting("Admin:BootstrapUsername", AdminUsername);
        builder.UseSetting("Admin:BootstrapPassword", AdminPassword);
        builder.UseSetting("Alerts:PollInterval", "00:00:00.200");
        builder.UseSetting("AdminAudit:LogFilePath", Path.Combine(AdminAuditDirectory, "admin-audit-.log"));
        builder.UseSetting("Digest:PollInterval", "00:00:00.200");
        builder.ConfigureTestServices(services => services.AddSingleton<IEmailSender>(Emails));
    }

    public NotesDbContext CreateDbContext() =>
        Services.CreateScope().ServiceProvider.GetRequiredService<NotesDbContext>();

    public async Task InitializeAsync() => await _mysql.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _mysql.DisposeAsync();
    }
}

/// <summary>Records digest emails instead of sending them.</summary>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public bool IsConfigured => true;

    public IReadOnlyCollection<EmailMessage> Sent => _sent;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public async Task<EmailMessage> WaitForAsync(Func<EmailMessage, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_sent.FirstOrDefault(match) is { } message)
            {
                return message;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Expected email was not sent.");
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<NotesApiFactory>
{
    public const string Name = "api";
}
