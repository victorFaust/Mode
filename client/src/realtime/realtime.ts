import {
  HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel, type IRetryPolicy,
} from "@microsoft/signalr";
import type { Conversation, FileDto, Message, Presence, Reaction, RoomRef } from "../api/client";

export interface JoinResult {
  room: RoomRef;
  granted: boolean;
  currentSequence: number;
  missed: Message[];
  resyncRequired: boolean;
}

export interface RealtimeHandlers {
  messageCreated(e: { room: RoomRef; sequence: number; message: Message }): void;
  messageEdited(e: { room: RoomRef; sequence: number; message: Message }): void;
  messageDeleted(e: { room: RoomRef; sequence: number; messageId: string; deletedAt: string }): void;
  reactionChanged(e: { room: RoomRef; sequence: number; messageId: string; reactions: Reaction[] }): void;
  typing(e: { room: RoomRef; userId: string; parentMessageId: string | null }): void;
  activityCreated(e: { unreadCount: number }): void;
  activityRead(e: { activityIds: string[]; unreadCount: number }): void;
  notification(e: {
    activityId: string; type: string; title: string; body: string;
    channelId: string | null; conversationId: string | null; messageId: string | null;
  }): void;
  readMarkerChanged(e: { room: RoomRef; lastReadSequence: number }): void;
  conversationSettingsChanged(e: Conversation): void;
  presenceChanged(e: Presence): void;
  roomAccessRevoked(e: { room: RoomRef }): void;
  fileUpdated(e: { room: RoomRef; file: FileDto }): void;
  stateChanged(state: "connecting" | "connected" | "reconnecting" | "disconnected"): void;
  /** Called after every (re)connect so the app can rejoin rooms and catch up. */
  connected(): Promise<void>;
}

/** Keeps retrying forever with backoff; the banner tells the user what is happening. */
const retryForever: IRetryPolicy = {
  nextRetryDelayInMilliseconds: ({ previousRetryCount }) => Math.min(30_000, [0, 1000, 3000, 5000, 10_000][previousRetryCount] ?? 15_000),
};

const MAX_ROOMS_PER_JOIN = 200;
const MAX_PRESENCE_PER_CALL = 500;
const HEARTBEAT_MS = 60_000;

/** The one SignalR connection; events are handed to the store through the handlers. */
export class RealtimeService {
  private readonly connection: HubConnection;
  private readonly presenceIds = new Set<string>();
  private heartbeat: ReturnType<typeof setInterval> | undefined;
  private active = true;
  private stopped = false;

  constructor(baseUrl: string, getToken: () => Promise<string>, private readonly handlers: RealtimeHandlers) {
    this.connection = new HubConnectionBuilder()
      .withUrl(new URL(`${baseUrl}/hubs/mode`, window.location.href).toString(), {
        accessTokenFactory: getToken,
        withCredentials: false,
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect(retryForever)
      .configureLogging(LogLevel.Warning)
      .build();

    const on = <K extends keyof RealtimeHandlers>(name: K) =>
      this.connection.on(name, (payload: never) => (handlers[name] as (p: never) => void)(payload));
    on("messageCreated");
    on("messageEdited");
    on("messageDeleted");
    on("reactionChanged");
    on("typing");
    on("activityCreated");
    on("activityRead");
    on("notification");
    on("readMarkerChanged");
    on("conversationSettingsChanged");
    on("presenceChanged");
    on("roomAccessRevoked");
    on("fileUpdated");

    this.connection.onreconnecting(() => handlers.stateChanged("reconnecting"));
    this.connection.onreconnected(async () => {
      handlers.stateChanged("connected");
      await this.afterConnect();
    });
    this.connection.onclose(() => {
      if (this.stopped) return;
      handlers.stateChanged("disconnected");
      setTimeout(() => void this.start(), 15_000);
    });
  }

  async start(): Promise<void> {
    if (this.stopped || this.connection.state !== HubConnectionState.Disconnected) return;
    this.handlers.stateChanged("connecting");
    try {
      await this.connection.start();
      this.handlers.stateChanged("connected");
      await this.afterConnect();
    } catch {
      this.handlers.stateChanged("disconnected");
      setTimeout(() => void this.start(), 5_000);
    }
    this.heartbeat ??= setInterval(() => void this.sendHeartbeat(), HEARTBEAT_MS);
  }

  async stop(): Promise<void> {
    this.stopped = true;
    clearInterval(this.heartbeat);
    await this.connection.stop();
  }

  get isConnected(): boolean {
    return this.connection.state === HubConnectionState.Connected;
  }

  /** Marks the user as active since the last heartbeat (keyboard or pointer use). */
  noteActivity(): void {
    this.active = true;
  }

  async joinRooms(rooms: { room: RoomRef; lastSequence: number }[]): Promise<JoinResult[]> {
    if (!this.isConnected) return [];
    const results: JoinResult[] = [];
    for (let i = 0; i < rooms.length; i += MAX_ROOMS_PER_JOIN)
      results.push(...await this.connection.invoke<JoinResult[]>("JoinRooms", rooms.slice(i, i + MAX_ROOMS_PER_JOIN)));
    return results;
  }

  async leaveRoom(room: RoomRef): Promise<void> {
    if (this.isConnected) await this.connection.invoke("LeaveRoom", room);
  }

  async typing(room: RoomRef, parentMessageId: string | null): Promise<void> {
    if (this.isConnected) await this.connection.send("Typing", room, parentMessageId);
  }

  /** Subscribes once per person; the server leaves out people the user may not see. */
  async subscribePresence(userIds: string[]): Promise<Presence[]> {
    const fresh = userIds.filter((id) => !this.presenceIds.has(id));
    fresh.forEach((id) => this.presenceIds.add(id));
    if (!this.isConnected || fresh.length === 0) return [];
    return this.invokePresence(fresh);
  }

  private async invokePresence(ids: string[]): Promise<Presence[]> {
    const results: Presence[] = [];
    for (let i = 0; i < ids.length; i += MAX_PRESENCE_PER_CALL)
      results.push(...await this.connection.invoke<Presence[]>("SubscribePresence", ids.slice(i, i + MAX_PRESENCE_PER_CALL)));
    results.forEach((p) => this.handlers.presenceChanged(p));
    return results;
  }

  private async afterConnect(): Promise<void> {
    if (this.presenceIds.size > 0) await this.invokePresence([...this.presenceIds]);
    await this.handlers.connected();
  }

  private async sendHeartbeat(): Promise<void> {
    if (!this.isConnected) return;
    const active = this.active && (typeof document === "undefined" || document.visibilityState === "visible");
    this.active = false;
    await this.connection.send("Heartbeat", active);
  }
}
