using System.Text.RegularExpressions;
using Markdig;
using Microsoft.EntityFrameworkCore;
using Mode.Domain.Directory;
using Mode.Domain.Entities;
using Mode.Infrastructure.Data;

namespace Mode.Api.Notifications;

public sealed record NotificationText(string Title, string Body);

/// <summary>Human-readable title and body for activity items, shared by the feed, banners, push and email.</summary>
public sealed partial class NotificationContent(ModeDbContext db, IUserDirectory directory)
{
    private const int SnippetLength = 140;

    [GeneratedRegex(@"@\{(?<id>[^{}\s]{1,64})\}")]
    private static partial Regex MentionToken();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public async Task<IReadOnlyDictionary<Guid, NotificationText>> BuildAsync(IReadOnlyCollection<ActivityItem> items, CancellationToken ct)
    {
        if (items.Count == 0) return new Dictionary<Guid, NotificationText>();

        var channelIds = items.Where(i => i.ChannelId is not null).Select(i => i.ChannelId!.Value).Distinct().ToList();
        var conversationIds = items.Where(i => i.ConversationId is not null).Select(i => i.ConversationId!.Value).Distinct().ToList();
        var workspaceIds = items.Where(i => i.WorkspaceId is not null).Select(i => i.WorkspaceId!.Value).Distinct().ToList();
        var messageIds = items.Where(i => i.MessageId is not null).Select(i => i.MessageId!.Value).Distinct().ToList();

        var channels = await db.Channels.Where(c => channelIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var groups = await db.Conversations.Where(c => conversationIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var workspaces = await db.Workspaces.Where(w => workspaceIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var bodies = await db.Messages.Where(m => messageIds.Contains(m.Id) && m.DeletedAt == null)
            .ToDictionaryAsync(m => m.Id, m => m.BodyMarkdown, ct);

        var mentioned = bodies.Values.SelectMany(b => MentionToken().Matches(b).Select(m => m.Groups["id"].Value));
        var people = await directory.GetManyAsync(items.Select(i => i.ActorUserId).Concat(mentioned).Distinct().ToList(), ct);
        string Name(string id) => directory.TryNormalizeId(id, out var key) && people.TryGetValue(key, out var person) ? person.DisplayName : "Someone";

        return items.ToDictionary(i => i.Id, item =>
        {
            var actor = Name(item.ActorUserId);
            var where = item.ChannelId is { } channelId && channels.TryGetValue(channelId, out var channel) ? $"#{channel}"
                : item.ConversationId is { } conversationId ? groups.GetValueOrDefault(conversationId) ?? "your chat"
                : item.WorkspaceId is { } workspaceId ? workspaces.GetValueOrDefault(workspaceId) ?? "a workspace"
                : "Mode";
            var title = item.Type switch
            {
                ActivityType.Mention => $"{actor} mentioned you in {where}",
                ActivityType.ChannelMention => $"{actor} mentioned everyone in {where}",
                ActivityType.Reply => $"{actor} replied in {where}",
                ActivityType.Reaction => $"{actor} reacted to your message in {where}",
                ActivityType.ChatMessage => item.ConversationId is { } id && groups.GetValueOrDefault(id) is { } group ? $"{actor} in {group}" : actor,
                ActivityType.AddedToChannel => $"{actor} added you to {where}",
                ActivityType.AddedToWorkspace => $"{actor} added you to {where}",
                _ => actor,
            };
            var body = item.MessageId is { } messageId && bodies.TryGetValue(messageId, out var markdown) ? Snippet(markdown, Name) : "";
            return new NotificationText(title, body);
        });
    }

    private static string Snippet(string markdown, Func<string, string> name)
    {
        var withNames = MentionToken().Replace(markdown, m => "@" + name(m.Groups["id"].Value));
        var plain = Whitespace().Replace(Markdown.ToPlainText(withNames), " ").Trim();
        return plain.Length <= SnippetLength ? plain : plain[..(SnippetLength - 1)].TrimEnd() + "…";
    }
}
