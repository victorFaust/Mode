import { $createCodeNode, $isCodeNode } from "@lexical/code";
import { $isLinkNode, TOGGLE_LINK_COMMAND } from "@lexical/link";
import { INSERT_ORDERED_LIST_COMMAND, INSERT_UNORDERED_LIST_COMMAND, ListNode, REMOVE_LIST_COMMAND } from "@lexical/list";
import { ClearEditorPlugin } from "@lexical/react/LexicalClearEditorPlugin";
import { LexicalComposer, type InitialConfigType } from "@lexical/react/LexicalComposer";
import { useLexicalComposerContext } from "@lexical/react/LexicalComposerContext";
import { ContentEditable } from "@lexical/react/LexicalContentEditable";
import { LexicalErrorBoundary } from "@lexical/react/LexicalErrorBoundary";
import { HistoryPlugin } from "@lexical/react/LexicalHistoryPlugin";
import { LinkPlugin } from "@lexical/react/LexicalLinkPlugin";
import { ListPlugin } from "@lexical/react/LexicalListPlugin";
import { MarkdownShortcutPlugin } from "@lexical/react/LexicalMarkdownShortcutPlugin";
import { RichTextPlugin } from "@lexical/react/LexicalRichTextPlugin";
import { $createQuoteNode, $isQuoteNode } from "@lexical/rich-text";
import { $setBlocksType } from "@lexical/selection";
import { $getNearestNodeOfType, mergeRegister } from "@lexical/utils";
import * as Popover from "@radix-ui/react-popover";
import {
  $createParagraphNode, $createTextNode, $getRoot, $getSelection, $isRangeSelection, $isTextNode, CLEAR_EDITOR_COMMAND,
  COMMAND_PRIORITY_CRITICAL, COMMAND_PRIORITY_HIGH, FORMAT_TEXT_COMMAND, KEY_ARROW_DOWN_COMMAND, KEY_ARROW_UP_COMMAND,
  KEY_DOWN_COMMAND, KEY_ENTER_COMMAND, KEY_ESCAPE_COMMAND, KEY_TAB_COMMAND, PASTE_COMMAND, type LexicalEditor, type TextFormatType,
} from "lexical";
import { Bold, Code, Italic, Link, List, ListOrdered, Quote, SquareCode, Strikethrough } from "lucide-react";
import {
  forwardRef, useCallback, useEffect, useId, useImperativeHandle, useMemo, useRef, useState, type ReactNode,
} from "react";
import { ok, type User } from "../api/client";
import { useController, useMode, usePortal } from "../app/context";
import { Avatar, Button, IconButton, ui } from "../components/ui";
import viewStyles from "../views/views.module.css";
import { $exportMessage, $importMessage, editorNodes, messageTransformers } from "./markdown";
import { $createMentionNode, $isMentionNode } from "./MentionNode";
import styles from "./editor.module.css";

export const MAX_MESSAGE_LENGTH = 20_000;

export interface RichEditorHandle {
  focus(): void;
  clear(): void;
  insertText(text: string): void;
  getMarkdown(): string;
}

export interface RichEditorProps {
  ariaLabel: string;
  placeholder: string;
  initialMarkdown?: string;
  toolbar?: boolean;
  compact?: boolean;
  members: User[];
  allowChannelMention: boolean;
  /** Enter (without Shift) outside a code block. */
  onSubmit(markdown: string): void;
  onChange?(markdown: string, isEmpty: boolean): void;
  onEscape?(): void;
  /** Up arrow in an empty editor: edit the last message you sent, like Teams. */
  onArrowUpWhenEmpty?(): void;
  onFiles?(files: File[]): void;
  /** Extra controls on the right of the toolbar row (attach, emoji). */
  toolbarExtras?: ReactNode;
}

const theme = {
  paragraph: styles.paragraph,
  quote: styles.quote,
  code: styles.code,
  link: styles.link,
  list: { ul: styles.ul, ol: styles.ol, listitem: styles.listItem, nested: { listitem: styles.nestedListItem } },
  text: { bold: styles.bold, italic: styles.italic, strikethrough: styles.strikethrough, code: styles.inlineCode },
};

/** A rich-text box like Teams: formatting shows as you type, and the result is stored as Markdown. */
export const RichEditor = forwardRef<RichEditorHandle, RichEditorProps>(function RichEditor(props, ref) {
  const controller = useController();
  const nameOf = useCallback((id: string) => controller.store.getState().users[id]?.displayName, [controller]);
  const transformers = useMemo(() => messageTransformers(nameOf), [nameOf]);
  const initialMarkdown = useRef(props.initialMarkdown);

  const initialConfig: InitialConfigType = useMemo(() => ({
    namespace: "mode-composer",
    nodes: editorNodes,
    theme,
    onError: (error: Error) => controller.toastError(error.message),
    editorState: initialMarkdown.current ? () => $importMessage(initialMarkdown.current!, transformers) : undefined,
  }), [controller, transformers]);

  return (
    <LexicalComposer initialConfig={initialConfig}>
      <EditorBody {...props} transformers={transformers} handleRef={ref} />
    </LexicalComposer>
  );
});

function EditorBody(props: RichEditorProps & {
  transformers: ReturnType<typeof messageTransformers>;
  handleRef: React.ForwardedRef<RichEditorHandle>;
}) {
  const { transformers, handleRef } = props;
  const [editor] = useLexicalComposerContext();
  const [mentionAria, setMentionAria] = useState<{ listId: string; activeId: string } | null>(null);

  useImperativeHandle(handleRef, () => ({
    focus: () => editor.focus(),
    clear: () => editor.dispatchCommand(CLEAR_EDITOR_COMMAND, undefined),
    insertText: (text) => editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) selection.insertText(text);
      else $getRoot().selectEnd().insertText(text);
    }),
    getMarkdown: () => editor.getEditorState().read(() => $exportMessage(transformers)),
  }), [editor, transformers]);

  return (
    <div className={styles.root}>
      {props.toolbar && <Toolbar extras={props.toolbarExtras} />}
      <div style={{ position: "relative" }}>
        <RichTextPlugin
          contentEditable={
            <ContentEditable
              className={`${styles.input} ${props.compact ? styles.compact : ""}`}
              ariaLabel={props.ariaLabel}
              ariaMultiline
              ariaAutoComplete="list"
              ariaControls={mentionAria?.listId}
              ariaActiveDescendant={mentionAria?.activeId}
              aria-placeholder={props.placeholder}
              placeholder={<div className={styles.placeholder}>{props.placeholder}</div>}
            />
          }
          ErrorBoundary={LexicalErrorBoundary}
        />
        <MentionsPlugin members={props.members} allowChannelMention={props.allowChannelMention} onAriaChange={setMentionAria} />
      </div>
      <HistoryPlugin />
      <ListPlugin />
      <LinkPlugin validateUrl={isSafeUrl} />
      <MarkdownShortcutPlugin transformers={transformers} />
      <ClearEditorPlugin />
      <KeysPlugin {...props} transformers={transformers} />
      <ChangePlugin onChange={props.onChange} transformers={transformers} />
    </div>
  );
}

function isSafeUrl(url: string): boolean {
  return /^(https?:\/\/|mailto:)/i.test(url.trim());
}

function $isEmpty(): boolean {
  return $getRoot().getTextContent().trim().length === 0;
}

function ChangePlugin({ onChange, transformers }: { onChange?: RichEditorProps["onChange"]; transformers: ReturnType<typeof messageTransformers> }) {
  const [editor] = useLexicalComposerContext();
  useEffect(() => {
    if (!onChange) return;
    return editor.registerUpdateListener(({ editorState, dirtyElements, dirtyLeaves }) => {
      if (dirtyElements.size === 0 && dirtyLeaves.size === 0) return;
      editorState.read(() => onChange($exportMessage(transformers), $isEmpty()));
    });
  }, [editor, onChange, transformers]);
  return null;
}

function KeysPlugin({ onSubmit, onEscape, onArrowUpWhenEmpty, onFiles, transformers }: RichEditorProps & {
  transformers: ReturnType<typeof messageTransformers>;
}) {
  const [editor] = useLexicalComposerContext();

  useEffect(() => mergeRegister(
    editor.registerCommand(KEY_ENTER_COMMAND, (event) => {
      if (!event || event.shiftKey || event.isComposing) return false;
      const selection = $getSelection();
      // Inside a code block Enter adds a line, as in Teams.
      if ($isRangeSelection(selection) && $isCodeNode(selection.anchor.getNode().getTopLevelElement())) return false;
      event.preventDefault();
      onSubmit($exportMessage(transformers));
      return true;
    }, COMMAND_PRIORITY_HIGH),
    editor.registerCommand(KEY_ESCAPE_COMMAND, () => {
      if (!onEscape) return false;
      onEscape();
      return true;
    }, COMMAND_PRIORITY_HIGH),
    editor.registerCommand(KEY_ARROW_UP_COMMAND, (event) => {
      if (!onArrowUpWhenEmpty || !$isEmpty()) return false;
      event?.preventDefault();
      onArrowUpWhenEmpty();
      return true;
    }, COMMAND_PRIORITY_HIGH),
    // Underline has no Markdown form, so it would be lost when sent.
    editor.registerCommand(FORMAT_TEXT_COMMAND, (format) => format === "underline", COMMAND_PRIORITY_HIGH),
    editor.registerCommand(KEY_DOWN_COMMAND, (event) => {
      const mod = event.ctrlKey || event.metaKey;
      if (mod && !event.shiftKey && event.key.toLowerCase() === "e") {
        event.preventDefault();
        editor.dispatchCommand(FORMAT_TEXT_COMMAND, "code");
        return true;
      }
      if (mod && event.shiftKey && event.key.toLowerCase() === "x") {
        event.preventDefault();
        editor.dispatchCommand(FORMAT_TEXT_COMMAND, "strikethrough");
        return true;
      }
      return false;
    }, COMMAND_PRIORITY_HIGH),
    editor.registerCommand(PASTE_COMMAND, (event) => {
      const files = event instanceof ClipboardEvent ? Array.from(event.clipboardData?.files ?? []) : [];
      if (!onFiles || files.length === 0) return false;
      event.preventDefault();
      onFiles(files);
      return true;
    }, COMMAND_PRIORITY_HIGH),
  ), [editor, onSubmit, onEscape, onArrowUpWhenEmpty, onFiles, transformers]);
  return null;
}

type BlockType = "paragraph" | "quote" | "code" | "bullet" | "number";

function Toolbar({ extras }: { extras?: ReactNode }) {
  const [editor] = useLexicalComposerContext();
  const [formats, setFormats] = useState<Record<string, boolean>>({});
  const [block, setBlock] = useState<BlockType>("paragraph");
  const [isLink, setIsLink] = useState(false);

  useEffect(() => editor.registerUpdateListener(({ editorState }) => editorState.read(() => {
    const selection = $getSelection();
    if (!$isRangeSelection(selection)) return;
    setFormats({
      bold: selection.hasFormat("bold"),
      italic: selection.hasFormat("italic"),
      strikethrough: selection.hasFormat("strikethrough"),
      code: selection.hasFormat("code"),
    });
    const anchor = selection.anchor.getNode();
    const top = anchor.getKey() === "root" ? null : anchor.getTopLevelElement();
    const list = $getNearestNodeOfType(anchor, ListNode);
    setBlock(list ? (list.getListType() === "number" ? "number" : "bullet")
      : $isQuoteNode(top) ? "quote" : $isCodeNode(top) ? "code" : "paragraph");
    setIsLink($isLinkNode(anchor.getParent()) || $isLinkNode(anchor));
  })), [editor]);

  const format = (type: TextFormatType) => editor.dispatchCommand(FORMAT_TEXT_COMMAND, type);
  const setBlockType = (target: "quote" | "code") => editor.update(() => {
    const selection = $getSelection();
    if (!$isRangeSelection(selection)) return;
    $setBlocksType(selection, () => (block === target ? $createParagraphNode() : target === "quote" ? $createQuoteNode() : $createCodeNode()));
  });
  const toggleList = (type: "bullet" | "number") => {
    if (block === type) editor.dispatchCommand(REMOVE_LIST_COMMAND, undefined);
    else editor.dispatchCommand(type === "bullet" ? INSERT_UNORDERED_LIST_COMMAND : INSERT_ORDERED_LIST_COMMAND, undefined);
  };

  const keep = (e: React.MouseEvent) => e.preventDefault();
  return (
    <div className={styles.toolbar} role="toolbar" aria-label="Formatting" onMouseDown={keep}>
      <IconButton label="Bold (Ctrl+B)" size="small" aria-pressed={!!formats.bold} onClick={() => format("bold")}><Bold size={16} aria-hidden /></IconButton>
      <IconButton label="Italic (Ctrl+I)" size="small" aria-pressed={!!formats.italic} onClick={() => format("italic")}><Italic size={16} aria-hidden /></IconButton>
      <IconButton label="Strikethrough (Ctrl+Shift+X)" size="small" aria-pressed={!!formats.strikethrough} onClick={() => format("strikethrough")}>
        <Strikethrough size={16} aria-hidden />
      </IconButton>
      <IconButton label="Inline code (Ctrl+E)" size="small" aria-pressed={!!formats.code} onClick={() => format("code")}><Code size={16} aria-hidden /></IconButton>
      <LinkButton editor={editor} active={isLink} />
      <span className={styles.divider} aria-hidden />
      <IconButton label="Bulleted list" size="small" aria-pressed={block === "bullet"} onClick={() => toggleList("bullet")}><List size={16} aria-hidden /></IconButton>
      <IconButton label="Numbered list" size="small" aria-pressed={block === "number"} onClick={() => toggleList("number")}><ListOrdered size={16} aria-hidden /></IconButton>
      <IconButton label="Quote" size="small" aria-pressed={block === "quote"} onClick={() => setBlockType("quote")}><Quote size={16} aria-hidden /></IconButton>
      <IconButton label="Code block" size="small" aria-pressed={block === "code"} onClick={() => setBlockType("code")}><SquareCode size={16} aria-hidden /></IconButton>
      {extras}
    </div>
  );
}

function LinkButton({ editor, active }: { editor: LexicalEditor; active: boolean }) {
  const portal = usePortal();
  const [open, setOpen] = useState(false);
  const [url, setUrl] = useState("https://");
  const [error, setError] = useState<string | null>(null);
  const id = useId();

  const apply = () => {
    if (!isSafeUrl(url)) {
      setError("Use a link starting with https://, http:// or mailto:");
      return;
    }
    editor.update(() => {
      const selection = $getSelection();
      if ($isRangeSelection(selection) && selection.isCollapsed()) {
        const text = $createTextNode(url.trim());
        selection.insertNodes([text]);
        text.select(0, text.getTextContentSize());
      }
    });
    editor.dispatchCommand(TOGGLE_LINK_COMMAND, url.trim());
    setOpen(false);
    editor.focus();
  };

  if (active) {
    return (
      <IconButton label="Remove link" size="small" aria-pressed onClick={() => editor.dispatchCommand(TOGGLE_LINK_COMMAND, null)}>
        <Link size={16} aria-hidden />
      </IconButton>
    );
  }
  return (
    <Popover.Root open={open} onOpenChange={(next) => { setOpen(next); setError(null); if (next) setUrl("https://"); }}>
      <Popover.Trigger asChild>
        <IconButton label="Link" size="small"><Link size={16} aria-hidden /></IconButton>
      </Popover.Trigger>
      <Popover.Portal container={portal}>
        <Popover.Content className={styles.linkForm} side="top" align="start" sideOffset={6}
          onOpenAutoFocus={(e) => { e.preventDefault(); document.getElementById(id)?.focus(); }}>
          <label className={ui.label} htmlFor={id}>Link address</label>
          <input id={id} className={ui.input} value={url} onChange={(e) => setUrl(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); apply(); } }} aria-invalid={error ? true : undefined} />
          {error && <span className={ui.error} role="alert">{error}</span>}
          <div className={styles.linkActions}>
            <Button variant="ghost" onClick={() => setOpen(false)}>Cancel</Button>
            <Button variant="primary" onClick={apply}>Add link</Button>
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

interface Suggestion {
  id: string;
  label: string;
  meta?: string;
  user?: User;
}

/** "@" opens people suggestions: people in this chat first, then the directory. */
function MentionsPlugin({ members, allowChannelMention, onAriaChange }: {
  members: User[]; allowChannelMention: boolean; onAriaChange: (aria: { listId: string; activeId: string } | null) => void;
}) {
  const [editor] = useLexicalComposerContext();
  const controller = useController();
  const me = useMode((s) => s.me);
  const isGuest = useMode((s) => s.isGuest);
  const [query, setQuery] = useState<string | null>(null);
  const [directory, setDirectory] = useState<User[]>([]);
  const [active, setActive] = useState(0);
  const listId = useId();

  useEffect(() => editor.registerUpdateListener(({ editorState }) => editorState.read(() => {
    const selection = $getSelection();
    if (!$isRangeSelection(selection) || !selection.isCollapsed()) return setQuery(null);
    const node = selection.anchor.getNode();
    if (!$isTextNode(node) || $isMentionNode(node)) return setQuery(null);
    const before = node.getTextContent().slice(0, selection.anchor.offset);
    const match = /(^|\s)@([^\s@{}]{0,40})$/.exec(before);
    setQuery(match ? match[2] : null);
  })), [editor]);

  useEffect(() => {
    const q = query?.trim() ?? "";
    if (query === null || isGuest || q.length < 2) {
      setDirectory([]);
      return;
    }
    const handle = setTimeout(() => {
      void ok(controller.api.GET("/api/v1/users/search", { params: { query: { q, limit: 6 } } })).then(setDirectory, () => setDirectory([]));
    }, 200);
    return () => clearTimeout(handle);
  }, [query, isGuest, controller]);

  const suggestions = useMemo<Suggestion[]>(() => {
    if (query === null) return [];
    const q = query.toLowerCase();
    const matches = (u: User) => u.displayName.toLowerCase().includes(q) || (u.email ?? "").toLowerCase().startsWith(q);
    const inRoom = members.filter((u) => u.id !== me?.id && matches(u));
    const others = directory.filter((u) => u.id !== me?.id && !inRoom.some((m) => m.id === u.id));
    const list: Suggestion[] = [
      ...inRoom.map((u) => ({ id: u.id, label: u.displayName, meta: "In this conversation", user: u })),
      ...others.map((u) => ({ id: u.id, label: u.displayName, meta: u.email ?? undefined, user: u })),
    ];
    if (allowChannelMention && "channel".startsWith(q)) list.unshift({ id: "channel", label: "channel", meta: "Notify everyone here" });
    return list.slice(0, 8);
  }, [query, members, directory, me?.id, allowChannelMention]);

  useEffect(() => setActive(0), [query]);
  const open = suggestions.length > 0;
  useEffect(() => onAriaChange(open ? { listId, activeId: `${listId}-${active}` } : null), [open, active, listId, onAriaChange]);

  const pick = useCallback((s: Suggestion) => {
    editor.update(() => {
      const selection = $getSelection();
      if (!$isRangeSelection(selection)) return;
      const node = selection.anchor.getNode();
      if (!$isTextNode(node) || query === null) return;
      const end = selection.anchor.offset;
      const start = end - query.length - 1;
      const parts = node.splitText(start, end);
      const target = start === 0 ? parts[0] : parts[1];
      const mention = $createMentionNode(s.id, `@${s.label}`);
      target.replace(mention);
      const space = $createTextNode(" ");
      mention.insertAfter(space);
      space.select(1, 1);
    });
    if (s.user) controller.rememberUsers([s.user]);
    setQuery(null);
  }, [editor, query, controller]);

  useEffect(() => {
    if (!open) return;
    const take = (handler: () => void) => (event: KeyboardEvent | null) => {
      event?.preventDefault();
      handler();
      return true;
    };
    return mergeRegister(
      editor.registerCommand(KEY_ARROW_DOWN_COMMAND, take(() => setActive((a) => (a + 1) % suggestions.length)), COMMAND_PRIORITY_CRITICAL),
      editor.registerCommand(KEY_ARROW_UP_COMMAND, take(() => setActive((a) => (a - 1 + suggestions.length) % suggestions.length)), COMMAND_PRIORITY_CRITICAL),
      editor.registerCommand(KEY_ENTER_COMMAND, take(() => pick(suggestions[active])), COMMAND_PRIORITY_CRITICAL),
      editor.registerCommand(KEY_TAB_COMMAND, take(() => pick(suggestions[active])), COMMAND_PRIORITY_CRITICAL),
      editor.registerCommand(KEY_ESCAPE_COMMAND, take(() => setQuery(null)), COMMAND_PRIORITY_CRITICAL),
    );
  }, [open, editor, suggestions, active, pick]);

  if (!open) return null;
  return (
    <ul id={listId} role="listbox" aria-label="Mention someone" className={viewStyles.popover}
      style={{ position: "absolute", bottom: "100%", left: 8, margin: "0 0 6px", listStyle: "none" }}>
      {suggestions.map((s, i) => (
        <li key={s.id} id={`${listId}-${i}`} role="option" aria-selected={i === active} className={viewStyles.option}
          onMouseDown={(e) => { e.preventDefault(); pick(s); }} onMouseEnter={() => setActive(i)}>
          {s.user ? <Avatar id={s.user.id} name={s.user.displayName} avatarUrl={s.user.avatarUrl} size={24} />
            : <span style={{ width: 24, textAlign: "center" }}>@</span>}
          <span>{s.label}</span>
          {s.meta && <span className={viewStyles.optionMeta}>{s.meta}</span>}
        </li>
      ))}
    </ul>
  );
}
