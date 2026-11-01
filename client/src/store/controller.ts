import {
  ok, roomKey, type Api, type Channel, type Conversation, type FileDto, type Message, type PresenceStatus, type RoomRef, type User,
} from "../api/client";
import { RealtimeService, type RealtimeHandlers } from "../realtime/realtime";
import { features } from "../app/features";
import { toWireMarkdown, type PickedMention } from "../lib/mentions";
import {
  applyDeleted, applyMessage, applyReactions, mentionsMe, patchRoom, roomOf, updateMessage,
} from "./mutations";
import type { Banner, LocalMessage, ModeState, ModeStore, PendingSend, Section, SidePane } from "./store";

const PAGE_SIZE = 50;
const TYPING_SEND_MS = 3000;
const TYPING_SHOW_MS = 6000;
/** Rooms not opened yet join "at the end": no replay, only new events. */
const NO_REPLAY = Number.MAX_SAFE_INTEGER;

export interface Platform {
  /** Shows a system notification when the tab is hidden; returns false if not allowed. */
  notify?(title: string, body: string, onClick: () => void): boolean;
  /** Service worker script for Web Push, served from the host page's origin. */
  serviceWorkerUrl?: string;
}

export function displayName(conversation: Conversation, meId: string | undefined): string {
  if (conversation.name) return conversation.name;
  const others = conversation.members.filter((m) => m.id !== meId);
  return (others.length ? others : conversation.members).map((m) => m.displayName).join(", ");
}

/** All behaviour behind the UI. Components read the store and call these methods. */
export class ModeController {
  readonly realtime: RealtimeService;
  private readonly atBottom = new Map<string, boolean>();
  private readonly readTimers = new Map<string, ReturnType<typeof setTimeout>>();
  private lastTypingSent = new Map<string, number>();
  private unreadRefresh: ReturnType<typeof setTimeout> | undefined;
  private readonly requestedUsers = new Set<string>();

  constructor(
    readonly api: Api,
    readonly store: ModeStore,
    getToken: () => Promise<string>,
    readonly platform: Platform = {},
  ) {
    this.realtime = new RealtimeService(api.baseUrl, getToken, this.handlers);
  }

  private get state(): ModeState {
    return this.store.getState();
  }

  private set(update: Partial<ModeState> | ((s: ModeState) => Partial<ModeState>)): void {
    this.store.setState(update);
  }

  // ---- Startup -------------------------------------------------------------------------------

  async bootstrap(): Promise<void> {
    try {
      const [me, workspaces, conversations, unread, activity] = await Promise.all([
        ok(this.api.GET("/api/v1/me")),
        ok(this.api.GET("/api/v1/workspaces")),
        ok(this.api.GET("/api/v1/conversations")),
        ok(this.api.GET("/api/v1/me/unread")),
        ok(this.api.GET("/api/v1/activity/unread-count")),
      ]);
      const channelLists = features.channels
        ? await Promise.all(workspaces.map((w) =>
          ok(this.api.GET("/api/v1/workspaces/{workspaceId}/channels", { params: { path: { workspaceId: w.id } } }))))
        : workspaces.map(() => []);

      const users: Record<string, User> = { [me.user.id]: me.user };
      conversations.forEach((c) => c.members.forEach((m) => (users[m.id] = m)));
      this.set({
        me: me.user,
        isGuest: me.isGuest,
        workspaces,
        channels: Object.fromEntries(workspaces.map((w, i) => [w.id, channelLists[i]])),
        conversations,
        unread: Object.fromEntries(unread.map((u) => [roomKey(u.room), u])),
        activityUnread: activity.unreadCount,
        users,
        bootstrapped: true,
        bootstrapError: null,
      });
      void this.realtime.start();
      void this.realtime.subscribePresence(Object.keys(users));
    } catch (error) {
      this.set({ bootstrapError: error instanceof Error ? error.message : "Mode could not load." });
    }
  }

  async stop(): Promise<void> {
    await this.realtime.stop();
  }

  setOnline(online: boolean): void {
    this.set({ online });
  }

  // ---- Realtime ------------------------------------------------------------------------------

  readonly handlers: RealtimeHandlers = {
    stateChanged: (connection) => this.set({ connection }),
    connected: () => this.rejoinAll(),
    messageCreated: ({ room, sequence, message }) => {
      this.checkSequence(room, sequence);
      this.set((s) => applyMessage(s, message));
      this.countUnread(room, message);
      this.clearTyping(room, message.author.id);
    },
    messageEdited: ({ room, sequence, message }) => {
      this.checkSequence(room, sequence);
      this.set((s) => applyMessage(s, message));
    },
    messageDeleted: ({ room, sequence, messageId }) => {
      this.checkSequence(room, sequence);
      this.set((s) => applyDeleted(s, room, messageId, sequence));
    },
    reactionChanged: ({ room, sequence, messageId, reactions }) => {
      this.checkSequence(room, sequence);
      this.set((s) => applyReactions(s, room, messageId, reactions, sequence));
    },
    typing: ({ room, userId }) => {
      const key = roomKey(room);
      this.set((s) => ({ typing: { ...s.typing, [key]: { ...s.typing[key], [userId]: Date.now() + TYPING_SHOW_MS } } }));
      setTimeout(() => this.expireTyping(room), TYPING_SHOW_MS + 100);
      void this.ensureUsers([userId]);
    },
    activityCreated: ({ unreadCount }) => this.set({ activityUnread: unreadCount }),
    activityRead: ({ unreadCount }) => this.set({ activityUnread: unreadCount }),
    notification: (e) => {
      if (e.channelId && !features.channels) return;
      const room: RoomRef | null = e.channelId ? { type: "Channel", id: e.channelId }
        : e.conversationId ? { type: "Conversation", id: e.conversationId } : null;
      const banner: Banner = { id: e.activityId, title: e.title, body: e.body, room, messageId: e.messageId };
      const hidden = typeof document !== "undefined" && document.visibilityState === "hidden";
      if (hidden && this.platform.notify?.(e.title, e.body, () => this.openBanner(banner))) return;
      this.showBanner(banner);
    },
    readMarkerChanged: () => this.refreshUnreadSoon(),
    presenceChanged: (presence) => this.set((s) => ({ presence: { ...s.presence, [presence.userId]: presence } })),
    roomAccessRevoked: ({ room }) => this.removeRoom(room, "You no longer have access to this conversation."),
    fileUpdated: ({ room, file }) => {
      this.set((s) => {
        const key = roomKey(room);
        const current = s.rooms[key];
        if (!current) return {};
        const items = current.items.map((m) => m.files.some((f) => f.fileId === file.id)
          ? {
              ...m,
              files: m.files.map((f) => f.fileId === file.id
                ? { ...f, scanStatus: file.current.scanStatus, previewStatus: file.current.previewStatus }
                : f),
            }
          : m);
        return { rooms: { ...s.rooms, [key]: { ...current, items } } };
      });
      window.dispatchEvent(new CustomEvent("mode:file-updated", { detail: file }));
    },
  };

  private allRooms(): RoomRef[] {
    const s = this.state;
    return [
      ...Object.values(s.channels).flat().filter((c) => !c.isArchived || roomKey({ type: "Channel", id: c.id }) in s.rooms)
        .map((c): RoomRef => ({ type: "Channel", id: c.id })),
      ...s.conversations.map((c): RoomRef => ({ type: "Conversation", id: c.id })),
    ];
  }

  /** Joins every room; rooms already on screen replay what was missed while disconnected. */
  private async rejoinAll(): Promise<void> {
    const s = this.state;
    const requests = this.allRooms().map((room) => {
      const key = roomKey(room);
      return { room, lastSequence: s.rooms[key]?.loaded ? (s.roomSequence[key] ?? 0) : NO_REPLAY };
    });
    const results = await this.realtime.joinRooms(requests);
    for (const result of results) {
      const key = roomKey(result.room);
      if (!result.granted) continue;
      if (result.resyncRequired) {
        this.set((st) => patchRoom(st, result.room, (r) => ({ ...r, loaded: false, items: [] })));
        if (this.isOpen(result.room)) void this.loadRoom(result.room);
        continue;
      }
      this.set((st) => {
        let next = st;
        for (const message of result.missed) next = { ...next, ...applyMessage(next, message) };
        return { ...next, roomSequence: { ...next.roomSequence, [key]: Math.max(next.roomSequence[key] ?? 0, result.currentSequence) } };
      });
    }
    this.refreshUnreadSoon(0);
    const activity = await ok(this.api.GET("/api/v1/activity/unread-count")).catch(() => null);
    if (activity) this.set({ activityUnread: activity.unreadCount });
  }

  /** A gap in sequence numbers means an event was missed: ask the server for the changes since the last one. */
  private checkSequence(room: RoomRef, sequence: number): void {
    const key = roomKey(room);
    const last = this.state.roomSequence[key];
    if (last === undefined || sequence <= last + 1 || !this.state.rooms[key]?.loaded) return;
    void this.realtime.joinRooms([{ room, lastSequence: last }]).then((results) => {
      for (const result of results) {
        if (result.resyncRequired) {
          void this.loadRoom(room, true);
          return;
        }
        this.set((st) => {
          let next = st;
          for (const message of result.missed) next = { ...next, ...applyMessage(next, message) };
          return next;
        });
      }
    });
  }

  private countUnread(room: RoomRef, message: Message): void {
    const me = this.state.me?.id;
    if (message.author.id === me) return;
    if (this.isOpen(room) && this.atBottom.get(roomKey(room)) && document.visibilityState === "visible") {
      this.scheduleMarkRead(room);
      return;
    }
    const key = roomKey(room);
    this.set((s) => {
      const current = s.unread[key] ?? {
        room, unreadCount: 0, mentionCount: 0, lastReadSequence: 0, firstUnreadSequence: message.sequence, isMuted: false,
      };
      return {
        unread: {
          ...s.unread,
          [key]: {
            ...current,
            unreadCount: current.unreadCount + 1,
            mentionCount: current.mentionCount + (mentionsMe(message, me) ? 1 : 0),
            firstUnreadSequence: current.firstUnreadSequence ?? message.sequence,
          },
        },
      };
    });
  }

  refreshUnreadSoon(delay = 300): void {
    clearTimeout(this.unreadRefresh);
    this.unreadRefresh = setTimeout(async () => {
      const unread = await ok(this.api.GET("/api/v1/me/unread")).catch(() => null);
      if (unread) this.set({ unread: Object.fromEntries(unread.map((u) => [roomKey(u.room), u])) });
    }, delay);
  }

  private clearTyping(room: RoomRef, userId: string): void {
    const key = roomKey(room);
    this.set((s) => {
      if (!s.typing[key]?.[userId]) return {};
      const { [userId]: _, ...rest } = s.typing[key];
      return { typing: { ...s.typing, [key]: rest } };
    });
  }

  private expireTyping(room: RoomRef): void {
    const key = roomKey(room);
    const now = Date.now();
    this.set((s) => ({
      typing: { ...s.typing, [key]: Object.fromEntries(Object.entries(s.typing[key] ?? {}).filter(([, until]) => until > now)) },
    }));
  }

  // ---- Navigation ----------------------------------------------------------------------------

  isOpen(room: RoomRef): boolean {
    const open = this.state.room;
    return open !== null && open.type === room.type && open.id === room.id;
  }

  openSection(section: Section): void {
    this.set({ section, searchQuery: null });
  }

  openRoom(room: RoomRef | null): void {
    this.set({
      room,
      side: null,
      searchQuery: null,
      section: room === null ? this.state.section : room.type === "Channel" ? "channels" : "chat",
    });
    if (!room) return;
    const current = roomOf(this.state, room);
    if (!current.loaded && !current.loading) void this.loadRoom(room);
    else this.set((s) => patchRoom(s, room, (r) => ({ ...r, lastReadOnOpen: s.unread[roomKey(room)]?.lastReadSequence ?? null })));
    const conversation = room.type === "Conversation" ? this.state.conversations.find((c) => c.id === room.id) : undefined;
    if (conversation) void this.realtime.subscribePresence(conversation.members.map((m) => m.id));
  }

  setSide(side: SidePane | null): void {
    this.set({ side });
    if (side?.kind === "thread") void this.loadThread(side.messageId);
  }

  openCommandBar(open: boolean): void {
    this.set({ commandBarOpen: open });
  }

  setListOpen(listOpen: boolean): void {
    this.set({ listOpen });
  }

  search(query: string): void {
    this.set({ searchQuery: query.trim() || null, commandBarOpen: false });
  }

  showBanner(banner: Banner): void {
    this.set((s) => ({ banners: [...s.banners.filter((b) => b.id !== banner.id), banner].slice(-3) }));
  }

  dismissBanner(id: string): void {
    this.set((s) => ({ banners: s.banners.filter((b) => b.id !== id) }));
  }

  openBanner(banner: Banner): void {
    this.dismissBanner(banner.id);
    if (banner.room) {
      this.openRoom(banner.room);
      if (banner.messageId) void this.revealMessage(banner.room, banner.messageId);
    } else {
      this.openSection("activity");
    }
  }

  /** Opens a message: top-level messages are already in the room; replies open their thread. */
  async revealMessage(room: RoomRef, messageId: string): Promise<void> {
    if (!this.isOpen(room)) this.openRoom(room);
    const message = await ok(this.api.GET("/api/v1/messages/{messageId}", { params: { path: { messageId } } })).catch(() => null);
    if (message?.parentMessageId) this.setSide({ kind: "thread", messageId: message.parentMessageId });
    else if (message) window.dispatchEvent(new CustomEvent("mode:reveal", { detail: message.id }));
  }

  private removeRoom(room: RoomRef, notice: string): void {
    const key = roomKey(room);
    this.set((s) => {
      const { [key]: _, ...rooms } = s.rooms;
      const { [key]: __, ...unread } = s.unread;
      return {
        rooms,
        unread,
        conversations: room.type === "Conversation" ? s.conversations.filter((c) => c.id !== room.id) : s.conversations,
        channels: room.type === "Channel"
          ? Object.fromEntries(Object.entries(s.channels).map(([w, list]) => [w, list.filter((c) => c.id !== room.id)]))
          : s.channels,
      };
    });
    if (this.isOpen(room)) {
      this.set({ room: null, side: null });
      this.showBanner({ id: `revoked-${key}`, title: "Access removed", body: notice, room: null, messageId: null });
    }
  }

  // ---- Messages ------------------------------------------------------------------------------

  async loadRoom(room: RoomRef, force = false): Promise<void> {
    const key = roomKey(room);
    if (!force && this.state.rooms[key]?.loading) return;
    this.set((s) => patchRoom(s, room, (r) => ({ ...r, loading: true })));
    try {
      const page = await this.fetchPage(room, null);
      const unread = this.state.unread[key];
      let items = page.items.slice().reverse() as LocalMessage[];
      let nextBefore = page.nextBefore ?? null;

      // Make sure the first unread message is loaded so the "New messages" marker can be shown.
      for (let i = 0; unread?.firstUnreadSequence && nextBefore && items[0]?.sequence > unread.firstUnreadSequence && i < 3; i++) {
        const older = await this.fetchPage(room, nextBefore);
        items = [...older.items.slice().reverse(), ...items];
        nextBefore = older.nextBefore ?? null;
      }

      this.set((s) => ({
        ...patchRoom(s, room, (r) => ({
          items: [...items, ...r.items.filter((m) => m.pending)],
          loaded: true,
          loading: false,
          hasOlder: nextBefore !== null,
          lastReadOnOpen: unread ? unread.lastReadSequence : null,
        })),
        roomSequence: { ...s.roomSequence, [key]: Math.max(s.roomSequence[key] ?? 0, ...items.map((m) => m.updatedSequence)) },
        users: { ...s.users, ...Object.fromEntries(items.map((m) => [m.author.id, m.author])) },
      }));
      void this.realtime.subscribePresence([...new Set(items.map((m) => m.author.id))]);
      void this.ensureMentionedUsers(items);
    } catch (error) {
      this.set((s) => patchRoom(s, room, (r) => ({ ...r, loading: false })));
      if (error instanceof Error) this.toastError(error.message);
    }
  }

  async loadOlder(room: RoomRef): Promise<void> {
    const current = roomOf(this.state, room);
    const oldest = current.items.find((m) => !m.pending);
    if (current.loading || !current.hasOlder || !oldest) return;
    this.set((s) => patchRoom(s, room, (r) => ({ ...r, loading: true })));
    try {
      const page = await this.fetchPage(room, oldest.sequence);
      const older = page.items.slice().reverse();
      this.set((s) => ({
        ...patchRoom(s, room, (r) => ({ ...r, items: [...older, ...r.items], loading: false, hasOlder: page.nextBefore != null })),
        users: { ...s.users, ...Object.fromEntries(older.map((m) => [m.author.id, m.author])) },
      }));
      void this.ensureMentionedUsers(older);
    } catch {
      this.set((s) => patchRoom(s, room, (r) => ({ ...r, loading: false })));
    }
  }

  private fetchPage(room: RoomRef, before: number | null) {
    const query = { limit: PAGE_SIZE, ...(before ? { before } : {}) };
    return room.type === "Channel"
      ? ok(this.api.GET("/api/v1/channels/{channelId}/messages", { params: { path: { channelId: room.id }, query } }))
      : ok(this.api.GET("/api/v1/conversations/{conversationId}/messages", { params: { path: { conversationId: room.id }, query } }));
  }

  async loadThread(parentId: string): Promise<void> {
    try {
      const [parent, replies] = await Promise.all([
        ok(this.api.GET("/api/v1/messages/{messageId}", { params: { path: { messageId: parentId } } })),
        ok(this.api.GET("/api/v1/messages/{messageId}/replies", { params: { path: { messageId: parentId }, query: { limit: 100 } } })),
      ]);
      this.set((s) => ({
        ...updateMessage(s, parent.room, parent.id, () => parent),
        threads: {
          ...s.threads,
          [parentId]: { loaded: true, parent, items: [...replies, ...(s.threads[parentId]?.items.filter((m) => m.pending) ?? [])] },
        },
        users: { ...s.users, ...Object.fromEntries([parent, ...replies].map((m) => [m.author.id, m.author])) },
      }));
      void this.ensureMentionedUsers([parent, ...replies]);
    } catch (error) {
      if (error instanceof Error) this.toastError(error.message);
    }
  }

  async send(room: RoomRef, text: string, picked: PickedMention[], fileIds: string[], parentMessageId: string | null): Promise<void> {
    const me = this.state.me;
    if (!me) return;
    const send: PendingSend = { bodyMarkdown: toWireMarkdown(text, picked), parentMessageId, fileIds };
    const local: LocalMessage = {
      id: crypto.randomUUID(),
      room,
      sequence: 0,
      updatedSequence: 0,
      parentMessageId,
      author: me,
      bodyMarkdown: send.bodyMarkdown,
      createdAt: new Date().toISOString(),
      isDeleted: false,
      deletedByAuthor: false,
      replyCount: 0,
      reactions: [],
      mentions: [],
      files: [],
      pending: { state: "sending", send },
    };
    this.set((s) => applyMessage(s, local));
    await this.deliver(local);
  }

  async retry(message: LocalMessage): Promise<void> {
    if (!message.pending) return;
    const sending: LocalMessage = { ...message, pending: { ...message.pending, state: "sending" } };
    this.set((s) => applyMessage(s, sending));
    await this.deliver(sending);
  }

  discard(message: LocalMessage): void {
    this.set((s) => {
      const key = roomKey(message.room);
      const room = s.rooms[key];
      const parent = message.parentMessageId;
      return {
        rooms: room ? { ...s.rooms, [key]: { ...room, items: room.items.filter((m) => m.id !== message.id) } } : s.rooms,
        threads: parent && s.threads[parent]
          ? { ...s.threads, [parent]: { ...s.threads[parent], items: s.threads[parent].items.filter((m) => m.id !== message.id) } }
          : s.threads,
      };
    });
  }

  /** Sends with the client-generated id, so a retry after a lost response never duplicates the message. */
  private async deliver(local: LocalMessage): Promise<void> {
    const { send } = local.pending!;
    const body = { id: local.id, bodyMarkdown: send.bodyMarkdown, parentMessageId: send.parentMessageId, fileIds: send.fileIds };
    try {
      const saved = local.room.type === "Channel"
        ? await ok(this.api.POST("/api/v1/channels/{channelId}/messages", { params: { path: { channelId: local.room.id } }, body }))
        : await ok(this.api.POST("/api/v1/conversations/{conversationId}/messages", { params: { path: { conversationId: local.room.id } }, body }));
      this.set((s) => applyMessage(s, saved));
      this.scheduleMarkRead(local.room);
    } catch {
      this.set((s) => applyMessage(s, { ...local, pending: { state: "failed", send } }));
    }
  }

  async edit(message: Message, text: string, picked: PickedMention[]): Promise<void> {
    const saved = await ok(this.api.PATCH("/api/v1/messages/{messageId}", {
      params: { path: { messageId: message.id } }, body: { bodyMarkdown: toWireMarkdown(text, picked) },
    }));
    this.set((s) => applyMessage(s, saved));
    this.set({ editingMessageId: null });
  }

  async remove(message: Message): Promise<void> {
    await ok(this.api.DELETE("/api/v1/messages/{messageId}", { params: { path: { messageId: message.id } } }));
    this.set((s) => applyDeleted(s, message.room, message.id, message.updatedSequence, message.author.id === s.me?.id));
  }

  /** Undo for your own delete. */
  async restore(message: Message): Promise<void> {
    const restored = await ok(this.api.POST("/api/v1/messages/{messageId}/restore", { params: { path: { messageId: message.id } } }));
    this.set((s) => applyMessage(s, restored));
  }

  startEdit(messageId: string | null): void {
    this.set({ editingMessageId: messageId });
  }

  /** Up arrow in an empty composer edits your most recent message there, as in Teams. */
  editLastOwn(room: RoomRef, parentMessageId: string | null): void {
    const s = this.state;
    const items = parentMessageId ? s.threads[parentMessageId]?.items ?? [] : roomOf(s, room).items;
    const last = [...items].reverse().find((m) => m.author.id === s.me?.id && !m.isDeleted && !m.pending);
    if (last) this.startEdit(last.id);
  }

  async toggleReaction(message: Message, emoji: string): Promise<void> {
    const me = this.state.me?.id ?? "";
    const mine = message.reactions.some((r) => r.emoji === emoji && r.userIds.includes(me));
    const params = { params: { path: { messageId: message.id, emoji } } };
    const reactions = mine
      ? await ok(this.api.DELETE("/api/v1/messages/{messageId}/reactions/{emoji}", params))
      : await ok(this.api.PUT("/api/v1/messages/{messageId}/reactions/{emoji}", params));
    this.set((s) => updateMessage(s, message.room, message.id, (m) => ({ ...m, reactions })));
  }

  /** Called by the message list as the user scrolls. */
  setAtBottom(room: RoomRef, atBottom: boolean): void {
    this.atBottom.set(roomKey(room), atBottom);
    if (atBottom && this.state.unread[roomKey(room)]) this.scheduleMarkRead(room);
  }

  scheduleMarkRead(room: RoomRef): void {
    const key = roomKey(room);
    clearTimeout(this.readTimers.get(key));
    this.readTimers.set(key, setTimeout(() => void this.markRead(room), 500));
  }

  async markRead(room: RoomRef): Promise<void> {
    const key = roomKey(room);
    const sequence = this.state.roomSequence[key];
    if (!sequence) return;
    this.set((s) => {
      const { [key]: current, ...rest } = s.unread;
      return { unread: current?.isMuted ? { ...rest, [key]: { ...current, unreadCount: 0, mentionCount: 0 } } : rest };
    });
    const body = { sequence };
    await (room.type === "Channel"
      ? this.api.PUT("/api/v1/channels/{channelId}/read", { params: { path: { channelId: room.id } }, body })
      : this.api.PUT("/api/v1/conversations/{conversationId}/read", { params: { path: { conversationId: room.id } }, body }));
  }

  async setMuted(room: RoomRef, muted: boolean): Promise<void> {
    const body = { muted };
    await ok(room.type === "Channel"
      ? this.api.PUT("/api/v1/channels/{channelId}/mute", { params: { path: { channelId: room.id } }, body })
      : this.api.PUT("/api/v1/conversations/{conversationId}/mute", { params: { path: { conversationId: room.id } }, body }));
    const key = roomKey(room);
    this.set((s) => ({
      unread: {
        ...s.unread,
        [key]: { ...(s.unread[key] ?? { room, unreadCount: 0, mentionCount: 0, lastReadSequence: 0 }), isMuted: muted },
      },
    }));
  }

  typing(room: RoomRef, parentMessageId: string | null): void {
    const key = `${roomKey(room)}:${parentMessageId ?? ""}`;
    const now = Date.now();
    if (now - (this.lastTypingSent.get(key) ?? 0) < TYPING_SEND_MS) return;
    this.lastTypingSent.set(key, now);
    void this.realtime.typing(room, parentMessageId).catch(() => undefined);
  }

  // ---- People --------------------------------------------------------------------------------

  /** Loads names for people referenced in mentions; failures (e.g. guests) leave the raw id. */
  async ensureUsers(ids: string[]): Promise<void> {
    const missing = ids.filter((id) => !this.state.users[id] && !this.requestedUsers.has(id));
    missing.forEach((id) => this.requestedUsers.add(id));
    const found = await Promise.all(missing.map((userId) =>
      ok(this.api.GET("/api/v1/users/{userId}", { params: { path: { userId } } })).catch(() => null)));
    const users = found.filter((u): u is User => u !== null);
    if (users.length) this.set((s) => ({ users: { ...s.users, ...Object.fromEntries(users.map((u) => [u.id, u])) } }));
  }

  private ensureMentionedUsers(messages: Message[]): Promise<void> {
    return this.ensureUsers([...new Set(messages.flatMap((m) => m.mentions.map((x) => x.userId).filter((x): x is string => !!x)))]);
  }

  rememberUsers(users: User[]): void {
    this.set((s) => ({ users: { ...s.users, ...Object.fromEntries(users.map((u) => [u.id, u])) } }));
    void this.realtime.subscribePresence(users.map((u) => u.id));
  }

  async setPresence(status: PresenceStatus, message: string | null): Promise<void> {
    const presence = await ok(this.api.PUT("/api/v1/me/presence", { body: { status, message } }));
    this.set((s) => ({ presence: { ...s.presence, [presence.userId]: presence } }));
  }

  // ---- Rooms ---------------------------------------------------------------------------------

  async createWorkspace(name: string): Promise<void> {
    const workspace = await ok(this.api.POST("/api/v1/workspaces", { body: { name } }));
    const channels = await ok(this.api.GET("/api/v1/workspaces/{workspaceId}/channels", { params: { path: { workspaceId: workspace.id } } }));
    this.set((s) => ({ workspaces: [...s.workspaces, workspace], channels: { ...s.channels, [workspace.id]: channels } }));
    await this.joinNew(channels.map((c) => ({ type: "Channel", id: c.id })));
    if (channels[0]) this.openRoom({ type: "Channel", id: channels[0].id });
  }

  async createChannel(workspaceId: string, name: string, type: Channel["type"], description: string | null): Promise<void> {
    const channel = await ok(this.api.POST("/api/v1/workspaces/{workspaceId}/channels", {
      params: { path: { workspaceId } }, body: { name, type, description },
    }));
    this.set((s) => ({ channels: { ...s.channels, [workspaceId]: [...(s.channels[workspaceId] ?? []), channel] } }));
    await this.joinNew([{ type: "Channel", id: channel.id }]);
    this.openRoom({ type: "Channel", id: channel.id });
  }

  async startConversation(userIds: string[], name: string | null): Promise<void> {
    const body = userIds.length === 1 && !name
      ? { type: "Direct" as const, userId: userIds[0] }
      : { type: "Group" as const, userIds, name };
    const conversation = await ok(this.api.POST("/api/v1/conversations", { body }));
    this.set((s) => ({
      conversations: [conversation, ...s.conversations.filter((c) => c.id !== conversation.id)],
    }));
    this.rememberUsers(conversation.members);
    await this.joinNew([{ type: "Conversation", id: conversation.id }]);
    this.openRoom({ type: "Conversation", id: conversation.id });
  }

  async bootstrapConversations(): Promise<void> {
    const conversations = await ok(this.api.GET("/api/v1/conversations"));
    this.set({ conversations });
    this.rememberUsers(conversations.flatMap((c) => c.members));
  }

  private async joinNew(rooms: RoomRef[]): Promise<void> {
    const results = await this.realtime.joinRooms(rooms.map((room) => ({ room, lastSequence: NO_REPLAY })));
    this.set((s) => ({
      roomSequence: { ...s.roomSequence, ...Object.fromEntries(results.map((r) => [roomKey(r.room), r.currentSequence])) },
    }));
  }

  // ---- Files ---------------------------------------------------------------------------------

  async uploadFile(room: RoomRef, file: File, onProgress: (fraction: number) => void, signal?: AbortSignal): Promise<FileDto> {
    const started = await ok(this.api.POST("/api/v1/files/uploads", { body: { room, name: file.name, size: file.size } }));
    await this.api.upload(started.uploadUrl, file, onProgress, signal);
    return ok(this.api.POST("/api/v1/files/uploads/{uploadId}/complete", { params: { path: { uploadId: started.uploadId } } }));
  }

  async fileLink(fileId: string, variant?: "thumbnail" | "preview"): Promise<string> {
    const link = await ok(this.api.GET("/api/v1/files/{fileId}/link", {
      params: { path: { fileId }, query: variant ? { variant } : {} },
    }));
    return this.api.baseUrl + link.url;
  }

  toastError(message: string): void {
    this.showBanner({ id: `error-${Date.now()}`, title: "Something went wrong", body: message, room: null, messageId: null });
  }
}
