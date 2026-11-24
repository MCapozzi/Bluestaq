using System.ComponentModel.DataAnnotations;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Domain;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Import;

/// <summary>
/// Creates notes from an export (possibly from another instance). Imported notes get new IDs and belong to the
/// importing user; audit fields are stamped as of the import. All notes are validated first and saved in one
/// transaction, so an import either fully succeeds or saves nothing.
/// </summary>
public sealed class NoteImporter(NotesDbContext db, TimeProvider clock, IOptions<ImportOptions> options)
{
    public async Task<NoteImportResponse> ImportAsync(long ownerId, IReadOnlyList<ImportedNote?> notes, CancellationToken ct)
    {
        var errors = Validate(notes);
        if (errors.Count > 0)
        {
            throw new NoteImportException(errors);
        }

        var created = notes.Select(n => new Note
        {
            OwnerId = ownerId,
            Title = n!.Title!,
            Content = n.Content!,
            Visibility = n.Visibility,
        }).ToList();
        db.Notes.AddRange(created);
        await db.SaveChangesAsync(ct);

        return new NoteImportResponse(
            clock.GetUtcNow().UtcDateTime,
            created.Count,
            notes.Zip(created, (source, note) => new ImportedNoteResult(source!.Id, NoteResponse.From(note))).ToList());
    }

    private Dictionary<string, string[]> Validate(IReadOnlyList<ImportedNote?> notes)
    {
        var errors = new Dictionary<string, string[]>();
        var maxNotes = options.Value.MaxNotes;
        if (notes.Count == 0)
        {
            errors[nameof(NoteImportRequest.Notes)] = ["notes must contain at least one note."];
        }
        else if (notes.Count > maxNotes)
        {
            errors[nameof(NoteImportRequest.Notes)] = [$"An import may contain at most {maxNotes} notes; split the file."];
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        for (var i = 0; i < notes.Count; i++)
        {
            var key = $"{nameof(NoteImportRequest.Notes)}[{i}]";
            if (notes[i] is not { } note)
            {
                errors[key] = ["A note is required."];
                continue;
            }

            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(note, new ValidationContext(note), results, validateAllProperties: true))
            {
                foreach (var result in results)
                {
                    var member = result.MemberNames.FirstOrDefault();
                    errors[member is null ? key : $"{key}.{member}"] = [result.ErrorMessage ?? "Invalid value."];
                }
            }
        }

        return errors;
    }
}
