namespace Mode.Domain.Entities;

public class Conversation
{
    public Guid Id { get; set; }
    public ConversationType Type { get; set; }
    public string? Name { get; set; }

    /// <summary>For direct chats only: the two user ids, ordinal-sorted and joined with '|'. Unique.</summary>
    public string? DirectKey { get; set; }

    public required string CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long LastSequence { get; set; }

    public List<ConversationMember> Members { get; set; } = [];
}

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public required string UserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }

    /// <summary>Per-person chat list settings. A new message in the chat unhides it.</summary>
    public bool IsPinned { get; set; }
    public bool IsHidden { get; set; }

    public Conversation? Conversation { get; set; }
}
