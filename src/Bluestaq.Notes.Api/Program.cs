using Bluestaq.Notes.Api.Admin;
using Bluestaq.Notes.Api.Alerts;
using Bluestaq.Notes.Api.Auth;
using Bluestaq.Notes.Api.Data;
using Bluestaq.Notes.Api.Digest;
using Bluestaq.Notes.Api.Domain;
using Bluestaq.Notes.Api.Export;
using Bluestaq.Notes.Api.Import;
using Bluestaq.Notes.Api.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Notes");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'ConnectionStrings:Notes' is not configured.");
}

connectionString = ConnectionStrings.WithPassword(connectionString, builder.Configuration["Database:Password"]);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddScoped<AlertInterceptor>();
builder.Services.AddDbContext<NotesDbContext>((sp, options) => options
    .UseMySQL(connectionString)
    .AddInterceptors(sp.GetRequiredService<AuditInterceptor>(), sp.GetRequiredService<AlertInterceptor>()));
builder.Services.Configure<AlertOptions>(builder.Configuration.GetSection(AlertOptions.SectionName));
builder.Services.AddScoped<AlertService>();
builder.Services.AddSingleton<AlertConnections>();
builder.Services.AddSingleton<AdminAlertSender>();
builder.Services.AddMemoryCache();
builder.Services.Configure<RegistrationOptions>(builder.Configuration.GetSection(RegistrationOptions.SectionName));
builder.Services.AddSingleton<RegistrationFailures>();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertDispatcher>());
builder.Services.Configure<ExportOptions>(builder.Configuration.GetSection(ExportOptions.SectionName));
builder.Services.AddScoped<NoteExporter>();
builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection(ImportOptions.SectionName));
builder.Services.AddScoped<NoteImporter>();
builder.Services.Configure<DigestOptions>(builder.Configuration.GetSection(DigestOptions.SectionName));
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<DigestService>();
builder.Services.AddHostedService<DigestDispatcher>();
builder.Services.Configure<SessionAuthOptions>(builder.Configuration.GetSection(SessionAuthOptions.SectionName));
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.Configure<AdminAuditOptions>(builder.Configuration.GetSection(AdminAuditOptions.SectionName));
builder.Services.AddSingleton<AdminAuditFileLog>();
builder.Services.AddSingleton<IAdminAuditLog, AdminAuditLog>();

builder.Services
    .AddAuthentication(SessionAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationDefaults.Scheme, null);

// Every endpoint requires a valid session unless explicitly marked [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Roles.AdministratorPolicy, policy => policy.RequireRole(Roles.Administrator));

var authRateLimit = builder.Configuration.GetSection(AuthRateLimitOptions.SectionName).Get<AuthRateLimitOptions>()
    ?? new AuthRateLimitOptions();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthRateLimitOptions.PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authRateLimit.PermitLimit,
            Window = authRateLimit.Window,
            QueueLimit = 0,
        }));
});

builder.Services.AddControllers(o => o.InputFormatters.Add(new NoteCsvInputFormatter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<NotesDbContext>().Database.MigrateAsync();
}

{
    using var scope = app.Services.CreateScope();
    var bootstrap = app.Configuration.GetSection(AdminBootstrapOptions.SectionName).Get<AdminBootstrapOptions>() ?? new();
    var admin = await AdminBootstrapper.EnsureAsync(
        scope.ServiceProvider.GetRequiredService<NotesDbContext>(),
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
        bootstrap,
        TimeProvider.System,
        CancellationToken.None);
    if (admin is not null)
    {
        app.Logger.LogInformation("Bootstrap administrator account: {Username}", admin.Username);
    }
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.UseWebSockets();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<AdminAuditMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.Map(AlertEndpoint.Path, AlertEndpoint.HandleAsync);
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

await app.RunAsync();

public partial class Program;
