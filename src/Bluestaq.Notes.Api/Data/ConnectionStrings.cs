using MySql.Data.MySqlClient;

namespace Bluestaq.Notes.Api.Data;

public static class ConnectionStrings
{
    /// <summary>
    /// Applies a separately supplied password via the provider's builder so it's escaped correctly, whatever
    /// characters it contains (quotes, semicolons, ...). Returns the input unchanged when no password is given.
    /// </summary>
    public static string WithPassword(string connectionString, string? password) =>
        string.IsNullOrEmpty(password)
            ? connectionString
            : new MySqlConnectionStringBuilder(connectionString) { Password = password }.ConnectionString;
}
