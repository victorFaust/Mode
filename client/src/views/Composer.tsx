import { Loader2, Paperclip, SendHorizontal, X } from "lucide-react";
import { forwardRef, useCallback, useImperativeHandle, useMemo, useRef, useState } from "react";
import { roomKey, type RoomRef, type User } from "../api/client";
import { useController, useMode } from "../app/context";
import { EmojiPicker } from "../components/EmojiPicker";
import { Button, IconButton } from "../components/ui";
import { MAX_MESSAGE_LENGTH, RichEditor, type RichEditorHandle } from "../editor/RichEditor";
import { loadDraft, saveDraft } from "../lib/drafts";
import { formatBytes } from "../lib/format";
import styles from "./views.module.css";

interface Upload {
  key: string;
  name: string;
  size: number;
  progress: number;
  fileId: string | null;
  error: string | null;
  abort: AbortController;
}

export interface ComposerHandle {
  addFiles(files: File[]): void;
  focus(): void;
}

export const Composer = forwardRef<ComposerHandle, {
  room: RoomRef;
  parentMessageId?: string | null;
  placeholder: string;
  members: User[];
  allowChannelMention: boolean;
}>(function Composer({ room, parentMessageId = null, placeholder, members, allowChannelMention }, ref) {
  const controller = useController();
  const me = useMode((s) => s.me);
  const draftKey = `${roomKey(room)}${parentMessageId ? `:${parentMessageId}` : ""}`;
  const initialDraft = useMemo(() => (me ? loadDraft(me.id, draftKey) : ""), [me, draftKey]);
  const [length, setLength] = useState(initialDraft.length);
  const [empty, setEmpty] = useState(initialDraft.trim().length === 0);
  const [uploads, setUploads] = useState<Upload[]>([]);
  const [dragging, setDragging] = useState(false);
  const editorRef = useRef<RichEditorHandle>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const draftTimer = useRef<ReturnType<typeof setTimeout>>(undefined);

  const addFiles = useCallback((files: File[]) => {
    for (const file of files) {
      const upload: Upload = {
        key: crypto.randomUUID(), name: file.name, size: file.size, progress: 0, fileId: null, error: null, abort: new AbortController(),
      };
      setUploads((u) => [...u, upload]);
      const patch = (p: Partial<Upload>) => setUploads((list) => list.map((x) => (x.key === upload.key ? { ...x, ...p } : x)));
      controller.uploadFile(room, file, (progress) => patch({ progress }), upload.abort.signal)
        .then((dto) => patch({ fileId: dto.id, progress: 1 }))
        .catch((e: Error) => { if (e.name !== "AbortError") patch({ error: e.message }); });
    }
    editorRef.current?.focus();
  }, [controller, room]);

  useImperativeHandle(ref, () => ({ addFiles, focus: () => editorRef.current?.focus() }), [addFiles]);

  const uploading = uploads.some((u) => !u.fileId && !u.error);
  const ready = uploads.filter((u) => u.fileId);
  const tooLong = length > MAX_MESSAGE_LENGTH;
  const canSend = !uploading && !tooLong && (!empty || ready.length > 0);

  const send = (markdown: string) => {
    if (uploading || tooLong || (!markdown && ready.length === 0)) return;
    void controller.send(room, markdown, [], ready.map((u) => u.fileId!), parentMessageId);
    editorRef.current?.clear();
    setUploads([]);
    clearTimeout(draftTimer.current);
    if (me) saveDraft(me.id, draftKey, "");
  };

  const onChange = useCallback((markdown: string, isEmpty: boolean) => {
    setLength(markdown.length);
    setEmpty(isEmpty);
    if (!isEmpty) controller.typing(room, parentMessageId);
    clearTimeout(draftTimer.current);
    draftTimer.current = setTimeout(() => me && saveDraft(me.id, draftKey, markdown), 300);
  }, [controller, room, parentMessageId, me, draftKey]);

  const editLast = useCallback(() => controller.editLastOwn(room, parentMessageId), [controller, room, parentMessageId]);

  return (
    <div className={styles.composer}>
      <div className={styles.composerBox} data-dragging={dragging}
        onDragOver={(e) => { if (e.dataTransfer.types.includes("Files")) { e.preventDefault(); setDragging(true); } }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          if (!e.dataTransfer.files.length) return;
          e.preventDefault();
          e.stopPropagation();
          setDragging(false);
          addFiles(Array.from(e.dataTransfer.files));
        }}>
        {uploads.length > 0 && (
          <ul className={styles.uploads} aria-label="Attachments" style={{ listStyle: "none", margin: 0 }}>
            {uploads.map((u) => (
              <li key={u.key} className={styles.upload}>
                <span className={styles.uploadName} title={u.name}>{u.name}</span>
                <IconButton label={`Remove ${u.name}`} size="small" tooltip={false}
                  onClick={() => { u.abort.abort(); setUploads((list) => list.filter((x) => x.key !== u.key)); }}>
                  <X size={14} aria-hidden />
                </IconButton>
                {u.error ? <span className={styles.uploadError} role="alert">{u.error}</span> : (
                  <>
                    <span className={styles.progress} role="progressbar" aria-label={`Uploading ${u.name}`}
                      aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(u.progress * 100)}>
                      <span style={{ width: `${Math.round(u.progress * 100)}%` }} />
                    </span>
                    <span style={{ gridColumn: "1 / -1", color: "var(--m-text-muted)", fontSize: 12 }}>
                      {u.fileId ? `Uploaded · ${formatBytes(u.size)}` : `${Math.round(u.progress * 100)}% of ${formatBytes(u.size)}`}
                    </span>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}

        <RichEditor ref={editorRef} key={draftKey} ariaLabel={placeholder} placeholder={placeholder} initialMarkdown={initialDraft}
          toolbar={!parentMessageId} compact={!!parentMessageId} members={members} allowChannelMention={allowChannelMention}
          onSubmit={send} onChange={onChange} onFiles={addFiles} onArrowUpWhenEmpty={editLast} />

        <div className={styles.composerFooter}>
          <IconButton label="Attach files" size="small" onClick={() => fileInputRef.current?.click()}><Paperclip size={18} aria-hidden /></IconButton>
          <input ref={fileInputRef} type="file" multiple hidden aria-hidden tabIndex={-1} data-testid="file-input"
            onChange={(e) => { addFiles(Array.from(e.target.files ?? [])); e.target.value = ""; }} />
          <EmojiPicker onPick={(emoji) => editorRef.current?.insertText(emoji)} />
          <span className={styles.composerHint} aria-live="polite">
            {length > MAX_MESSAGE_LENGTH - 1000
              ? `${length.toLocaleString()} / ${MAX_MESSAGE_LENGTH.toLocaleString()}${tooLong ? " (too long)" : ""}`
              : "Enter to send, Shift+Enter for a new line"}
          </span>
          <Button variant="primary" className={styles.send} disabled={!canSend} aria-label="Send"
            onClick={() => send(editorRef.current?.getMarkdown() ?? "")}>
            {uploading ? <Loader2 size={16} aria-hidden /> : <SendHorizontal size={16} aria-hidden />}
          </Button>
        </div>
      </div>
    </div>
  );
});
