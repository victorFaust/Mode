import { AlertCircle, Copy, Forward, Link2, MessageSquareReply, MoreHorizontal, Pencil, Pin, PinOff, Trash2 } from "lucide-react";
import { memo, useEffect, useRef, useState } from "react";
import type { Message } from "../api/client";
import { useMode, useController } from "../app/context";
import { EmojiPicker } from "../components/EmojiPicker";
import { ForwardDialog, HistoryDialog } from "../components/MessageDialogs";
import { Avatar, Button, Dialog, DialogActions, IconButton, Menu, MenuItem, MenuSeparator } from "../components/ui";
import { MAX_MESSAGE_LENGTH, RichEditor, type RichEditorHandle } from "../editor/RichEditor";
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
  const editing = useMode((s) => s.editingMessageId === message.id);
  const [menuOpen, setMenuOpen] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [forwarding, setForwarding] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const mine = message.author.id === meId;
  const pending = message.pending;
  const canReply = !inThread && !message.parentMessageId && !pending && !message.isDeleted;
  const canEdit = mine && canPost && !message.isDeleted && !pending;
  const canDelete = (mine || canManage) && canPost && !message.isDeleted && !pending;
  const canPin = canPost && !message.parentMessageId && !inThread && !message.isDeleted && !pending;
  const titleId = `msg-${message.id}`;

  const run = (action: () => Promise<void>) => void action().catch((e: Error) => controller.toastError(e.message));
  const copyLink = () => void navigator.clipboard?.writeText(controller.messageLink(message.id)).then(
    () => controller.showBanner({ id: `copied-${message.id}`, title: "Link copied", body: "Only people in this chat can open it.", room: null, messageId: null }),
    () => controller.toastError("Couldn't copy the link."),
  );

  return (
    <article className={styles.message} data-roving tabIndex={-1} aria-labelledby={titleId}
      data-grouped={grouped} data-pending={pending?.state} data-active={menuOpen || editing} data-highlight={highlighted}>
      <div className={styles.gutter}>
        {grouped
          ? <time className={styles.groupedTime} dateTime={message.createdAt} title={formatFull(message.createdAt)}>{formatTime(message.createdAt)}</time>
          : <Avatar id={message.author.id} name={message.author.displayName} avatarUrl={message.author.avatarUrl} size={36} status={status} />}
      </div>
      <div style={{ minWidth: 0 }}>
        <div className={grouped ? "m-visually-hidden" : styles.meta} id={titleId}>
          <span className={styles.author}>{message.author.displayName}</span>{" "}
          {message.isBot && <span className={styles.botTag}>Bot</span>}{message.isBot && " "}
          <time className={styles.time} dateTime={message.createdAt} title={formatFull(message.createdAt)}>
            {pending?.state === "sending" ? "Sending…" : formatTime(message.createdAt)}
          </time>
          {message.editedAt && !message.isDeleted && (mine
            ? <button type="button" className={`${styles.edited} ${styles.editedButton}`} aria-label="Edited. View earlier versions"
                onClick={() => setShowHistory(true)}>Edited</button>
            : <span className={styles.edited}>Edited</span>)}
          {message.isPinned && !message.isDeleted && (
            <span className={styles.pinnedTag}><Pin size={12} aria-hidden /> Pinned</span>
          )}
        </div>

        {message.isDeleted ? (
          <p className={styles.deleted}>
            This message was deleted.{" "}
            {mine && message.deletedByAuthor && canPost && (
              <button type="button" className={styles.linkButton} onClick={() => run(() => controller.restore(message))}>Undo</button>
            )}
          </p>
        ) : editing ? <EditBox message={message} onDone={() => controller.startEdit(null)} />
          : message.bodyMarkdown && <MessageBody markdown={message.bodyMarkdown} />}

        {!message.isDeleted && message.forwarded && <ForwardCard message={message} />}

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
            <AlertCircle size={16} aria-hidden /> {pending.error ?? "Not sent."}
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
          {canEdit && (
            <IconButton label="Edit" size="small" onClick={() => controller.startEdit(message.id)}><Pencil size={16} aria-hidden /></IconButton>
          )}
          {canDelete && (
            <IconButton label="Delete" size="small" onClick={() => setConfirmDelete(true)}><Trash2 size={16} aria-hidden /></IconButton>
          )}
          <Menu align="end" onOpenChange={setMenuOpen} trigger={<IconButton label="More actions" size="small"><MoreHorizontal size={16} aria-hidden /></IconButton>}>
            <MenuItem onSelect={() => void navigator.clipboard?.writeText(toEditableText(message.bodyMarkdown, controller.store.getState().users).text)}>
              <Copy size={16} aria-hidden /> Copy text
            </MenuItem>
            <MenuItem onSelect={copyLink}><Link2 size={16} aria-hidden /> Copy link</MenuItem>
            <MenuItem onSelect={() => setForwarding(true)}><Forward size={16} aria-hidden /> Forward</MenuItem>
            {canPin && (message.isPinned
              ? <MenuItem onSelect={() => run(() => controller.setPinned(message, false))}><PinOff size={16} aria-hidden /> Unpin</MenuItem>
              : <MenuItem onSelect={() => run(() => controller.setPinned(message, true))}><Pin size={16} aria-hidden /> Pin</MenuItem>)}
            {(canEdit || canDelete) && <MenuSeparator />}
            {canEdit && <MenuItem onSelect={() => controller.startEdit(message.id)}><Pencil size={16} aria-hidden /> Edit</MenuItem>}
            {canDelete && <MenuItem danger onSelect={() => setConfirmDelete(true)}><Trash2 size={16} aria-hidden /> Delete</MenuItem>}
          </Menu>
        </div>
      )}

      {forwarding && <ForwardDialog message={message} open={forwarding} onOpenChange={setForwarding} />}
      {showHistory && <HistoryDialog message={message} open={showHistory} onOpenChange={setShowHistory} />}

      <Dialog open={confirmDelete} onOpenChange={setConfirmDelete} title="Delete this message?"
        description={mine ? "Everyone will see that a message was deleted. You can undo it straight after." : "Everyone will see that a message was deleted."}>
        <DialogActions>
          <Button variant="ghost" onClick={() => setConfirmDelete(false)}>Cancel</Button>
          <Button variant="primary" autoFocus onClick={() => { setConfirmDelete(false); run(() => controller.remove(message)); }}>Delete</Button>
        </DialogActions>
      </Dialog>
    </article>
  );
});

function ForwardCard({ message }: { message: Message }) {
  const controller = useController();
  const card = message.forwarded!;
  return (
    <div className={styles.forwardCard}>
      <div className={styles.forwardMeta}>
        <Forward size={14} aria-hidden />
        <span>Forwarded from <strong>{card.author.displayName}</strong></span>
        <time dateTime={card.createdAt} title={formatFull(card.createdAt)}>{formatListTime(card.createdAt)}</time>
        {card.messageId && (
          <button type="button" className={styles.linkButton} onClick={() => void controller.openMessage(card.messageId!)}>
            View original
          </button>
        )}
      </div>
      <MessageBody markdown={card.bodyMarkdown} />
    </div>
  );
}

function EditBox({ message, onDone }: { message: LocalMessage; onDone: () => void }) {
  const controller = useController();
  const editorRef = useRef<RichEditorHandle>(null);
  const [busy, setBusy] = useState(false);
  const [state, setState] = useState({ empty: false, length: message.bodyMarkdown.length });

  useEffect(() => editorRef.current?.focus(), []);

  const save = async (markdown: string) => {
    if (!markdown || markdown.length > MAX_MESSAGE_LENGTH) return;
    if (markdown === message.bodyMarkdown) return onDone();
    setBusy(true);
    try {
      await controller.edit(message, markdown, []);
    } catch (e) {
      controller.toastError(e instanceof Error ? e.message : "Could not save the edit.");
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className={styles.composerBox} style={{ marginTop: 4 }}>
      <RichEditor ref={editorRef} ariaLabel="Edit message" placeholder="Edit message" initialMarkdown={message.bodyMarkdown}
        toolbar members={[]} allowChannelMention={message.room.type === "Channel"}
        onSubmit={(markdown) => void save(markdown)} onEscape={onDone}
        onChange={(markdown, empty) => setState({ empty, length: markdown.length })} />
      <div className={styles.composerFooter} style={{ justifyContent: "flex-end", gap: 8 }}>
        <span className={styles.composerHint}>Enter to save, Esc to cancel</span>
        <Button variant="ghost" onClick={onDone}>Cancel</Button>
        <Button variant="primary" disabled={busy || state.empty || state.length > MAX_MESSAGE_LENGTH}
          onClick={() => void save(editorRef.current?.getMarkdown() ?? "")}>Save</Button>
      </div>
    </div>
  );
}
