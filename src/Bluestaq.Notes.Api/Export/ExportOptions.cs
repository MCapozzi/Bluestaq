namespace Bluestaq.Notes.Api.Export;

public sealed class ExportOptions
{
    public const string SectionName = "Export";

    /// <summary>Largest number of notes a single export may return; larger selections are rejected.</summary>
    public int MaxNotes { get; set; } = 10_000;
}

public sealed class ExportLimitExceededException(int maxNotes)
    : Exception($"The selection matches more than {maxNotes} notes.")
{
    public int MaxNotes { get; } = maxNotes;
}
