import { AtSign, Bell, CheckCheck, Hash, Heart, MessageSquare, MessageSquareReply, PanelLeftClose, UserPlus } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import { ok, type ActivityFeedItem, type RoomRef } from "../api/client";
import { useController, useMode } from "../app/context";
import { Avatar, Button, EmptyState, IconButton, Skeleton, ui } from "../components/ui";
import { formatListTime } from "../lib/format";
import { rovingKeyDown } from "../lib/roving";
import styles from "./views.module.css";

type Filter = "all" | "unread" | "mentions" | "replies" | "reactions";

const filters: { value: Filter; label: string; types?: string }[] = [
  { value: "all", label: "All" },
  { value: "unread", label: "Unread" },
  { value: "mentions", label: "Mentions", types: "Mention,ChannelMention" },
  { value: "replies", label: "Replies", types: "Reply" },
  { value: "reactions", label: "Reactions", types: "Reaction" },
];

const typeIcons: Record<string, typeof Bell> = {
  Mention: AtSign, ChannelMention: AtSign, Reply: MessageSquareReply, Reaction: Heart, ChatMessage: MessageSquare,
  AddedToChannel: Hash, AddedToWorkspace: UserPlus,
};

export function ActivityList() {
  const controller = useController();
  const unreadCount = useMode((s) => s.activityUnread);
  const [filter, setFilter] = useState<Filter>("all");
  const [items, setItems] = useState<ActivityFeedItem[] | null>(null);
  const [nextBefore, setNextBefore] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const seenUnread = useRef(unreadCount);

  const load = useCallback(async (before: string | null) => {
    const selected = filters.find((f) => f.value === filter)!;
    const page = await ok(controller.api.GET("/api/v1/activity", {
      params: {
        query: {
          limit: 30,
          ...(selected.types ? { type: selected.types } : {}),
          ...(filter === "unread" ? { unreadOnly: true } : {}),
          ...(before ? { before } : {}),
        },
      },
    }));
    setItems((current) => (before ? [...(current ?? []), ...page.items] : page.items));
    setNextBefore(page.nextBefore ?? null);
  }, [controller, filter]);

  useEffect(() => {
    setItems(null);
    void load(null).catch((e: Error) => controller.toastError(e.message));
  }, [load, controller]);

  // A new item arrived: refresh the first page.
  useEffect(() => {
    if (unreadCount > seenUnread.current) void load(null).catch(() => undefined);
    seenUnread.current = unreadCount;
  }, [unreadCount, load]);

  const open = async (item: ActivityFeedItem) => {
    if (!item.isRead) {
      setItems((list) => list?.map((i) => (i.id === item.id ? { ...i, isRead: true } : i)) ?? null);
      void controller.api.POST("/api/v1/activity/{activityId}/read", { params: { path: { activityId: item.id } } });
    }
    const room: RoomRef | null = item.channelId ? { type: "Channel", id: item.channelId }
      : item.conversationId ? { type: "Conversation", id: item.conversationId } : null;
    if (room && item.messageId) await controller.revealMessage(room, item.messageId);
    else if (room) controller.openRoom(room);
    else controller.openSection("channels");
  };

  const markAll = async () => {
    await ok(controller.api.POST("/api/v1/activity/read-all"));
    setItems((list) => (filter === "unread" ? [] : list?.map((i) => ({ ...i, isRead: true })) ?? null));
  };

  return (
    <>
      <div className={styles.listHeader}>
        <h2 className={styles.listTitle}>Activity</h2>
        <IconButton label="Mark all as read" disabled={unreadCount === 0} onClick={() => void markAll()}><CheckCheck size={18} aria-hidden /></IconButton>
        <IconButton label="Hide list" onClick={() => controller.setListOpen(false)}><PanelLeftClose size={18} aria-hidden /></IconButton>
      </div>
      <div className={styles.filters} role="group" aria-label="Filter activity">
        {filters.map((f) => (
          <button key={f.value} type="button" className={ui.chip} aria-pressed={filter === f.value} onClick={() => setFilter(f.value)}>{f.label}</button>
        ))}
      </div>
      <div className={styles.listBody} onKeyDown={rovingKeyDown}>
        {items === null ? (
          <div style={{ display: "grid", gap: 16, padding: 12 }} aria-busy="true">
            {[0, 1, 2, 3, 4].map((i) => <Skeleton key={i} width="100%" height={40} />)}
          </div>
        ) : items.length === 0 ? (
          <EmptyState icon={<Bell />} title={filter === "all" ? "You're all caught up" : "Nothing here"}>
            Mentions, replies, reactions and new chats will show up here.
          </EmptyState>
        ) : (
          <ul style={{ listStyle: "none", margin: 0, padding: 0 }}>
            {items.map((item) => {
              const Icon = typeIcons[item.type] ?? Bell;
              return (
                <li key={item.id}>
                  <button type="button" data-roving className={styles.activityItem} data-unread={!item.isRead} onClick={() => void open(item)}
                    aria-label={`${item.isRead ? "" : "Unread. "}${item.title}. ${item.body}`}>
                    <span style={{ position: "relative" }}>
                      <Avatar id={item.actor.id} name={item.actor.displayName} avatarUrl={item.actor.avatarUrl} size={36} />
                      <Icon size={14} aria-hidden style={{
                        position: "absolute", right: -4, bottom: -4, padding: 2, borderRadius: 999,
                        background: "var(--m-surface-raised)", color: "var(--m-text-accent)", width: 18, height: 18,
                      }} />
                    </span>
                    <span className={styles.activityText}>
                      <span className={styles.activityTitle}>{item.title}</span>
                      {item.body && <span className={styles.activityBody}>{item.body}</span>}
                    </span>
                    <time className={styles.roomTime} dateTime={item.createdAt}>{formatListTime(item.createdAt)}</time>
                  </button>
                </li>
              );
            })}
          </ul>
        )}
        {nextBefore && items && items.length > 0 && (
          <div style={{ display: "grid", padding: 12 }}>
            <Button disabled={loadingMore} onClick={() => {
              setLoadingMore(true);
              void load(nextBefore).finally(() => setLoadingMore(false));
            }}>Show older</Button>
          </div>
        )}
      </div>
    </>
  );
}
