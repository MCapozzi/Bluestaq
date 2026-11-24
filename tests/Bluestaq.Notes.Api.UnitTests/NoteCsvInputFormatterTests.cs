using System.Text;
using Bluestaq.Notes.Api.Contracts;
using Bluestaq.Notes.Api.Import;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class NoteCsvInputFormatterTests
{
    private readonly NoteCsvInputFormatter _formatter = new();

    private static InputFormatterContext Context(string csv, Type? type = null, int maxNotes = 10)
    {
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(Options.Create(new ImportOptions { MaxNotes = maxNotes })).BuildServiceProvider(),
        };
        http.Request.ContentType = "text/csv; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return new InputFormatterContext(http, "", new ModelStateDictionary(),
            new EmptyModelMetadataProvider().GetMetadataForType(type ?? typeof(NoteImportRequest)),
            (stream, encoding) => new StreamReader(stream, encoding));
    }

    [Fact]
    public void Reads_only_import_requests_sent_as_csv()
    {
        Assert.True(_formatter.CanRead(Context("")));
        Assert.False(_formatter.CanRead(Context("", typeof(NoteRequest))));
    }

    [Fact]
    public async Task Binds_csv_rows_to_the_import_request()
    {
        var result = await _formatter.ReadAsync(Context("Id,Title,Content\r\n5,T,C\r\n"));

        var note = Assert.Single(Assert.IsType<NoteImportRequest>(result.Model).Notes!)!;
        Assert.Equal((5L, "T", "C"), ((long)note.Id!, note.Title!, note.Content!));
    }

    [Fact]
    public async Task Csv_errors_become_model_state_errors()
    {
        var context = Context("Title,Content\nA,1\nB,2", maxNotes: 1);

        var result = await _formatter.ReadAsync(context);

        Assert.True(result.HasError);
        Assert.Contains("at most 1", Assert.Single(context.ModelState["Notes"]!.Errors).ErrorMessage);
    }
}
