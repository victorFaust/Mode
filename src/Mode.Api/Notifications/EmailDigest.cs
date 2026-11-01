using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using Mode.Api.Auth;
using Mode.Api.Authorization;
using Mode.Api.Jobs;
using Mode.Domain.Directory;
using Mode.Infrastructure.Data;

namespace Mode.Api.Notifications;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; }
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;

    /// <summary>STARTTLS on the submission port (587). Set false only for a relay that uses implicit TLS on 465.</summary>
    public bool UseStartTls { get; set; } = true;

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "Mode";

    /// <summary>Hour (UTC) at which daily digests go out.</summary>
    public int DailyDigestHourUtc { get; set; } = 7;

    /// <summary>Link to Mode in the host application, included in emails.</summary>
    public string AppUrl { get; set; } = "";
}

public interface IEmailSender
{
    Task SendAsync(string toAddress, string toName, string subject, string textBody, string htmlBody, CancellationToken cancellationToken);
}

public sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    public async Task SendAsync(string toAddress, string toName, string subject, string textBody, string htmlBody, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        message.To.Add(new MailboxAddress(toName, toAddress));
        message.Subject = subject;
        message.Body = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host, settings.Port,
            settings.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, cancellationToken);
        if (settings.Username.Length > 0)
            await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

/// <summary>Emails each opted-in person their unread activity not yet emailed (hourly, or once a day).</summary>
public sealed class EmailDigestJob(
    ModeDbContext db, PreferencesStore preferences, AccessPolicy policy, NotificationContent content, IUserDirectory directory,
    IEmailSender sender, IOptions<EmailOptions> options, TimeProvider clock, ILogger<EmailDigestJob> logger) : IJobHandler
{
    public const int MaxItemsPerEmail = 50;

    /// <summary>Gives people a chance to see an item in Mode before it is emailed.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    public string Type => JobTypes.EmailDigest;

    public async Task HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled) return;

        var now = clock.GetUtcNow();
        var due = (await preferences.WithDigestAsync(ct))
            .Where(p => p.Settings.EmailDigest == DigestFrequency.Hourly || now.Hour == settings.DailyDigestHourUtc)
            .Select(p => p.UserId)
            .ToList();

        var failed = 0;
        foreach (var userId in due)
        {
            try
            {
                await SendDigestAsync(userId, now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                logger.LogWarning(ex, "Email digest for {UserId} failed", userId);
            }
        }
        if (failed > 0) throw new InvalidOperationException($"{failed} digest email(s) failed; they will be retried.");
    }

    private async Task SendDigestAsync(string userId, DateTimeOffset now, CancellationToken ct)
    {
        var person = await directory.GetByIdAsync(userId, ct);
        if (person is not { IsActive: true, Email: { Length: > 0 } email }) return;

        var rooms = await policy.ReadableRoomsAsync(new CurrentUser(userId, person.DisplayName, email, false, false), ct);
        var items = await db.ActivityItems
            .Where(a => a.UserId == userId && a.ReadAt == null && a.EmailedAt == null && a.ExpiresAt > now && a.CreatedAt <= now - Grace)
            .Where(ActivityVisibility.VisibleIn(rooms))
            .OrderByDescending(a => a.CreatedAt)
            .Take(MaxItemsPerEmail)
            .ToListAsync(ct);
        if (items.Count == 0) return;

        var texts = await content.BuildAsync(items, ct);
        var subject = items.Count == 1 ? texts[items[0].Id].Title : $"You have {items.Count} unread notifications in Mode";
        var text = new StringBuilder();
        var html = new StringBuilder("<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px\"><ul>");
        foreach (var item in items)
        {
            var line = texts[item.Id];
            text.AppendLine($"- {line.Title}{(line.Body.Length > 0 ? ": " + line.Body : "")}");
            html.Append("<li><strong>").Append(HtmlEncoder.Default.Encode(line.Title)).Append("</strong>");
            if (line.Body.Length > 0) html.Append("<br>").Append(HtmlEncoder.Default.Encode(line.Body));
            html.Append("</li>");
        }
        html.Append("</ul>");
        if (options.Value.AppUrl.Length > 0)
        {
            text.AppendLine().AppendLine($"Open Mode: {options.Value.AppUrl}");
            html.Append("<p><a href=\"").Append(HtmlEncoder.Default.Encode(options.Value.AppUrl)).Append("\">Open Mode</a></p>");
        }
        html.Append("</div>");

        await sender.SendAsync(email, person.DisplayName, subject, text.ToString(), html.ToString(), ct);

        var ids = items.Select(i => i.Id).ToList();
        await db.ActivityItems.Where(a => ids.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.EmailedAt, now), ct);
    }
}
