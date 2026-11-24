using System.Net;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;
using Microsoft.AspNetCore.Hosting;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class RateLimitTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Login_is_throttled_after_permit_limit()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Auth:PermitLimit", "3"));
        var client = limited.CreateClient();
        var request = new LoginRequest(ApiClient.UniqueUsername(), "wrong-password");

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/auth/login", request)).StatusCode);
    }
}
