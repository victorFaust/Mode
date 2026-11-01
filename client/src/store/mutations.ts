import { roomKey, type Message, type Reaction, type RoomRef } from "../api/client";
import type { LocalMessage, ModeState, RoomMessages } from "./store";

const emptyRoom = (): RoomMessages => ({ items: [], loaded: false, loading: false, hasOlder: true, lastReadOnOpen: null });
/** Shared so store selectors return a stable reference for rooms not loaded yet. */
const EMPTY_ROOM: RoomMessages = Object.freeze(emptyRoom()) as RoomMessages;

export function roomOf(state: ModeState, room: RoomRef): RoomMessages {
  return state.rooms[roomKey(room)] ?? EMPTY_ROOM;
}

/** Inserts or replaces by id, keeping ascending sequence order; pending messages stay at the end. */
export function upsertSorted(items: LocalMessage[], message: LocalMessage): LocalMessage[] {
  const without = items.filter((m) => m.id !== message.id);
  if (message.pending) return [...without, message];
  const confirmed = without.filter((m) => !m.pending);
  const pending = without.filter((m) => m.pending);
  const index = confirmed.findIndex((m) => m.sequence > message.sequence);
  if (index < 0) confirmed.push(message);
  else confirmed.splice(index, 0, message);
  return [...confirmed, ...pending];
}

export function patchRoom(state: ModeState, room: RoomRef, patch: (r: RoomMessages) => RoomMessages): Partial<ModeState> {
  const key = roomKey(room);
  return { rooms: { ...state.rooms, [key]: patch(state.rooms[key] ?? emptyRoom()) } };
}

/** Applies a created or changed message (from REST, replay or realtime). */
export function applyMessage(state: ModeState, message: LocalMessage): Partial<ModeState> {
  const key = roomKey(message.room);
  const users = { ...state.users, [message.author.id]: message.author };
  const roomSequence = { ...state.roomSequence, [key]: Math.max(state.roomSequence[key] ?? 0, message.updatedSequence) };

  if (message.parentMessageId) {
    const parentId = message.parentMessageId;
    const thread = state.threads[parentId] ?? { items: [], loaded: false };
    const isNew = !thread.items.some((m) => m.id === message.id && !m.pending);
    const threads = { ...state.threads, [parentId]: { ...thread, items: upsertSorted(thread.items, message) } };
    const room = state.rooms[key];
    const rooms = room && isNew && !message.pending
        ? {
            ...state.rooms,
            [key]: {
              ...room,
              items: room.items.map((m) => m.id === parentId
                ? { ...m, replyCount: m.replyCount + 1, lastReplyAt: message.createdAt }
                : m),
            },
          }
        : state.rooms;
    return { threads, rooms, users, roomSequence };
  }

  const room = state.rooms[key];
  if (!room?.loaded) return { users, roomSequence };
  return { rooms: { ...state.rooms, [key]: { ...room, items: upsertSorted(room.items, message) } }, users, roomSequence };
}

/** Replaces a message wherever it is shown (room list or thread). */
export function updateMessage(state: ModeState, room: RoomRef, messageId: string, update: (m: LocalMessage) => LocalMessage): Partial<ModeState> {
  const key = roomKey(room);
  const current = state.rooms[key];
  const rooms = current
    ? { ...state.rooms, [key]: { ...current, items: current.items.map((m) => (m.id === messageId ? update(m) : m)) } }
    : state.rooms;
  const threads = Object.fromEntries(
    Object.entries(state.threads).map(([parentId, thread]) => [
      parentId,
      thread.items.some((m) => m.id === messageId)
        ? { ...thread, items: thread.items.map((m) => (m.id === messageId ? update(m) : m)) }
        : thread,
    ]),
  );
  return { rooms, threads };
}

export function applyReactions(state: ModeState, room: RoomRef, messageId: string, reactions: Reaction[], sequence: number) {
  return {
    ...updateMessage(state, room, messageId, (m) => ({ ...m, reactions, updatedSequence: Math.max(m.updatedSequence, sequence) })),
    roomSequence: { ...state.roomSequence, [roomKey(room)]: Math.max(state.roomSequence[roomKey(room)] ?? 0, sequence) },
  };
}

export function applyDeleted(state: ModeState, room: RoomRef, messageId: string, sequence: number) {
  return {
    ...updateMessage(state, room, messageId, (m) => ({
      ...m, isDeleted: true, bodyMarkdown: "", files: [], mentions: [], updatedSequence: sequence,
    })),
    roomSequence: { ...state.roomSequence, [roomKey(room)]: Math.max(state.roomSequence[roomKey(room)] ?? 0, sequence) },
  };
}

export function mentionsMe(message: Message, meId: string | undefined): boolean {
  return message.mentions.some((m) => m.kind !== "User" || m.userId === meId);
}
