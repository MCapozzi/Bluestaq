using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Bluestaq.Notes.Api.Contracts;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NoteExportRequestTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<ValidationResult> Validate(NoteExportRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }

    public static TheoryData<NoteExportRequest> ValidRequests => new()
    {
        new NoteExportRequest(NoteIds: [1, 2]),
        new NoteExportRequest(UserId: 5),
        new NoteExportRequest(From: Jan1, To: Jan1.AddDays(1)),
        new NoteExportRequest(From: Jan1, To: Jan1),
        new NoteExportRequest(UserId: 5, Format: ExportFormat.Csv),
    };

    [Theory]
    [MemberData(nameof(ValidRequests))]
    public void Valid_requests_pass(NoteExportRequest request)
    {
        Assert.Empty(Validate(request));
    }

    public static TheoryData<NoteExportRequest, string> InvalidRequests => new()
    {
        { new NoteExportRequest(), "exactly one" },
        { new NoteExportRequest(NoteIds: [1], UserId: 1), "exactly one" },
        { new NoteExportRequest(UserId: 1, From: Jan1, To: Jan1), "exactly one" },
        { new NoteExportRequest(NoteIds: [1], To: Jan1), "exactly one" },
        { new NoteExportRequest(NoteIds: []), "at least one" },
        { new NoteExportRequest(From: Jan1), "Both from and to" },
        { new NoteExportRequest(To: Jan1), "Both from and to" },
        { new NoteExportRequest(From: Jan1.AddDays(1), To: Jan1), "on or before" },
        { new NoteExportRequest(NoteIds: Enumerable.Range(1, ValidationLimits.MaxExportNoteIds + 1).Select(i => (long)i).ToArray()), "maximum length" },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void Invalid_requests_fail(NoteExportRequest request, string expectedMessage)
    {
        var error = Assert.Single(Validate(request));
        Assert.Contains(expectedMessage, error.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToUtc_normalizes_every_kind()
    {
        var unspecified = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Unspecified);
        Assert.Equal(DateTimeKind.Utc, NoteExportRequest.ToUtc(unspecified).Kind);
        Assert.Equal(unspecified.Ticks, NoteExportRequest.ToUtc(unspecified).Ticks);

        Assert.Equal(Jan1, NoteExportRequest.ToUtc(Jan1));

        var local = Jan1.ToLocalTime();
        Assert.Equal(Jan1, NoteExportRequest.ToUtc(local));
    }

    [Fact]
    public void Format_is_serialized_as_a_case_insensitive_string()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = JsonSerializer.Deserialize<NoteExportRequest>("""{"userId":1,"format":"csv"}""", options)!;
        Assert.Equal(ExportFormat.Csv, request.Format);
        Assert.Equal(ExportFormat.Json, JsonSerializer.Deserialize<NoteExportRequest>("""{"userId":1}""", options)!.Format);
        Assert.Contains("\"Json\"", JsonSerializer.Serialize(ExportFormat.Json));
    }

    [Fact]
    public void Undefined_format_values_are_rejected()
    {
        var error = Assert.Single(Validate(new NoteExportRequest(UserId: 1, Format: (ExportFormat)7)));
        Assert.Equal([nameof(NoteExportRequest.Format)], error.MemberNames);
    }
}
