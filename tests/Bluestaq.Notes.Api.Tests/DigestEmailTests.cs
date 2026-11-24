using System.Net;
using System.Net.Http.Json;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Domain;

namespace Bluestaq.Notes.Api.Tests;

[Collection(ApiCollection.Name)]
public sealed class DigestEmailTests(NotesApiFactory factory)
{
    [Fact]
    public async Task Digest_is_off_by_default_and_rejects_unknown_frequencies()
    {
        var (bob, _) = await ApiClient.CreateAuthenticatedClientAsync(factory);

        var settings = (await bob.GetFromJsonAsync<DigestSettingsResponse>("/api/digest"))!;
        Assert.Equal((DigestFrequency.None, "TEST@TEST.TEST"), (settings.Frequency, settings.Recipient));

        var bad = await bob.PutAsJsonAsync("/api/digest", new { frequency = "Hourly" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Opted_in_users_are_emailed_their_shared_notes_and_others_are_not()
    {
        var (alice, aliceSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (bob, bobSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var (_, carolSession) = await ApiClient.CreateAuthenticatedClientAsync(factory);
        var note = (await (await alice.PostAsJsonAsync("/api/notes", new NoteRequest("Digest me", "body"))).Content.ReadFromJsonAsync<NoteResponse>())!;
        foreach (var user in new[] { bobSession, carolSession })
        {
            (await alice.PostAsJsonAsync($"/api/notes/{note.Id}/shares", new ShareNoteRequest(user.User.Username))).EnsureSuccessStatusCode();
        }

        var preview = (await bob.GetFromJsonAsync<NoteDigest>("/api/digest/preview"))!;
        Assert.Equal("Digest me", Assert.Single(preview.NewlyShared).Title);

        var put = await bob.PutAsJsonAsync("/api/digest", new UpdateDigestRequest(DigestFrequency.Daily));
        Assert.Equal(DigestFrequency.Daily, (await put.Content.ReadFromJsonAsync<DigestSettingsResponse>())!.Frequency);

        var email = await factory.Emails.WaitForAsync(m => m.Subject.Contains(bobSession.User.Username), TimeSpan.FromSeconds(15));
        Assert.Equal("TEST@TEST.TEST", email.To);
        Assert.Contains("\"Digest me\"", email.TextBody);
        Assert.Contains($"shared by {aliceSession.User.Username}", email.TextBody);
        Assert.DoesNotContain(factory.Emails.Sent, m => m.Subject.Contains(carolSession.User.Username));
    }
}
