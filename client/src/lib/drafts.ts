/** Unsent text per user and room, kept in this browser only and cleared on send. */
const prefix = "mode.draft.";

function key(userId: string, room: string): string {
  return `${prefix}${userId}.${room}`;
}

export function loadDraft(userId: string, room: string): string {
  try {
    return localStorage.getItem(key(userId, room)) ?? "";
  } catch {
    return "";
  }
}

export function saveDraft(userId: string, room: string, text: string): void {
  try {
    if (text.trim()) localStorage.setItem(key(userId, room), text);
    else localStorage.removeItem(key(userId, room));
  } catch {
    // Storage can be full or disabled; drafts are a convenience only.
  }
}
