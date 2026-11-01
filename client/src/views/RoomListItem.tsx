import { BellOff } from "lucide-react";
import type { ReactNode } from "react";
import { useShallow } from "zustand/react/shallow";
import { roomKey, type RoomRef } from "../api/client";
import { useController, useMode } from "../app/context";
import { Badge } from "../components/ui";
import styles from "./views.module.css";

/** One channel or chat in a list, with unread, mention and mute state. */
export function RoomListItem({ room, name, icon, preview, time }: {
  room: RoomRef; name: string; icon: ReactNode; preview?: string; time?: string;
}) {
  const controller = useController();
  const key = roomKey(room);
  const { current, unread, mentions, muted } = useMode(useShallow((s) => ({
    current: s.room?.id === room.id && s.searchQuery === null,
    unread: s.unread[key]?.unreadCount ?? 0,
    mentions: s.unread[key]?.mentionCount ?? 0,
    muted: s.unread[key]?.isMuted ?? false,
  })));
  const hasUnread = unread > 0 && !muted;
  const label = [name, mentions > 0 && `${mentions} mention${mentions > 1 ? "s" : ""}`, hasUnread && `${unread} unread`, muted && "muted"]
    .filter(Boolean).join(", ");

  return (
    <li>
      <button type="button" data-roving className={styles.roomItem} aria-current={current ? "true" : undefined}
        data-unread={hasUnread} data-muted={muted} aria-label={label} onClick={() => controller.openRoom(room)}>
        {icon}
        {preview !== undefined ? (
          <span className={styles.roomTwoLine}>
            <span className={styles.roomName}>{name}</span>
            <span className={styles.roomPreview}>{preview}</span>
          </span>
        ) : <span className={styles.roomName}>{name}</span>}
        {time && <span className={styles.roomTime}>{time}</span>}
        {muted && <BellOff size={14} aria-hidden />}
        {mentions > 0 ? <Badge count={mentions} label={`${mentions} mentions`} /> : hasUnread && <Badge count={unread} muted label={`${unread} unread`} />}
      </button>
    </li>
  );
}
