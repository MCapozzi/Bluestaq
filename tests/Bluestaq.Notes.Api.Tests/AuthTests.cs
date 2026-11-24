using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Register_then_login_returns_token_and_session_cookie()
    {
        var client = factory.CreateClient();
        var username = ApiClient.UniqueUsername();

        var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, ApiClient.DefaultPassword));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username.ToUpperInvariant(), ApiClient.DefaultPassword));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var session = await login.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.False(string.IsNullOrEmpty(session!.Token));
        Assert.Equal(username, session.User.Username);

        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("notes_session=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        // The default test client stores cookies, so the session cookie alone authorizes this request.
        var me = await client.GetFromJsonAsync<UserResponse>("/api/auth/me");
        Assert.Equal(username, me!.Username);
    }

    [Fact]
    public async Task Session_token_is_stored_hashed()
    {
        var (_, session) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        await using var db = factory.CreateDbContext();

        Assert.False(await db.Sessions.AnyAsync(s => s.TokenHash == session.Token));
        Assert.True(await db.Sessions.AnyAsync(s => s.TokenHash == SessionService.HashToken(session.Token)));
    }

    [Fact]
    public async Task Duplicate_username_is_rejected_case_insensitively()
    {
        var client = factory.CreateClient();
        var username = ApiClient.UniqueUsername();
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, ApiClient.DefaultPassword));

        var again = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username.ToUpperInvariant(), ApiClient.DefaultPassword));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("ab", "long-enough-password")]
    [InlineData("has space", "long-enough-password")]
    [InlineData("validname", "short")]
    public async Task Register_validates_input(string username, string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, password));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Login_with_bad_credentials_returns_401(bool userExists)
    {
        var client = factory.CreateClient();
        var username = ApiClient.UniqueUsername();
        if (userExists)
        {
            await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(username, ApiClient.DefaultPassword));
        }

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/notes")]
    [InlineData("/api/notes/1")]
    [InlineData("/api/auth/me")]
    public async Task Protected_endpoints_require_a_session(string url)
    {
        var response = await factory.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");

        var response = await client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Expired_session_is_rejected()
    {
        var (client, session) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        await using (var db = factory.CreateDbContext())
        {
            var hash = SessionService.HashToken(session.Token);
            await db.Sessions.Where(s => s.TokenHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        var response = await client.GetAsync("/api/notes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_ends_the_callers_session()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(ApiClient.UniqueUsername(), ApiClient.DefaultPassword));

        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var (client, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/notes")).StatusCode);
    }
}
