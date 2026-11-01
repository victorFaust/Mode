import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDown, Loader2 } from "lucide-react";
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import type { RoomRef } from "../api/client";
import { useController, useMode } from "../app/context";
import { MessageSkeleton } from "../components/ShellSkeleton";
import { Button } from "../components/ui";
import { formatDay } from "../lib/format";
import { buildRows, type Row } from "../lib/rows";
import { rovingKeyDown } from "../lib/roving";
import { roomOf } from "../store/mutations";
import { MessageItem } from "./MessageItem";
import styles from "./views.module.css";

const BOTTOM_SLACK = 80;
const LOAD_OLDER_AT = 300;

function estimate(row: Row | undefined): number {
  if (!row) return 120;
  if (row.kind === "day") return 44;
  if (row.kind === "new") return 32;
  return row.grouped ? 28 : 64;
}

export function MessageList({ room, title, canPost, canManage }: { room: RoomRef; title: string; canPost: boolean; canManage: boolean }) {
  const controller = useController();
  const meId = useMode((s) => s.me?.id);
  const state = useMode((s) => roomOf(s, room));
  const rows = useMemo(() => buildRows(state.items, meId, state.lastReadOnOpen), [state.items, meId, state.lastReadOnOpen]);
  const scrollRef = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);
  const [showJump, setShowJump] = useState(false);
  const [highlight, setHighlight] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState("");
  const initialised = useRef(false);
  const firstKey = useRef<string | null>(null);
  const lastKey = useRef<string | null>(null);

  // Row 0 is the start-of-history header or the "loading older" indicator.
  const count = rows.length + 1;
  const virtualizer = useVirtualizer({
    count,
    getScrollElement: () => scrollRef.current,
    estimateSize: (i) => (i === 0 ? 120 : estimate(rows[i - 1])),
    getItemKey: (i) => (i === 0 ? "__start" : rows[i - 1].key),
    overscan: 10,
  });

  const scrollToBottom = useCallback(() => {
    virtualizer.scrollToIndex(count - 1, { align: "end" });
  }, [virtualizer, count]);

  // Initial position: the "New messages" marker if there is one, otherwise the newest message.
  useLayoutEffect(() => {
    if (initialised.current || !state.loaded) return;
    initialised.current = true;
    const marker = rows.findIndex((r) => r.kind === "new");
    if (marker >= 0) virtualizer.scrollToIndex(marker + 1, { align: "start" });
    else scrollToBottom();
  }, [state.loaded, rows, virtualizer, scrollToBottom]);

  // Keep position when older messages are prepended; follow new messages when already at the bottom.
  useLayoutEffect(() => {
    const newFirst = rows.find((r) => r.kind === "message")?.key ?? null;
    const newLast = rows.length ? rows[rows.length - 1].key : null;
    if (firstKey.current && newFirst !== firstKey.current) {
      const index = rows.findIndex((r) => r.key === firstKey.current);
      if (index > 0) virtualizer.scrollToIndex(index + 1, { align: "start" });
    }
    if (lastKey.current && newLast !== lastKey.current) {
      const last = rows[rows.length - 1];
      const fromMe = last?.kind === "message" && last.message.author.id === meId;
      if (atBottom.current || fromMe) scrollToBottom();
      else setShowJump(true);
      if (last?.kind === "message" && !fromMe && !last.message.pending) {
        setAnnouncement(`New message from ${last.message.author.displayName}`);
      }
    }
    firstKey.current = newFirst;
    lastKey.current = newLast;
  }, [rows, virtualizer, scrollToBottom, meId]);

  const reveal = useMode((s) => s.reveal);
  useEffect(() => {
    if (!reveal) return;
    const index = rows.findIndex((r) => r.key === reveal);
    if (index < 0) return;
    controller.clearReveal();
    virtualizer.scrollToIndex(index + 1, { align: "center" });
    setHighlight(reveal);
    const timer = setTimeout(() => setHighlight(null), 2000);
    return () => clearTimeout(timer);
  }, [reveal, rows, virtualizer, controller]);

  const onScroll = () => {
    const el = scrollRef.current;
    if (!el) return;
    const bottom = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_SLACK;
    if (bottom !== atBottom.current) {
      atBottom.current = bottom;
      controller.setAtBottom(room, bottom);
    }
    if (bottom) setShowJump(false);
    if (el.scrollTop < LOAD_OLDER_AT && state.hasOlder && !state.loading && state.loaded) void controller.loadOlder(room);
  };

  useEffect(() => {
    controller.setAtBottom(room, true);
    return () => controller.setAtBottom(room, false);
  }, [controller, room]);

  if (!state.loaded) {
    return (
      <div className={styles.scroller} aria-busy="true" aria-label={`Loading messages in ${title}`} style={{ contain: "none" }}>
        <div style={{ display: "grid", gap: 24, padding: 24, alignContent: "end", height: "100%" }}>
          {[0, 1, 2, 3].map((i) => <MessageSkeleton key={i} />)}
        </div>
      </div>
    );
  }

  return (
    <>
      <div ref={scrollRef} className={styles.scroller} onScroll={onScroll} role="log" aria-live="off" aria-label={`Messages in ${title}`}
        aria-busy={state.loading} tabIndex={0} onKeyDown={rovingKeyDown}>
        <div className={styles.virtualInner} style={{ height: virtualizer.getTotalSize() }}>
          {virtualizer.getVirtualItems().map((item) => {
            const row = item.index === 0 ? null : rows[item.index - 1];
            return (
              <div key={item.key} data-index={item.index} ref={virtualizer.measureElement} className={styles.virtualRow}
                style={{ transform: `translateY(${item.start}px)` }}>
                {row === null ? (
                  state.hasOlder ? (
                    <div className={styles.listStart} aria-hidden={!state.loading}>
                      {state.loading && <span style={{ display: "flex", gap: 8, alignItems: "center", color: "var(--m-text-muted)" }}>
                        <Loader2 size={16} aria-hidden /> Loading older messages…
                      </span>}
                    </div>
                  ) : (
                    <div className={styles.listStart}>
                      <p className={styles.listStartTitle}>{title}</p>
                      <p className={styles.listStartBody}>
                        {rows.length === 0 ? "No messages yet. Say hello to get things started." : "This is the start of the conversation."}
                      </p>
                    </div>
                  )
                ) : row.kind === "day" ? (
                  <div className={styles.day} role="separator">{formatDay(row.date)}</div>
                ) : row.kind === "new" ? (
                  <div className={styles.newMarker} role="separator">New messages</div>
                ) : (
                  <MessageItem message={row.message} grouped={row.grouped} canPost={canPost} canManage={canManage}
                    highlighted={highlight === row.message.id} />
                )}
              </div>
            );
          })}
        </div>
      </div>
      {showJump && (
        <Button className={styles.jump} onClick={() => { scrollToBottom(); setShowJump(false); }}>
          <ArrowDown size={16} aria-hidden /> Jump to latest
        </Button>
      )}
      <div className="m-visually-hidden" aria-live="polite">{announcement}</div>
    </>
  );
}
