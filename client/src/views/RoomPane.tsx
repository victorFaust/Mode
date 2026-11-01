import { ArrowLeft, Bell, BellOff, ChevronDown, FileText, Hash, Lock, PanelLeftOpen, Pin, PinOff, Users } from "lucide-react";
import { useEffect, useId, useRef, useState } from "react";
import { useShallow } from "zustand/react/shallow";
import { ok, roomKey, type RoomRef, type User } from "../api/client";
import { useController, useMode } from "../app/context";
import { IconButton } from "../components/ui";
import { previewText } from "../lib/mentions";
import { displayName } from "../store/controller";
import { Composer, type ComposerHandle } from "./Composer";
import { MessageList } from "./MessageList";
import styles from "./views.module.css";

/** Members of a room, for mention suggestions and the members pane. */
export function useRoomMembers(room: RoomRef): { members: User[]; roles: Record<string, string>; reload: () => void } {
  const controller = useController();
  const conversation = useMode((s) => (room.type === "Conversation" ? s.conversations.find((c) => c.id === room.id) : undefined));
  const [channelMembers, setChannelMembers] = useState<{ members: User[]; roles: Record<string, string> }>({ members: [], roles: {} });
  const [version, setVersion] = useState(0);

  useEffect(() => {
    if (room.type !== "Channel") return;
    let cancelled = false;
    void ok(controller.api.GET("/api/v1/channels/{channelId}/members", { params: { path: { channelId: room.id } } })).then((list) => {
      if (cancelled) return;
      setChannelMembers({ members: list.map((m) => m.user), roles: Object.fromEntries(list.map((m) => [m.user.id, m.role])) });
      controller.rememberUsers(list.map((m) => m.user));
    }, () => undefined);
    return () => { cancelled = true; };
  }, [room.type, room.id, controller, version]);

  return room.type === "Channel"
    ? { ...channelMembers, reload: () => setVersion((v) => v + 1) }
    : { members: conversation?.members ?? [], roles: {}, reload: () => setVersion((v) => v + 1) };
}

export function useRoomInfo(room: RoomRef) {
  return useMode(useShallow((s) => {
    if (room.type === "Channel") {
      for (const [workspaceId, list] of Object.entries(s.channels)) {
        const channel = list.find((c) => c.id === room.id);
        if (channel) {
          const workspace = s.workspaces.find((w) => w.id === workspaceId);
          return {
            name: channel.name, subtitle: workspace?.name ?? "", isPrivate: channel.type === "Private", isChannel: true,
            canPost: channel.canPost, canManage: channel.canManage, archived: channel.isArchived, description: channel.description ?? null,
            direct: null as User | null,
          };
        }
      }
    }
    const conversation = s.conversations.find((c) => c.id === room.id);
    const others = conversation?.members.filter((m) => m.id !== s.me?.id) ?? [];
    return {
      name: conversation ? displayName(conversation, s.me?.id) : "Conversation",
      subtitle: conversation?.type === "Group" ? `${conversation.members.length} people` : (others[0]?.email ?? ""),
      isPrivate: false, isChannel: false, canPost: true, canManage: false, archived: false,
      description: null, direct: conversation?.type === "Direct" ? (others[0] ?? null) : null,
    };
  }));
}

/** The latest pin, with the full list one click away; selecting a pin scrolls to the message. */
function PinnedBar({ room, canPost }: { room: RoomRef; canPost: boolean }) {
  const controller = useController();
  const { pins, users } = useMode(useShallow((s) => ({ pins: s.pins[roomKey(room)], users: s.users })));
  const [open, setOpen] = useState(false);
  const listId = useId();
  if (!pins?.length) return null;

  const text = (pin: (typeof pins)[number]) =>
    previewText(pin.message.bodyMarkdown || pin.message.forwarded?.bodyMarkdown || "", users) || "(no text)";
  const go = (pin: (typeof pins)[number]) => {
    setOpen(false);
    void controller.revealMessage(room, pin.message.id, pin.message);
  };

  return (
    <div className={styles.pinnedBar} aria-label="Pinned messages" role="region">
      <div className={styles.pinnedRow}>
        <Pin size={14} aria-hidden />
        <button type="button" className={styles.pinnedLatest} onClick={() => go(pins[0])}>
          <span className="m-visually-hidden">Pinned: </span>{text(pins[0])}
        </button>
        <button type="button" className={styles.pinnedToggle} aria-expanded={open} aria-controls={listId} onClick={() => setOpen(!open)}>
          {pins.length} pinned <ChevronDown size={14} aria-hidden />
        </button>
      </div>
      {open && (
        <ul id={listId} className={styles.pinnedList}>
          {pins.map((pin) => (
            <li key={pin.message.id} className={styles.pinnedItem}>
              <button type="button" className={styles.pinnedItemText} onClick={() => go(pin)}>
                <strong>{pin.message.author.displayName}</strong> {text(pin)}
                <span className={styles.pinnedBy}>Pinned by {pin.pinnedBy.displayName}</span>
              </button>
              {canPost && (
                <IconButton label={`Unpin message from ${pin.message.author.displayName}`} size="small"
                  onClick={() => void controller.setPinned(pin.message, false).catch((e: Error) => controller.toastError(e.message))}>
                  <PinOff size={14} aria-hidden />
                </IconButton>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function RoomPane({ room }: { room: RoomRef }) {
  const controller = useController();
  const info = useRoomInfo(room);
  const { members } = useRoomMembers(room);
  const { muted, side, typingMap, users } = useMode(useShallow((s) => ({
    muted: s.unread[roomKey(room)]?.isMuted ?? false,
    side: s.side?.kind ?? null,
    typingMap: s.typing[roomKey(room)],
    users: s.users,
  })));
  const now = Date.now();
  const typingNames = Object.entries(typingMap ?? {}).filter(([, until]) => until > now)
    .map(([id]) => users[id]?.displayName ?? "Someone");
  const directStatus = useMode((s) => (info.direct ? s.presence[info.direct.id] : undefined));
  const composerRef = useRef<ComposerHandle>(null);

  const subtitle = info.direct && directStatus?.message ? directStatus.message : info.subtitle;
  const typingText = typingNames.length === 0 ? "" : typingNames.length === 1 ? `${typingNames[0]} is typing…`
    : typingNames.length === 2 ? `${typingNames[0]} and ${typingNames[1]} are typing…` : "Several people are typing…";

  return (
    <section className={styles.pane} aria-label={info.name}
      onDragOver={(e) => { if (info.canPost && e.dataTransfer.types.includes("Files")) e.preventDefault(); }}
      onDrop={(e) => {
        if (!info.canPost || !e.dataTransfer.files.length) return;
        e.preventDefault();
        composerRef.current?.addFiles(Array.from(e.dataTransfer.files));
      }}>
      <header className={styles.paneHeader}>
        <IconButton label="Back" className={styles.backButton} onClick={() => controller.openRoom(null)}><ArrowLeft size={18} aria-hidden /></IconButton>
        <IconButton label="Show list" className={styles.listToggle} onClick={() => controller.setListOpen(true)}><PanelLeftOpen size={18} aria-hidden /></IconButton>
        <div className={styles.paneTitle}>
          <h2>
            {info.isChannel && (info.isPrivate ? <Lock size={16} aria-label="Private channel" /> : <Hash size={16} aria-hidden />)}
            {info.name}
          </h2>
          {subtitle && <span className={styles.paneSubtitle}>{subtitle}{info.description ? ` · ${info.description}` : ""}</span>}
        </div>
        <div className={styles.paneActions}>
          <IconButton label={muted ? "Unmute" : "Mute"} aria-pressed={muted}
            onClick={() => void controller.setMuted(room, !muted).catch((e: Error) => controller.toastError(e.message))}>
            {muted ? <BellOff size={18} aria-hidden /> : <Bell size={18} aria-hidden />}
          </IconButton>
          <IconButton label="Files" aria-pressed={side === "files"} onClick={() => controller.setSide(side === "files" ? null : { kind: "files" })}>
            <FileText size={18} aria-hidden />
          </IconButton>
          <IconButton label="Members" aria-pressed={side === "members"} onClick={() => controller.setSide(side === "members" ? null : { kind: "members" })}>
            <Users size={18} aria-hidden />
          </IconButton>
        </div>
      </header>

      <PinnedBar room={room} canPost={info.canPost} />

      <MessageList room={room} title={info.name} canPost={info.canPost} canManage={info.canManage} />

      <div className={styles.typing} aria-live="polite">{typingText}</div>
      {info.canPost ? (
        <Composer ref={composerRef} room={room} members={members} allowChannelMention={info.isChannel}
          placeholder={info.isChannel ? `Message #${info.name}` : `Message ${info.name}`} />
      ) : (
        <p className={styles.archived}>{info.archived ? "This channel is archived. You can read it but not post." : "You can read this channel but not post."}</p>
      )}
    </section>
  );
}
