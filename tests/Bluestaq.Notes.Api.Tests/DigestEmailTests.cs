using System.Net;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class DigestEmailTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Digest_is_off_by_default_and_requires_both_opt_ins_in_updates()
    {
        var (bob, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        var settings = (await bob.GetFromJsonAsync<DigestSettingsResponse>("/api/digest"))!;
        Assert.Equal((false, false, (DateTime?)null, "America/New_York", "TEST@TEST.TEST"),
            (settings.SharedNotes, settings.PublicNotes, settings.NextDigestAt, settings.TimeZone, settings.Recipient));

        var bad = await bob.PutAsJsonAsync("/api/digest", new { sharedNotes = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Opted_in_users_are_emailed_yesterdays_activity_and_others_are_not()
    {
        var (alice, aliceSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (_, carolSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (dave, daveSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var shared = await CreateNoteAsync(alice, "Digest me");
        foreach (var user in new[] { bobSession, carolSession })
        {
            (await alice.PostAsJsonAsync($"/api/notes/{shared.Id}/shares", new ShareNoteRequest(user.User.Username))).EnsureSuccessStatusCode();
        }

        var published = await CreateNoteAsync(alice, "Public yesterday");
        (await alice.PutAsJsonAsync($"/api/notes/{published.Id}/visibility", new SetVisibilityRequest(NoteVisibility.Public))).EnsureSuccessStatusCode();
        await BackdateOneDayAsync(shared.Id, published.Id);
        var today = await CreateNoteAsync(alice, "Public today");
        (await alice.PutAsJsonAsync($"/api/notes/{today.Id}/visibility", new SetVisibilityRequest(NoteVisibility.Public))).EnsureSuccessStatusCode();

        (await dave.PutAsJsonAsync("/api/digest", new UpdateDigestRequest(true, false))).EnsureSuccessStatusCode();
        var put = await bob.PutAsJsonAsync("/api/digest", new UpdateDigestRequest(true, true));
        var settings = (await put.Content.ReadFromJsonAsync<DigestSettingsResponse>())!;
        Assert.Equal((true, true), (settings.SharedNotes, settings.PublicNotes));

        var preview = (await bob.GetFromJsonAsync<NoteDigest>("/api/digest/preview"))!;
        Assert.Contains(preview.NewPublic, n => n.Title == "Public today");

        var email = await factory.Emails.WaitForAsync(m => m.Subject.Contains(bobSession.User.Username), TimeSpan.FromSeconds(15));
        Assert.Equal("TEST@TEST.TEST", email.To);
        Assert.Contains("\"Digest me\"", email.TextBody);
        Assert.Contains($"shared by {aliceSession.User.Username}", email.TextBody);
        Assert.Contains("\"Public yesterday\"", email.TextBody);
        Assert.DoesNotContain("Public today", email.TextBody);

        // Dave opted in but has nothing new: his digest is processed without sending an email.
        await WaitUntilProcessedAsync(daveSession.User.Id);
        Assert.DoesNotContain(factory.Emails.Sent, m => m.Subject.Contains(daveSession.User.Username));
        Assert.DoesNotContain(factory.Emails.Sent, m => m.Subject.Contains(carolSession.User.Username));
    }

    private static async Task<NoteResponse> CreateNoteAsync(HttpClient client, string title) =>
        (await (await client.PostAsJsonAsync("/api/notes", new NoteRequest(title, "body"))).Content.ReadFromJsonAsync<NoteResponse>())!;

    private async Task BackdateOneDayAsync(params long[] noteIds)
    {
        await using var db = factory.CreateDbContext();
        await db.NoteShares.Where(s => noteIds.Contains(s.NoteId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SharedAt, x => x.SharedAt.AddDays(-1)));
        await db.Notes.Where(n => noteIds.Contains(n.Id)).ExecuteUpdateAsync(s => s
            .SetProperty(n => n.CreatedAt, n => n.CreatedAt.AddDays(-1))
            .SetProperty(n => n.UpdatedAt, n => n.UpdatedAt.AddDays(-1))
            .SetProperty(n => n.ContentUpdatedAt, n => n.ContentUpdatedAt.AddDays(-1))
            .SetProperty(n => n.PublicSince, n => n.PublicSince!.Value.AddDays(-1)));
    }

    private async Task WaitUntilProcessedAsync(long userId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = factory.CreateDbContext();
            if (await db.Users.AnyAsync(u => u.Id == userId && u.DigestLastSentAt != null))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The digest was not processed.");
    }
}
