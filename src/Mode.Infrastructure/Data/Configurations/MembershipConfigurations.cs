using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mode.Domain;
using Mode.Domain.Entities;

namespace Mode.Infrastructure.Data.Configurations;

internal static class PropertyBuilderExtensions
{
    public static PropertyBuilder<T> IsUserId<T>(this PropertyBuilder<T> builder) =>
        builder.HasMaxLength(UserIds.MaxLength);

    public static PropertyBuilder<T> IsEnumString<T>(this PropertyBuilder<T> builder) =>
        builder.HasConversion<string>().HasMaxLength(32);
}

internal sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.Property(w => w.Name).HasMaxLength(100);
        b.Property(w => w.Description).HasMaxLength(1000);
        b.Property(w => w.CreatedByUserId).IsUserId();
    }
}

internal sealed class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> b)
    {
        b.HasKey(m => new { m.WorkspaceId, m.UserId });
        b.Property(m => m.UserId).IsUserId();
        b.Property(m => m.AddedByUserId).IsUserId();
        b.Property(m => m.Role).IsEnumString();
        b.HasIndex(m => m.UserId);
        b.HasOne(m => m.Workspace).WithMany(w => w.Members)
            .HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ChannelConfiguration : IEntityTypeConfiguration<Channel>
{
    public void Configure(EntityTypeBuilder<Channel> b)
    {
        b.Property(c => c.Name).HasMaxLength(80);
        b.Property(c => c.NormalizedName).HasMaxLength(80);
        b.Property(c => c.Description).HasMaxLength(1000);
        b.Property(c => c.Type).IsEnumString();
        b.Property(c => c.CreatedByUserId).IsUserId();
        b.HasIndex(c => new { c.WorkspaceId, c.NormalizedName }).IsUnique();
        b.HasOne(c => c.Workspace).WithMany(w => w.Channels)
            .HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ChannelMemberConfiguration : IEntityTypeConfiguration<ChannelMember>
{
    public void Configure(EntityTypeBuilder<ChannelMember> b)
    {
        b.HasKey(m => new { m.ChannelId, m.UserId });
        b.Property(m => m.UserId).IsUserId();
        b.Property(m => m.AddedByUserId).IsUserId();
        b.HasIndex(m => m.UserId);
        b.HasOne(m => m.Channel).WithMany(c => c.Members)
            .HasForeignKey(m => m.ChannelId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.Property(c => c.Type).IsEnumString();
        b.Property(c => c.Name).HasMaxLength(100);
        b.Property(c => c.DirectKey).HasMaxLength(UserIds.MaxLength * 2 + 1);
        b.Property(c => c.CreatedByUserId).IsUserId();
        b.HasIndex(c => c.DirectKey).IsUnique().HasFilter("\"DirectKey\" IS NOT NULL");
        b.ToTable(t => t.HasCheckConstraint(
            "CK_Conversations_DirectKey",
            "(\"Type\" = 'Direct') = (\"DirectKey\" IS NOT NULL)"));
    }
}

internal sealed class ConversationMemberConfiguration : IEntityTypeConfiguration<ConversationMember>
{
    public void Configure(EntityTypeBuilder<ConversationMember> b)
    {
        b.HasKey(m => new { m.ConversationId, m.UserId });
        b.Property(m => m.UserId).IsUserId();
        b.HasIndex(m => m.UserId);
        b.HasOne(m => m.Conversation).WithMany(c => c.Members)
            .HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
