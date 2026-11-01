import {
  Bold, Code, Italic, Link, List, Loader2, Paperclip, Quote, SendHorizontal, Strikethrough, X,
} from "lucide-react";
import {
  forwardRef, useCallback, useEffect, useId, useImperativeHandle, useMemo, useRef, useState, type ChangeEvent, type KeyboardEvent,
} from "react";
import { ok, roomKey, type RoomRef, type User } from "../api/client";
import { useController, useMode } from "../app/context";
import { EmojiPicker } from "../components/EmojiPicker";
import { Avatar, Button, IconButton } from "../components/ui";
import { loadDraft, saveDraft } from "../lib/drafts";
import { formatBytes } from "../lib/format";
import { activeMentionQuery, type PickedMention } from "../lib/mentions";
import styles from "./views.module.css";

const MAX_LENGTH = 20_000;

interface Upload {
  key: string;
  name: string;
  size: number;
  progress: number;
  fileId: string | null;
  error: string | null;
  abort: AbortController;
}

interface Suggestion {
  id: string;
  label: string;
  meta?: string;
  user?: User;
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
  const isGuest = useMode((s) => s.isGuest);
  const draftKey = `${roomKey(room)}${parentMessageId ? `:${parentMessageId}` : ""}`;
  const [text, setText] = useState(() => (me ? loadDraft(me.id, draftKey) : ""));
  const [picked, setPicked] = useState<PickedMention[]>([]);
  const [uploads, setUploads] = useState<Upload[]>([]);
  const [dragging, setDragging] = useState(false);
  const [mention, setMention] = useState<{ start: number; query: string } | null>(null);
  const [directory, setDirectory] = useState<User[]>([]);
  const [active, setActive] = useState(0);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const id = useId();

  useEffect(() => {
    if (!me) return;
    const handle = setTimeout(() => saveDraft(me.id, draftKey, text), 300);
    return () => clearTimeout(handle);
  }, [text, me, draftKey]);

  // Grow with the text up to the CSS max-height.
  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }, [text]);

  // People in the room come first; the directory fills in the rest.
  useEffect(() => {
    const q = mention?.query.trim() ?? "";
    if (!mention || isGuest || q.length < 2) {
      setDirectory([]);
      return;
    }
    const handle = setTimeout(() => {
      void ok(controller.api.GET("/api/v1/users/search", { params: { query: { q, limit: 6 } } }))
        .then(setDirectory, () => setDirectory([]));
    }, 200);
    return () => clearTimeout(handle);
  }, [mention, isGuest, controller]);

  const suggestions = useMemo<Suggestion[]>(() => {
    if (!mention) return [];
    const q = mention.query.toLowerCase();
    const matches = (u: User) => u.displayName.toLowerCase().includes(q) || (u.email ?? "").toLowerCase().startsWith(q);
    const inRoom = members.filter((u) => u.id !== me?.id && matches(u));
    const others = directory.filter((u) => u.id !== me?.id && !inRoom.some((m) => m.id === u.id));
    const list: Suggestion[] = [
      ...inRoom.map((u) => ({ id: u.id, label: u.displayName, meta: "In this conversation", user: u })),
      ...others.map((u) => ({ id: u.id, label: u.displayName, meta: u.email ?? undefined, user: u })),
    ];
    if (allowChannelMention && "channel".startsWith(q)) list.unshift({ id: "channel", label: "channel", meta: "Notify everyone here" });
    return list.slice(0, 8);
  }, [mention, members, directory, me?.id, allowChannelMention]);

  useEffect(() => setActive(0), [mention?.query]);

  const updateMention = (value: string, caret: number) => setMention(activeMentionQuery(value, caret));

  const insertAtCaret = useCallback((insert: string, replaceFrom?: number) => {
    const el = textareaRef.current;
    const start = replaceFrom ?? el?.selectionStart ?? text.length;
    const end = el?.selectionEnd ?? text.length;
    const next = text.slice(0, start) + insert + text.slice(end);
    setText(next);
    requestAnimationFrame(() => {
      el?.focus();
      const caret = start + insert.length;
      el?.setSelectionRange(caret, caret);
    });
  }, [text]);

  const pickSuggestion = (s: Suggestion) => {
    if (!mention) return;
    insertAtCaret(`@${s.label} `, mention.start);
    if (s.user) {
      setPicked((p) => (p.some((x) => x.id === s.id) ? p : [...p, { id: s.id, name: s.label }]));
      controller.rememberUsers([s.user]);
    }
    setMention(null);
  };

  /** Wraps the selection (or inserts a placeholder) with Markdown markers. */
  const wrap = (before: string, after = before, placeholderText = "text") => {
    const el = textareaRef.current;
    if (!el) return;
    const { selectionStart: start, selectionEnd: end } = el;
    const selected = text.slice(start, end) || placeholderText;
    const next = text.slice(0, start) + before + selected + after + text.slice(end);
    setText(next);
    requestAnimationFrame(() => {
      el.focus();
      el.setSelectionRange(start + before.length, start + before.length + selected.length);
    });
  };

  const prefixLines = (prefix: string) => {
    const el = textareaRef.current;
    if (!el) return;
    const lineStart = text.lastIndexOf("\n", el.selectionStart - 1) + 1;
    const end = el.selectionEnd;
    const block = text.slice(lineStart, end).split("\n").map((line) => prefix + line).join("\n");
    setText(text.slice(0, lineStart) + block + text.slice(end));
    requestAnimationFrame(() => el.focus());
  };

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
    textareaRef.current?.focus();
  }, [controller, room]);

  useImperativeHandle(ref, () => ({ addFiles, focus: () => textareaRef.current?.focus() }), [addFiles]);

  const uploading = uploads.some((u) => !u.fileId && !u.error);
  const ready = uploads.filter((u) => u.fileId);
  const canSend = !uploading && (text.trim().length > 0 || ready.length > 0) && text.length <= MAX_LENGTH;

  const send = () => {
    if (!canSend) return;
    void controller.send(room, text.trim(), picked, ready.map((u) => u.fileId!), parentMessageId);
    setText("");
    setPicked([]);
    setUploads([]);
    setMention(null);
    if (me) saveDraft(me.id, draftKey, "");
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (mention && suggestions.length > 0) {
      if (e.key === "ArrowDown") { e.preventDefault(); setActive((a) => (a + 1) % suggestions.length); return; }
      if (e.key === "ArrowUp") { e.preventDefault(); setActive((a) => (a - 1 + suggestions.length) % suggestions.length); return; }
      if (e.key === "Enter" || e.key === "Tab") { e.preventDefault(); pickSuggestion(suggestions[active]); return; }
      if (e.key === "Escape") { e.preventDefault(); setMention(null); return; }
    }
    const mod = e.ctrlKey || e.metaKey;
    if (mod && e.key.toLowerCase() === "b") { e.preventDefault(); wrap("**"); return; }
    if (mod && e.key.toLowerCase() === "i") { e.preventDefault(); wrap("_"); return; }
    if (mod && e.key.toLowerCase() === "e") { e.preventDefault(); wrap("`", "`", "code"); return; }
    if (mod && e.shiftKey && e.key.toLowerCase() === "x") { e.preventDefault(); wrap("~~"); return; }
    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      send();
    }
  };

  const onChange = (e: ChangeEvent<HTMLTextAreaElement>) => {
    setText(e.target.value);
    updateMention(e.target.value, e.target.selectionStart);
    if (e.target.value.trim()) controller.typing(room, parentMessageId);
  };

  const listId = `${id}-mentions`;
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
        {!parentMessageId && (
          <div className={styles.toolbar} role="toolbar" aria-label="Formatting">
            <IconButton label="Bold (Ctrl+B)" size="small" onClick={() => wrap("**")}><Bold size={16} aria-hidden /></IconButton>
            <IconButton label="Italic (Ctrl+I)" size="small" onClick={() => wrap("_")}><Italic size={16} aria-hidden /></IconButton>
            <IconButton label="Strikethrough (Ctrl+Shift+X)" size="small" onClick={() => wrap("~~")}><Strikethrough size={16} aria-hidden /></IconButton>
            <IconButton label="Code (Ctrl+E)" size="small" onClick={() => wrap("`", "`", "code")}><Code size={16} aria-hidden /></IconButton>
            <IconButton label="Link" size="small" onClick={() => wrap("[", "](https://)", "link text")}><Link size={16} aria-hidden /></IconButton>
            <IconButton label="Bulleted list" size="small" onClick={() => prefixLines("- ")}><List size={16} aria-hidden /></IconButton>
            <IconButton label="Quote" size="small" onClick={() => prefixLines("> ")}><Quote size={16} aria-hidden /></IconButton>
          </div>
        )}

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

        <div style={{ position: "relative" }}>
          {mention && suggestions.length > 0 && (
            <ul id={listId} role="listbox" aria-label="Mention someone" className={styles.popover}
              style={{ position: "absolute", bottom: "100%", left: 8, margin: 0, listStyle: "none", marginBottom: 6 }}>
              {suggestions.map((s, i) => (
                <li key={s.id} id={`${listId}-${i}`} role="option" aria-selected={i === active} className={styles.option}
                  onMouseDown={(e) => { e.preventDefault(); pickSuggestion(s); }} onMouseEnter={() => setActive(i)}>
                  {s.user ? <Avatar id={s.user.id} name={s.user.displayName} avatarUrl={s.user.avatarUrl} size={24} /> : <span style={{ width: 24, textAlign: "center" }}>@</span>}
                  <span>{s.label}</span>
                  {s.meta && <span className={styles.optionMeta}>{s.meta}</span>}
                </li>
              ))}
            </ul>
          )}
          <textarea ref={textareaRef} className={styles.textarea} value={text} placeholder={placeholder} rows={1}
            aria-label={placeholder} aria-controls={mention && suggestions.length ? listId : undefined}
            aria-activedescendant={mention && suggestions.length ? `${listId}-${active}` : undefined}
            onChange={onChange} onKeyDown={onKeyDown}
            onClick={(e) => updateMention(e.currentTarget.value, e.currentTarget.selectionStart)}
            onBlur={() => setTimeout(() => setMention(null), 100)} />
        </div>

        <div className={styles.composerFooter}>
          <IconButton label="Attach files" size="small" onClick={() => fileInputRef.current?.click()}><Paperclip size={18} aria-hidden /></IconButton>
          <input ref={fileInputRef} type="file" multiple hidden aria-hidden tabIndex={-1} data-testid="file-input"
            onChange={(e) => { addFiles(Array.from(e.target.files ?? [])); e.target.value = ""; }} />
          <EmojiPicker onPick={(emoji) => insertAtCaret(emoji)} />
          <span className={styles.composerHint}>
            {text.length > MAX_LENGTH - 1000 ? `${text.length.toLocaleString()} / ${MAX_LENGTH.toLocaleString()}` : "Shift+Enter for a new line"}
          </span>
          <Button variant="primary" className={styles.send} onClick={send} disabled={!canSend} aria-label="Send">
            {uploading ? <Loader2 size={16} aria-hidden /> : <SendHorizontal size={16} aria-hidden />}
          </Button>
        </div>
      </div>
    </div>
  );
});
