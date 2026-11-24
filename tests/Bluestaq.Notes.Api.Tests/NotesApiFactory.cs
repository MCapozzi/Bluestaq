using Bluestaq.Notes.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MySql;

namespace Bluestaq.Notes.Api.Tests;

/// <summary>Hosts the API in-memory against a throwaway MySQL container.</summary>
public sealed class NotesApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const int RateLimitPermits = 100_000;

    private readonly MySqlContainer _mysql = new MySqlBuilder(Environment.GetEnvironmentVariable("NOTES_TEST_MYSQL_IMAGE") ?? "mysql:8.4").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Notes", _mysql.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", RateLimitPermits.ToString());
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

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<NotesApiFactory>
{
    public const string Name = "api";
}
