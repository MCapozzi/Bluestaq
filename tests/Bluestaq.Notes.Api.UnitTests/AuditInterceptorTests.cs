using Bluestaq.Notes.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class AuditInterceptorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Insert_stamps_creator_updater_and_timestamps()
    {
        var alice = await _db.AddUserAsync();
        _db.CurrentUser.UserId = alice.Id;

        await using var ctx = _db.CreateContext();
        var note = new Note { OwnerId = alice.Id, Title = "t", Content = "c", CreatedByUserId = 999, CreatedAt = DateTime.MinValue };
        ctx.Notes.Add(note);
        await ctx.SaveChangesAsync();

        var now = TestDb.Start.UtcDateTime;
        Assert.Equal(alice.Id, note.CreatedByUserId);
        Assert.Equal(alice.Id, note.UpdatedByUserId);
        Assert.Equal(now, note.CreatedAt);
        Assert.Equal(now, note.UpdatedAt);
    }

    [Fact]
    public async Task Synchronous_save_is_also_audited()
    {
        var alice = await _db.AddUserAsync();
        _db.CurrentUser.UserId = alice.Id;

        using var ctx = _db.CreateContext();
        var note = new Note { OwnerId = alice.Id, Title = "t", Content = "c" };
        ctx.Notes.Add(note);
        ctx.SaveChanges();

        Assert.Equal(alice.Id, note.CreatedByUserId);
        Assert.Equal(TestDb.Start.UtcDateTime, note.CreatedAt);
    }

    [Fact]
    public async Task Update_stamps_updater_and_preserves_created_fields()
    {
        var alice = await _db.AddUserAsync("alice");
        var bob = await _db.AddUserAsync("bob");
        var note = await _db.AddNoteAsync(alice.Id);

        _db.Clock.Advance(TimeSpan.FromMinutes(5));
        _db.CurrentUser.UserId = bob.Id;
        await using (var ctx = _db.CreateContext())
        {
            var tracked = await ctx.Notes.SingleAsync(n => n.Id == note.Id);
            tracked.Content = "changed";
            tracked.CreatedByUserId = bob.Id; // tampering must not persist
            tracked.CreatedAt = DateTime.UnixEpoch;
            await ctx.SaveChangesAsync();
        }

        await using var verify = _db.CreateContext();
        var stored = await verify.Notes.SingleAsync(n => n.Id == note.Id);
        Assert.Equal(alice.Id, stored.CreatedByUserId);
        Assert.Equal(TestDb.Start.UtcDateTime, stored.CreatedAt);
        Assert.Equal(bob.Id, stored.UpdatedByUserId);
        Assert.Equal(TestDb.Start.UtcDateTime.AddMinutes(5), stored.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, stored.UpdatedAt.Kind);
    }

    [Fact]
    public async Task Saving_auditable_entity_without_user_throws()
    {
        var alice = await _db.AddUserAsync();
        _db.CurrentUser.UserId = null;

        await using var ctx = _db.CreateContext();
        ctx.Notes.Add(new Note { OwnerId = alice.Id, Title = "t", Content = "c" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Non_auditable_entities_and_deletes_do_not_require_a_user()
    {
        var alice = await _db.AddUserAsync();
        var note = await _db.AddNoteAsync(alice.Id);
        _db.CurrentUser.UserId = null;

        await using var ctx = _db.CreateContext();
        ctx.Notes.Remove(await ctx.Notes.SingleAsync(n => n.Id == note.Id));
        await ctx.SaveChangesAsync();

        Assert.False(await ctx.Notes.AnyAsync());
    }

    [Fact]
    public async Task Timestamps_are_truncated_to_microseconds()
    {
        var alice = await _db.AddUserAsync();
        _db.Clock.Advance(TimeSpan.FromTicks(1234567)); // 123456.7 microseconds
        var note = await _db.AddNoteAsync(alice.Id);

        Assert.Equal(0, note.CreatedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        Assert.Equal(TestDb.Start.UtcDateTime.AddTicks(1234560), note.CreatedAt);
    }
}
