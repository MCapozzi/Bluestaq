using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;

namespace Bluestaq.Notes.Api.Tests;

internal static class ApiClient
{
    public const string DefaultPassword = "correct-horse-battery";

    public static string UniqueUsername() => $"user_{Guid.NewGuid():N}"[..30];

    /// <summary>Registers and logs in a fresh user, returning a client authorized with its bearer token.</summary>
    public static async Task<(HttpClient Client, SessionResponse Session)> CreateAuthenticatedClientAsync(NotesApiFactory factory)
    {
        var client = factory.CreateClient();
        var username = UniqueUsername();
        (await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, DefaultPassword))).EnsureSuccessStatusCode();

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, DefaultPassword));
        login.EnsureSuccessStatusCode();
        var session = (await login.Content.ReadFromJsonAsync<SessionResponse>())!;

        var authed = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return (authed, session);
    }
}
