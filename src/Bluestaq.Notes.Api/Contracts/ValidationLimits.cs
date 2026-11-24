namespace Bluestaq.Notes.Api.Contracts;

public static class ValidationLimits
{
    public const int UsernameMinLength = 3;
    public const int UsernameMaxLength = 50;
    public const string UsernamePattern = "^[A-Za-z0-9_.-]+$";
    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;
    public const int TitleMaxLength = 200;
    public const int ContentMaxLength = 100_000;
    public const int MaxPageSize = 100;
}
