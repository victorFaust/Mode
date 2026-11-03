import { createStore } from "zustand/vanilla";
import type {
  Channel, Conversation, Message, Pin, Presence, RoomRef, RoomUnread, User, Workspace,
} from "../api/client";

export type Section = "activity" | "chat" | "channels" | "files" | "admin";

export type SidePane =
  | { kind: "thread"; messageId: string }
  | { kind: "files" }
  | { kind: "members" };

export interface PendingSend {
  bodyMarkdown: string;
  parentMessageId: string | null;
  fileIds: string[];
  acknowledgeDlpWarnings?: boolean;
}

/** A message as shown: server state, or a local optimistic copy while sending. */
export type LocalMessage = Message & { pending?: { state: "sending" | "failed"; send: PendingSend; error?: string } };

/** Data-loss warning rules matched; the sender decides whether to send anyway. */
export interface DlpWarning {
  rules: string[];
  confirm: () => void;
}

export interface RoomMessages {
  items: LocalMessage[];
  loaded: boolean;
  loading: boolean;
  hasOlder: boolean;
  /** Read marker when the room was opened; drives the "New messages" divider. Null when nothing was unread. */
  lastReadOnOpen: number | null;
}

export interface Thread {
  items: LocalMessage[];
  loaded: boolean;
  /** Fetched with the thread, for parents outside the loaded page of the room. */
  parent?: Message;
}

export type ConnectionState = "connecting" | "connected" | "reconnecting" | "disconnected";

export interface Banner {
  id: string;
  title: string;
  body: string;
  room: RoomRef | null;
  messageId: string | null;
}

export interface ModeState {
  me: User | null;
  isGuest: boolean;
  isAdmin: boolean;
  dlpWarning: DlpWarning | null;
  workspaces: Workspace[];
  channels: Record<string, Channel[]>;
  conversations: Conversation[];
  bootstrapped: boolean;
  bootstrapError: string | null;
  unread: Record<string, RoomUnread>;
  activityUnread: number;
  rooms: Record<string, RoomMessages>;
  threads: Record<string, Thread>;
  /** Highest sequence seen per room, to spot missed events. */
  roomSequence: Record<string, number>;
  presence: Record<string, Presence>;
  typing: Record<string, Record<string, number>>;
  users: Record<string, User>;
  connection: ConnectionState;
  online: boolean;
  banners: Banner[];
  section: Section;
  room: RoomRef | null;
  side: SidePane | null;
  searchQuery: string | null;
  commandBarOpen: boolean;
  /** Desktop: list column shown. Tablet: list overlay shown. */
  listOpen: boolean;
  /** The message being edited in place, if any. */
  editingMessageId: string | null;
  /** Pinned messages per room, newest pin first; missing until loaded. */
  pins: Record<string, Pin[]>;
  /** A loaded top-level message the message list should scroll to and highlight. */
  reveal: string | null;
}

export const initialState = (): ModeState => ({
  me: null,
  isGuest: false,
  isAdmin: false,
  dlpWarning: null,
  workspaces: [],
  channels: {},
  conversations: [],
  bootstrapped: false,
  bootstrapError: null,
  unread: {},
  activityUnread: 0,
  rooms: {},
  threads: {},
  roomSequence: {},
  presence: {},
  typing: {},
  users: {},
  connection: "connecting",
  online: typeof navigator === "undefined" ? true : navigator.onLine,
  banners: [],
  section: "chat",
  room: null,
  side: null,
  searchQuery: null,
  commandBarOpen: false,
  listOpen: true,
  editingMessageId: null,
  pins: {},
  reveal: null,
});

export const createModeStore = (initial?: Partial<ModeState>) =>
  createStore<ModeState>()(() => ({ ...initialState(), ...initial }));

export type ModeStore = ReturnType<typeof createModeStore>;
