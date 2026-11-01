namespace Mode.Domain.Entities;

public enum WorkspaceRole { Owner, Member, Guest }

public enum ChannelType { Standard, Private }

public enum ConversationType { Direct, Group }

public enum MessageChangeKind { Edit, Delete, Restore }

public enum MentionKind { User, Channel, Workspace }

public enum ScanStatus { Pending, Clean, Infected, Blocked, Failed }

public enum PreviewStatus { Pending, Ready, NotSupported, Failed }

public enum JobStatus { Pending, Running, Done, Failed }

public enum ActivityType { Mention, ChannelMention, Reply, Reaction, AddedToChannel, AddedToWorkspace, ChatMessage }
