namespace Bluestaq.Notes.Api.Import;

public sealed class ImportOptions
{
    public const string SectionName = "Import";

    /// <summary>Largest number of notes a single import may contain. Matches the default export limit.</summary>
    public int MaxNotes { get; set; } = 10_000;
}

/// <summary>The import was rejected; nothing was saved. Keys are field paths such as <c>Notes[3].Title</c>.</summary>
public sealed class NoteImportException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("The import contains invalid notes.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
