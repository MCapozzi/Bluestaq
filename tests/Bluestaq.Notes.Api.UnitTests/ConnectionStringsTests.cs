using Bluestaq.Notes.Api.Data;
using MySql.Data.MySqlClient;

namespace Bluestaq.Notes.Api.UnitTests;

public sealed class ConnectionStringsTests
{
    [Theory]
    [InlineData("plain-password")]
    [InlineData("'starts-with-quote")]
    [InlineData("semi;colon=and\"double")]
    public void Password_round_trips_whatever_characters_it_contains(string password)
    {
        var result = ConnectionStrings.WithPassword("Server=mysql;Port=3306;Database=notes;User=notes;", password);

        var parsed = new MySqlConnectionStringBuilder(result);
        Assert.Equal(password, parsed.Password);
        Assert.Equal(("mysql", "notes", "notes"), (parsed.Server, parsed.Database, parsed.UserID));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_password_leaves_the_connection_string_unchanged(string? password)
    {
        const string cs = "Server=mysql;Password=fromconfig;";
        Assert.Equal(cs, ConnectionStrings.WithPassword(cs, password));
    }
}
