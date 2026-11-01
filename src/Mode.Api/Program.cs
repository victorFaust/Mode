using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Mode.Api.Auditing;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Endpoints;
using Mode.Api.Files;
using Mode.Api.Files.Processing;
using Mode.Api.Files.Scanning;
using Mode.Api.Hubs;
using Mode.Api.Jobs;
using Mode.Api.Messaging;
using Mode.Api.Notifications;
using Mode.Api.Presence;
using Mode.Api.Realtime;
using Mode.Domain.Files;
using Mode.Infrastructure;
using Mode.Infrastructure.Files;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

// Set by scripts/export-openapi.ps1: the Swashbuckle CLI starts the host only to read endpoint metadata.
var openApiExport = Environment.GetEnvironmentVariable("MODE_OPENAPI_EXPORT") == "1";

builder.Host.UseSerilog((context, services, logger) =>
{
    logger.ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext();

    if (context.HostingEnvironment.IsDevelopment())
        logger.WriteTo.Console();
    else
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
});

builder.Services.AddModeInfrastructure();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();

builder.Services.AddModeAuthentication();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireClaim(ModeClaims.UserId).Build());
builder.Services.AddScoped(sp => CurrentUser.FromPrincipal(
    sp.GetRequiredService<IHttpContextAccessor>().HttpContext?.User
    ?? throw new InvalidOperationException("No HTTP context for the current user.")));
builder.Services.AddScoped<AccessPolicy>();
builder.Services.AddScoped<IAuditLog, AuditLogWriter>();
builder.Services.AddScoped<MessageStore>();
builder.Services.AddScoped<MentionProcessor>();
builder.Services.AddScoped<AccessRevoker>();
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.AddSingleton<RealtimeNotifier>();
builder.Services.AddSingleton<TypingThrottle>();

builder.Services.AddOptions<FilesOptions>()
    .BindConfiguration(FilesOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => o.IsValid(out _), "Invalid Files configuration; see FilesOptions.IsValid.")
    .ValidateOnStart();
builder.Services.AddOptions<ClamAvOptions>().BindConfiguration(ClamAvOptions.SectionName);
builder.Services.AddOptions<JobOptions>().BindConfiguration(JobOptions.SectionName);
builder.Services.AddSingleton<IFileStore>(sp => new LocalFileStore(sp.GetRequiredService<IOptions<FilesOptions>>().Value.StoragePath));
builder.Services.AddSingleton<FileLinkSigner>();
builder.Services.AddSingleton<IVirusScanner, ClamAvScanner>();
builder.Services.AddHttpClient<IPreviewProcessor, HttpPreviewProcessor>(c => c.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddScoped<FileQueries>();
builder.Services.AddScoped<JobQueue>();
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddScoped<IJobHandler, FileScanJob>();
builder.Services.AddScoped<IJobHandler, FilePreviewJob>();
builder.Services.AddScoped<IJobHandler, FilePurgeJob>();
if (!openApiExport) builder.Services.AddHostedService<JobWorker>();

builder.Services.AddOptions<PresenceOptions>().BindConfiguration(PresenceOptions.SectionName);
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<PresenceNotifier>();
if (!openApiExport) builder.Services.AddHostedService<PresenceSweeper>();

builder.Services.AddOptions<PushOptions>().BindConfiguration(PushOptions.SectionName);
builder.Services.AddOptions<EmailOptions>().BindConfiguration(EmailOptions.SectionName);
builder.Services.AddScoped<PreferencesStore>();
builder.Services.AddScoped<NotificationContent>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddHttpClient<IPushSender, WebPushSender>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IJobHandler, PushSendJob>();
builder.Services.AddScoped<IJobHandler, ActivityExpiryJob>();
builder.Services.AddScoped<IJobHandler, EmailDigestJob>();

builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
// Swashbuckle reads the MVC options when describing schemas.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SupportNonNullableReferenceTypes();
    o.NonNullableReferenceTypesAsRequired();
});

// The host application embeds the client from its own origin; empty means same-origin only.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

// No secrets are configured for an export, so skip the checks that would refuse to start without them.
if (openApiExport) builder.Services.RemoveAll<IStartupValidator>();
builder.Services.AddHealthChecks().AddModeInfrastructureChecks(tags: ["ready"]);

var app = builder.Build();

app.UseSerilogRequestLogging(options => options.GetMessageTemplateProperties = (http, path, elapsed, status) =>
[
    new LogEventProperty("RequestMethod", new ScalarValue(http.Request.Method)),
    new LogEventProperty("RequestPath", new ScalarValue(SignedLinkRedaction.Redact(path))),
    new LogEventProperty("StatusCode", new ScalarValue(status)),
    new LogEventProperty("Elapsed", new ScalarValue(elapsed)),
]);
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Liveness: process is up. Readiness: database and host user directory are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

app.MapGroup("/api/v1")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .MapUserEndpoints()
    .MapWorkspaceEndpoints()
    .MapChannelEndpoints()
    .MapConversationEndpoints()
    .MapMessageEndpoints()
    .MapFileEndpoints()
    .MapActivityEndpoints()
    .MapRoomStateEndpoints()
    .MapSearchEndpoints();

app.MapInternalFileEndpoints();

app.MapHub<ModeHub>(ModeHub.Path, options => options.CloseOnAuthenticationExpiration = true);

app.Run();

public partial class Program;
