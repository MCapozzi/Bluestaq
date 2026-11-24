using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using Bluestaq.Notes.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class RegistrationFailureTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Database_errors_alert_online_admins_and_point_to_support_after_repeated_failures()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(NotesApiFactory.AdminUsername, NotesApiFactory.AdminPassword));
        var admin = (await login.Content.ReadFromJsonAsync<SessionResponse>())!;
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = r => r.Headers.Authorization = $"Bearer {admin.Token}";
        using var socket = await ws.ConnectAsync(new Uri(factory.Server.BaseAddress, "ws/alerts"), CancellationToken.None);

        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER fail_registrations BEFORE INSERT ON users FOR EACH ROW
                BEGIN
                    IF NEW.Username LIKE 'regfail%' THEN
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'simulated storage failure';
                    END IF;
                END
                """);
        }

        try
        {
            var titles = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var response = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest($"regfail{i}", "a-strong-password"));
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                titles.Add((await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Title!);
            }

            Assert.All(titles[..2], t => Assert.Contains("Please try registering again", t));
            Assert.Contains("Support@BluestaqNotes.com", titles[2]);

            var alerts = new List<string>();
            while (alerts.Count < 3)
            {
                var text = await ReceiveAsync(socket, TimeSpan.FromSeconds(10));
                if (text.Contains("\"registrationFailed\""))
                {
                    alerts.Add(text);
                }
            }

            Assert.Contains("\"username\":\"regfail0\"", alerts[0]);
            Assert.Contains("simulated storage failure", alerts[2]);
        }
        finally
        {
            await using var db = factory.CreateDbContext();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_registrations");
        }

        var ok = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest("regfail-recovered", "a-strong-password"));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cts.Token);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
