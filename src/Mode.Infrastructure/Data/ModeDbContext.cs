using Microsoft.EntityFrameworkCore;
using Mode.Domain.Entities;

namespace Mode.Infrastructure.Data;

public sealed class ModeDbContext(DbContextOptions<ModeDbContext> options) : DbContext(options)
{
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<ReadMarker> ReadMarkers => Set<ReadMarker>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<ChannelMember> ChannelMembers => Set<ChannelMember>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageEdit> MessageEdits => Set<MessageEdit>();
    public DbSet<Reaction> Reactions => Set<Reaction>();
    public DbSet<Mention> Mentions => Set<Mention>();
    public DbSet<FileAsset> FileAssets => Set<FileAsset>();
    public DbSet<FileVersion> FileVersions => Set<FileVersion>();
    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<ActivityItem> ActivityItems => Set<ActivityItem>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ModeDbContext).Assembly);
}
