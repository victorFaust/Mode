using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mode.Domain.Entities;
using NpgsqlTypes;

namespace Mode.Infrastructure.Data.Configurations;

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public const string SearchVectorProperty = "SearchVector";

    public void Configure(EntityTypeBuilder<Message> b)
    {
        b.Property(m => m.Id).ValueGeneratedNever();
        b.Property(m => m.AuthorUserId).IsUserId();
        b.Property(m => m.DeletedByUserId).IsUserId();
        b.Property(m => m.ForwardedFromUserId).IsUserId();

        b.Property<NpgsqlTsVector>(SearchVectorProperty)
            .HasComputedColumnSql("to_tsvector('english', \"BodyMarkdown\" || ' ' || coalesce(\"ForwardedBodyMarkdown\", ''))", stored: true);
        b.HasIndex(SearchVectorProperty).HasMethod("GIN");

        b.HasIndex(m => new { m.ChannelId, m.Sequence }).IsUnique();
        b.HasIndex(m => new { m.ConversationId, m.Sequence }).IsUnique();
        b.HasIndex(m => new { m.ChannelId, m.UpdatedSequence });
        b.HasIndex(m => new { m.ConversationId, m.UpdatedSequence });
        b.HasIndex(m => m.ParentMessageId);
        b.HasIndex(m => new { m.AuthorUserId, m.CreatedAt });

        b.ToTable(t => t.HasCheckConstraint(
            "CK_Messages_SingleRoom",
            "num_nonnulls(\"ChannelId\", \"ConversationId\") = 1"));

        b.HasOne(m => m.Channel).WithMany()
            .HasForeignKey(m => m.ChannelId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(m => m.Conversation).WithMany()
            .HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(m => m.Parent).WithMany()
            .HasForeignKey(m => m.ParentMessageId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Message>().WithMany()
            .HasForeignKey(m => m.ForwardedFromMessageId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class PinnedMessageConfiguration : IEntityTypeConfiguration<PinnedMessage>
{
    public void Configure(EntityTypeBuilder<PinnedMessage> b)
    {
        b.HasKey(p => p.MessageId);
        b.Property(p => p.PinnedByUserId).IsUserId();
        b.HasOne(p => p.Message).WithOne(m => m.Pin)
            .HasForeignKey<PinnedMessage>(p => p.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MessageEditConfiguration : IEntityTypeConfiguration<MessageEdit>
{
    public void Configure(EntityTypeBuilder<MessageEdit> b)
    {
        b.Property(e => e.Kind).IsEnumString();
        b.Property(e => e.ChangedByUserId).IsUserId();
        b.HasIndex(e => e.MessageId);
        b.HasOne(e => e.Message).WithMany(m => m.Edits)
            .HasForeignKey(e => e.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReactionConfiguration : IEntityTypeConfiguration<Reaction>
{
    public void Configure(EntityTypeBuilder<Reaction> b)
    {
        b.HasKey(r => new { r.MessageId, r.UserId, r.Emoji });
        b.Property(r => r.UserId).IsUserId();
        b.Property(r => r.Emoji).HasMaxLength(64);
        b.HasOne(r => r.Message).WithMany(m => m.Reactions)
            .HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MentionConfiguration : IEntityTypeConfiguration<Mention>
{
    public void Configure(EntityTypeBuilder<Mention> b)
    {
        b.Property(m => m.Kind).IsEnumString();
        b.Property(m => m.TargetUserId).IsUserId();
        b.HasIndex(m => m.MessageId);
        b.HasIndex(m => m.TargetUserId);
        b.ToTable(t => t.HasCheckConstraint(
            "CK_Mentions_Target",
            "(\"Kind\" = 'User') = (\"TargetUserId\" IS NOT NULL)"));
        b.HasOne(m => m.Message).WithMany(msg => msg.Mentions)
            .HasForeignKey(m => m.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
