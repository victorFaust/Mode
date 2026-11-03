using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mode.Domain.Entities;

namespace Mode.Infrastructure.Data.Configurations;

internal sealed class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> b)
    {
        b.HasKey(p => p.WorkspaceId);
        b.Property(p => p.UpdatedByUserId).IsUserId();
        b.ToTable(t => t.HasCheckConstraint("CK_RetentionPolicies_KeepDays", "\"KeepDays\" IS NULL OR \"KeepDays\" >= 1"));
        b.HasOne<Workspace>().WithOne().HasForeignKey<RetentionPolicy>(p => p.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class LegalHoldConfiguration : IEntityTypeConfiguration<LegalHold>
{
    public void Configure(EntityTypeBuilder<LegalHold> b)
    {
        b.Property(h => h.TargetType).IsEnumString();
        b.Property(h => h.TargetId).HasMaxLength(64);
        b.Property(h => h.Reason).HasMaxLength(500);
        b.Property(h => h.CreatedByUserId).IsUserId();
        b.Property(h => h.ReleasedByUserId).IsUserId();
        b.HasIndex(h => new { h.TargetType, h.TargetId }).HasFilter("\"ReleasedAt\" IS NULL").HasDatabaseName("IX_LegalHolds_Active");
    }
}

internal sealed class WebhookConfiguration : IEntityTypeConfiguration<Webhook>
{
    public void Configure(EntityTypeBuilder<Webhook> b)
    {
        b.Property(w => w.Name).HasMaxLength(80);
        b.Property(w => w.SecretHash).HasMaxLength(64);
        b.Property(w => w.CreatedByUserId).IsUserId();
        b.ToTable(t => t.HasCheckConstraint("CK_Webhooks_SingleRoom", "num_nonnulls(\"ChannelId\", \"ConversationId\") = 1"));
        b.HasOne<Channel>().WithMany().HasForeignKey(w => w.ChannelId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Conversation>().WithMany().HasForeignKey(w => w.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DlpSettingsConfiguration : IEntityTypeConfiguration<DlpSettings>
{
    public void Configure(EntityTypeBuilder<DlpSettings> b)
    {
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.UpdatedByUserId).IsUserId();
        b.ToTable(t => t.HasCheckConstraint("CK_DlpSettings_Single", "\"Id\" = 1"));
        b.OwnsMany(s => s.Patterns, p =>
        {
            p.ToJson();
            p.Property(x => x.Action).HasConversion<string>();
        });
    }
}

internal sealed class ComplianceExportConfiguration : IEntityTypeConfiguration<ComplianceExport>
{
    public void Configure(EntityTypeBuilder<ComplianceExport> b)
    {
        b.Property(e => e.RequestedByUserId).IsUserId();
        b.Property(e => e.FiltersJson).HasColumnType("jsonb");
        b.Property(e => e.Status).IsEnumString();
        b.Property(e => e.StorageKey).HasMaxLength(200);
        b.Property(e => e.ManifestSha256).HasMaxLength(64);
        b.Property(e => e.ManifestSignature).HasMaxLength(100);
        b.Property(e => e.Error).HasMaxLength(2000);
        b.HasIndex(e => e.CreatedAt);
    }
}
