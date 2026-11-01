import { AlertCircle, Copy, MessageSquareReply, MoreHorizontal, Pencil, Trash2 } from "lucide-react";
import { memo, useState } from "react";
import { useMode, useController } from "../app/context";
import { EmojiPicker } from "../components/EmojiPicker";
import { Avatar, Button, IconButton, Menu, MenuItem, MenuSeparator } from "../components/ui";
import { formatFull, formatListTime, formatTime } from "../lib/format";
import { toEditableText } from "../lib/mentions";
import type { LocalMessage } from "../store/store";
import { AttachmentCard } from "./AttachmentCard";
import { MessageBody } from "./MessageBody";
import styles from "./views.module.css";

const quickReactions = ["👍", "❤️", "😂"];

export interface MessageItemProps {
  message: LocalMessage;
  grouped: boolean;
  canManage: boolean;
  canPost: boolean;
  inThread?: boolean;
  highlighted?: boolean;
}

export const MessageItem = memo(function MessageItem({ message, grouped, canManage, canPost, inThread, highlighted }: MessageItemProps) {
  const controller = useController();
  const meId = useMode((s) => s.me?.id);
  const status = useMode((s) => s.presence[message.author.id]?.status);
  const [editing, setEditing] = useState(false);
  const [menuOpen, setMenuOpen] = useState(false);
  const mine = message.author.id === meId;
  const pending = message.pending;
  const canReply = !inThread && !message.parentMessageId && !pending && !message.isDeleted;
  const titleId = `msg-${message.id}`;

  const run = (action: () => Promise<void>) => void action().catch((e: Error) => controller.toastError(e.message));

  return (
    <article className={styles.message} data-roving tabIndex={-1} aria-labelledby={titleId}
      data-grouped={grouped} data-pending={pending?.state} data-active={menuOpen} data-highlight={highlighted}>
      <div className={styles.gutter}>
        {grouped
          ? <time className={styles.groupedTime} dateTime={message.createdAt} title={formatFull(message.createdAt)}>{formatTime(message.createdAt)}</time>
          : <Avatar id={message.author.id} name={message.author.displayName} avatarUrl={message.author.avatarUrl} size={36} status={status} />}
      </div>
      <div style={{ minWidth: 0 }}>
        <div className={grouped ? "m-visually-hidden" : styles.meta} id={titleId}>
          <span className={styles.author}>{message.author.displayName}</span>{" "}
          <time className={styles.time} dateTime={message.createdAt} title={formatFull(message.createdAt)}>
            {pending?.state === "sending" ? "Sending…" : formatTime(message.createdAt)}
          </time>
          {message.editedAt && !message.isDeleted && <span className={styles.edited}>Edited</span>}
        </div>

        {message.isDeleted ? <p className={styles.deleted}>This message was deleted.</p>
          : editing ? <EditBox message={message} onDone={() => setEditing(false)} />
          : message.bodyMarkdown && <MessageBody markdown={message.bodyMarkdown} />}

        {!message.isDeleted && message.files.length > 0 && (
          <div className={styles.attachments}>
            {message.files.map((f) => <AttachmentCard key={f.fileId} attachment={f} />)}
          </div>
        )}

        {message.reactions.length > 0 && (
          <div className={styles.reactions}>
            {message.reactions.map((r) => {
              const reacted = r.userIds.includes(meId ?? "");
              return (
                <button key={r.emoji} type="button" className={styles.reaction} aria-pressed={reacted} disabled={!canPost}
                  aria-label={`${r.emoji} ${r.count} ${reacted ? ", including you" : ""}`}
                  onClick={() => run(() => controller.toggleReaction(message, r.emoji))}>
                  <span aria-hidden>{r.emoji}</span> {r.count}
                </button>
              );
            })}
          </div>
        )}

        {!inThread && message.replyCount > 0 && (
          <button type="button" className={styles.replies} onClick={() => controller.setSide({ kind: "thread", messageId: message.id })}>
            <MessageSquareReply size={16} aria-hidden />
            {message.replyCount} {message.replyCount === 1 ? "reply" : "replies"}
            {message.lastReplyAt && <span className={styles.repliesTime}>Last reply {formatListTime(message.lastReplyAt)}</span>}
          </button>
        )}

        {pending?.state === "failed" && (
          <div className={styles.failed} role="alert">
            <AlertCircle size={16} aria-hidden /> Not sent.
            <button type="button" className={styles.linkButton} onClick={() => void controller.retry(message)}>Retry</button>
            <button type="button" className={styles.linkButton} onClick={() => controller.discard(message)}>Delete</button>
          </div>
        )}
      </div>

      {!pending && !message.isDeleted && !editing && (
        <div className={styles.actions} role="toolbar" aria-label="Message actions">
          {canPost && quickReactions.map((emoji) => (
            <button key={emoji} type="button" className={styles.quickEmoji} aria-label={`React with ${emoji}`}
              onClick={() => run(() => controller.toggleReaction(message, emoji))}>{emoji}</button>
          ))}
          {canPost && <EmojiPicker label="More reactions" onPick={(emoji) => run(() => controller.toggleReaction(message, emoji))} />}
          {canReply && canPost && (
            <IconButton label="Reply in thread" size="small" onClick={() => controller.setSide({ kind: "thread", messageId: message.id })}>
              <MessageSquareReply size={16} aria-hidden />
            </IconButton>
          )}
          <Menu align="end" onOpenChange={setMenuOpen} trigger={<IconButton label="More actions" size="small"><MoreHorizontal size={16} aria-hidden /></IconButton>}>
            <MenuItem onSelect={() => void navigator.clipboard?.writeText(toEditableText(message.bodyMarkdown, controller.store.getState().users).text)}>
              <Copy size={16} aria-hidden /> Copy text
            </MenuItem>
            {mine && canPost && <MenuItem onSelect={() => setEditing(true)}><Pencil size={16} aria-hidden /> Edit</MenuItem>}
            {(mine || canManage) && canPost && (
              <>
                <MenuSeparator />
                <MenuItem danger onSelect={() => {
                  if (window.confirm("Delete this message? Everyone will see that it was deleted.")) run(() => controller.remove(message));
                }}><Trash2 size={16} aria-hidden /> Delete</MenuItem>
              </>
            )}
          </Menu>
        </div>
      )}
    </article>
  );
});

function EditBox({ message, onDone }: { message: LocalMessage; onDone: () => void }) {
  const controller = useController();
  const users = useMode((s) => s.users);
  const [{ text, picked }, setState] = useState(() => toEditableText(message.bodyMarkdown, users));
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    try {
      await controller.edit(message, text, picked);
      onDone();
    } catch (e) {
      controller.toastError(e instanceof Error ? e.message : "Could not save the edit.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.composerBox} style={{ marginTop: 4 }}>
      <textarea className={styles.textarea} value={text} autoFocus aria-label="Edit message" rows={2}
        onChange={(e) => setState({ text: e.target.value, picked })}
        onKeyDown={(e) => {
          if (e.key === "Escape") { e.preventDefault(); onDone(); }
          if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); void save(); }
        }} />
      <div className={styles.composerFooter} style={{ justifyContent: "flex-end", gap: 8 }}>
        <span className={styles.composerHint}>Enter to save, Esc to cancel</span>
        <Button variant="ghost" onClick={onDone}>Cancel</Button>
        <Button variant="primary" onClick={() => void save()} disabled={busy || !text.trim()}>Save</Button>
      </div>
    </div>
  );
}
