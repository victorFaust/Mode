using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mode.Domain.Entities;
using NpgsqlTypes;

namespace Mode.Infrastructure.Data.Configurations;

internal sealed class UploadSessionConfiguration : IEntityTypeConfiguration<UploadSession>
{
    public void Configure(EntityTypeBuilder<UploadSession> b)
    {
        b.Property(u => u.UserId).IsUserId();
        b.Property(u => u.FileName).HasMaxLength(255);
        b.Property(u => u.NormalizedName).HasMaxLength(255);
        b.Property(u => u.ContentType).HasMaxLength(255);
        b.Property(u => u.StorageKey).HasMaxLength(512);
        b.HasIndex(u => u.StorageKey).IsUnique();
        b.HasIndex(u => u.ExpiresAt);
        b.ToTable(t => t.HasCheckConstraint(
            "CK_UploadSessions_SingleRoom",
            "num_nonnulls(\"ChannelId\", \"ConversationId\") = 1"));
        b.HasOne<Channel>().WithMany().HasForeignKey(u => u.ChannelId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Conversation>().WithMany().HasForeignKey(u => u.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class MessageAttachmentConfiguration : IEntityTypeConfiguration<MessageAttachment>
{
    public void Configure(EntityTypeBuilder<MessageAttachment> b)
    {
        b.HasKey(a => new { a.MessageId, a.FileAssetId });
        b.HasIndex(a => a.FileAssetId);
        b.HasOne<Message>().WithMany(m => m.Attachments).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(a => a.FileAsset).WithMany().HasForeignKey(a => a.FileAssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> b)
    {
        b.Property(j => j.Id).UseIdentityAlwaysColumn();
        b.Property(j => j.Type).HasMaxLength(64);
        b.Property(j => j.PayloadJson).HasColumnType("jsonb");
        b.Property(j => j.Status).IsEnumString();
        b.Property(j => j.LastError).HasMaxLength(2000);
        b.HasIndex(j => new { j.Status, j.RunAfter });
    }
}

internal sealed class FileAssetConfiguration : IEntityTypeConfiguration<FileAsset>
{
    public void Configure(EntityTypeBuilder<FileAsset> b)
    {
        b.Property(f => f.Name).HasMaxLength(255);
        b.Property(f => f.NormalizedName).HasMaxLength(255);
        b.Property(f => f.CreatedByUserId).IsUserId();
        b.Property(f => f.DeletedByUserId).IsUserId();

        // Live (non-deleted) names are unique per room; re-uploading the same name adds a version.
        b.HasIndex(f => new { f.ChannelId, f.NormalizedName }).IsUnique()
            .HasFilter("\"ChannelId\" IS NOT NULL AND \"DeletedAt\" IS NULL");
        b.HasIndex(f => new { f.ConversationId, f.NormalizedName }).IsUnique()
            .HasFilter("\"ConversationId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

        b.ToTable(t => t.HasCheckConstraint(
            "CK_FileAssets_SingleRoom",
            "num_nonnulls(\"ChannelId\", \"ConversationId\") = 1"));

        b.HasOne(f => f.Channel).WithMany()
            .HasForeignKey(f => f.ChannelId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(f => f.Conversation).WithMany()
            .HasForeignKey(f => f.ConversationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FileVersionConfiguration : IEntityTypeConfiguration<FileVersion>
{
    public void Configure(EntityTypeBuilder<FileVersion> b)
    {
        b.Property(v => v.ObjectKey).HasMaxLength(512);
        b.Property(v => v.ContentType).HasMaxLength(255);
        b.Property(v => v.Sha256).HasMaxLength(64).IsFixedLength();
        b.Property(v => v.ScanStatus).IsEnumString();
        b.Property(v => v.ScanDetail).HasMaxLength(500);
        b.Property(v => v.PreviewStatus).IsEnumString();
        b.Property(v => v.ThumbnailKey).HasMaxLength(512);
        b.Property(v => v.PreviewKey).HasMaxLength(512);
        b.Property(v => v.UploadedByUserId).IsUserId();
        b.Property<NpgsqlTsVector>(MessageConfiguration.SearchVectorProperty)
            .HasComputedColumnSql("to_tsvector('english', coalesce(\"ExtractedText\", ''))", stored: true);
        b.HasIndex(MessageConfiguration.SearchVectorProperty).HasMethod("GIN");
        b.HasIndex(v => new { v.FileAssetId, v.VersionNo }).IsUnique();
        b.HasIndex(v => v.ObjectKey).IsUnique();
        b.HasOne(v => v.FileAsset).WithMany(f => f.Versions)
            .HasForeignKey(v => v.FileAssetId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ActivityItemConfiguration : IEntityTypeConfiguration<ActivityItem>
{
    public void Configure(EntityTypeBuilder<ActivityItem> b)
    {
        b.Property(a => a.UserId).IsUserId();
        b.Property(a => a.ActorUserId).IsUserId();
        b.Property(a => a.Type).IsEnumString();
        b.HasIndex(a => new { a.UserId, a.CreatedAt });
        b.HasIndex(a => a.UserId).HasFilter("\"ReadAt\" IS NULL").HasDatabaseName("IX_ActivityItems_UserId_Unread");
        b.HasIndex(a => a.ExpiresAt);

        b.HasOne<Workspace>().WithMany().HasForeignKey(a => a.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Channel>().WithMany().HasForeignKey(a => a.ChannelId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Conversation>().WithMany().HasForeignKey(a => a.ConversationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Message>().WithMany().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Property(a => a.Id).UseIdentityAlwaysColumn();
        b.Property(a => a.ActorUserId).IsUserId();
        b.Property(a => a.Action).HasMaxLength(64);
        b.Property(a => a.TargetType).HasMaxLength(32);
        b.Property(a => a.TargetId).HasMaxLength(64);
        b.Property(a => a.DataJson).HasColumnType("jsonb");
        b.Property(a => a.IpAddress).HasMaxLength(45);
        b.HasIndex(a => a.At);
        b.HasIndex(a => new { a.WorkspaceId, a.At });
        b.HasIndex(a => new { a.ActorUserId, a.At });
    }
}

internal sealed class UserSettingsConfiguration : IEntityTypeConfiguration<UserSettings>
{
    public void Configure(EntityTypeBuilder<UserSettings> b)
    {
        b.HasKey(s => s.UserId);
        b.Property(s => s.UserId).IsUserId();
        b.Property(s => s.PreferencesJson).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
    }
}

internal sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<PushSubscription>
{
    public void Configure(EntityTypeBuilder<PushSubscription> b)
    {
        b.Property(p => p.UserId).IsUserId();
        b.Property(p => p.Endpoint).HasMaxLength(1000);
        b.Property(p => p.P256dh).HasMaxLength(200);
        b.Property(p => p.Auth).HasMaxLength(100);
        b.HasIndex(p => p.Endpoint).IsUnique();
        b.HasIndex(p => p.UserId);
    }
}

internal sealed class ReadMarkerConfiguration : IEntityTypeConfiguration<ReadMarker>
{
    public void Configure(EntityTypeBuilder<ReadMarker> b)
    {
        b.Property(r => r.UserId).IsUserId();
        b.HasIndex(r => new { r.UserId, r.ChannelId }).IsUnique().HasFilter("\"ChannelId\" IS NOT NULL");
        b.HasIndex(r => new { r.UserId, r.ConversationId }).IsUnique().HasFilter("\"ConversationId\" IS NOT NULL");
        b.ToTable(t => t.HasCheckConstraint(
            "CK_ReadMarkers_SingleRoom",
            "num_nonnulls(\"ChannelId\", \"ConversationId\") = 1"));
        b.HasOne<Channel>().WithMany().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Conversation>().WithMany().HasForeignKey(r => r.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}
