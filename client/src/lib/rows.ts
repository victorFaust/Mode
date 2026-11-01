import type { LocalMessage } from "../store/store";

export type Row =
  | { kind: "day"; key: string; date: Date }
  | { kind: "new"; key: string }
  | { kind: "message"; key: string; message: LocalMessage; grouped: boolean };

const GROUP_WINDOW_MS = 5 * 60 * 1000;

function dayKey(date: Date): string {
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
}

/**
 * Turns messages (oldest first) into list rows: a separator per day, a "New messages" marker before the first
 * message from someone else after the read marker, and grouping of consecutive messages by the same author.
 */
export function buildRows(messages: LocalMessage[], meId: string | undefined, lastReadSequence: number | null): Row[] {
  const rows: Row[] = [];
  let previous: LocalMessage | null = null;
  let markerPlaced = lastReadSequence === null;

  for (const message of messages) {
    const created = new Date(message.createdAt);
    let breaksGroup = false;

    if (!previous || dayKey(new Date(previous.createdAt)) !== dayKey(created)) {
      rows.push({ kind: "day", key: `day-${dayKey(created)}`, date: created });
      breaksGroup = true;
    }
    if (!markerPlaced && !message.pending && message.sequence > (lastReadSequence ?? 0) && message.author.id !== meId) {
      rows.push({ kind: "new", key: "new-marker" });
      markerPlaced = true;
      breaksGroup = true;
    }

    const grouped = !breaksGroup && previous !== null &&
      previous.author.id === message.author.id &&
      !previous.isDeleted &&
      created.getTime() - new Date(previous.createdAt).getTime() < GROUP_WINDOW_MS;

    rows.push({ kind: "message", key: message.id, message, grouped });
    previous = message;
  }
  return rows;
}
