using System.Text;
using Bluestaq.Notes.Api.Contracts;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.Import;

/// <summary>
/// Binds a <c>text/csv</c> body (the CSV export) to <see cref="NoteImportRequest"/>, so one import action accepts both
/// export formats. CSV errors become model-state errors, which <c>[ApiController]</c> returns as a 400.
/// </summary>
public sealed class NoteCsvInputFormatter : TextInputFormatter
{
    public NoteCsvInputFormatter()
    {
        SupportedMediaTypes.Add("text/csv");
        SupportedEncodings.Add(Encoding.UTF8);
    }

    protected override bool CanReadType(Type type) => type == typeof(NoteImportRequest);

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        var http = context.HttpContext;
        var maxNotes = http.RequestServices.GetRequiredService<IOptions<ImportOptions>>().Value.MaxNotes;
        // Not context.ReaderFactory: its reader's ReadToEndAsync(CancellationToken) falls back to synchronous reads.
        using var reader = new StreamReader(http.Request.Body, encoding);
        var csv = await reader.ReadToEndAsync(http.RequestAborted);
        try
        {
            return await InputFormatterResult.SuccessAsync(new NoteImportRequest { Notes = NoteCsvReader.Read(csv, maxNotes) });
        }
        catch (NoteImportException ex)
        {
            foreach (var (key, messages) in ex.Errors)
            {
                foreach (var message in messages)
                {
                    context.ModelState.TryAddModelError(key, message);
                }
            }

            return await InputFormatterResult.FailureAsync();
        }
    }
}
