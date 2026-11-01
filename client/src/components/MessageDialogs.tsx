import { useEffect, useId, useMemo, useState } from "react";
import { useShallow } from "zustand/react/shallow";
import type { Message, MessageVersion } from "../api/client";
import { useController, useMode } from "../app/context";
import { formatFull } from "../lib/format";
import { previewText } from "../lib/mentions";
import { displayName } from "../store/controller";
import { MessageBody } from "../views/MessageBody";
import { Button, Dialog, DialogActions, TextField } from "./ui";
import styles from "./MessageDialogs.module.css";

/** Pick a chat and add an optional note; the server makes the copy so the card can be trusted. */
export function ForwardDialog({ message, open, onOpenChange }: { message: Message; open: boolean; onOpenChange: (open: boolean) => void }) {
  const controller = useController();
  const { conversations, meId, users } = useMode(useShallow((s) => ({ conversations: s.conversations, meId: s.me?.id, users: s.users })));
  const [filter, setFilter] = useState("");
  const [target, setTarget] = useState<string | null>(null);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const noteId = useId();

  useEffect(() => {
    if (!open) return;
    setFilter("");
    setTarget(null);
    setNote("");
    setError(null);
  }, [open]);

  const choices = useMemo(() => {
    const query = filter.trim().toLowerCase();
    return conversations
      .map((c) => ({ id: c.id, name: displayName(c, meId) }))
      .filter((c) => !query || c.name.toLowerCase().includes(query));
  }, [conversations, meId, filter]);

  const source = message.forwarded && !message.bodyMarkdown ? message.forwarded.bodyMarkdown : message.bodyMarkdown;

  const submit = async () => {
    if (!target) return;
    setBusy(true);
    setError(null);
    try {
      await controller.forward(message, { type: "Conversation", id: target }, note);
      onOpenChange(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not forward the message.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Forward message"
      description="People in the chat you pick see a copy. Files aren't forwarded.">
      <blockquote className={styles.quote}>{previewText(source, users, 200) || "(no text)"}</blockquote>
      <TextField label="Find a chat" value={filter} onChange={setFilter} autoFocus placeholder="Type a name" />
      <fieldset className={styles.choices}>
        <legend className="m-visually-hidden">Chat to forward to</legend>
        {choices.length === 0 && <p className={styles.empty}>No chats match.</p>}
        {choices.map((c) => (
          <label key={c.id} className={styles.choice} data-selected={target === c.id}>
            <input type="radio" name="forward-target" value={c.id} checked={target === c.id} onChange={() => setTarget(c.id)} />
            <span>{c.name}</span>
          </label>
        ))}
      </fieldset>
      <label className={styles.noteLabel} htmlFor={noteId}>Add a note (optional)</label>
      <textarea id={noteId} className={styles.note} rows={2} maxLength={2000} value={note} onChange={(e) => setNote(e.target.value)} />
      {error && <p className={styles.error} role="alert">{error}</p>}
      <DialogActions>
        <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
        <Button variant="primary" disabled={!target || busy} onClick={() => void submit()}>Forward</Button>
      </DialogActions>
    </Dialog>
  );
}

/** The author's earlier versions of a message, newest first. */
export function HistoryDialog({ message, open, onOpenChange }: { message: Message; open: boolean; onOpenChange: (open: boolean) => void }) {
  const controller = useController();
  const [versions, setVersions] = useState<MessageVersion[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    setVersions(null);
    setError(null);
    controller.history(message).then(
      (list) => { if (!cancelled) setVersions(list); },
      (e: Error) => { if (!cancelled) setError(e.message); },
    );
    return () => { cancelled = true; };
  }, [open, controller, message]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Edit history" description="Only you can see earlier versions of your message.">
      {error && <p className={styles.error} role="alert">{error}</p>}
      {!versions && !error && <p className={styles.empty}>Loading…</p>}
      {versions && (
        <ol className={styles.versions}>
          {[...versions].reverse().map((v, i) => (
            <li key={`${v.writtenAt}-${i}`} className={styles.version}>
              <div className={styles.versionMeta}>
                {i === 0 ? "Current" : "Earlier"} · <time dateTime={v.writtenAt}>{formatFull(v.writtenAt)}</time>
              </div>
              {v.bodyMarkdown ? <MessageBody markdown={v.bodyMarkdown} /> : <p className={styles.empty}>(no text)</p>}
            </li>
          ))}
        </ol>
      )}
      <DialogActions>
        <Button variant="primary" onClick={() => onOpenChange(false)}>Close</Button>
      </DialogActions>
    </Dialog>
  );
}
