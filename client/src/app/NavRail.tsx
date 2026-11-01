import { Bell, FileText, Hash, MessageSquare, Search, Settings } from "lucide-react";
import { useState } from "react";
import { useShallow } from "zustand/react/shallow";
import type { PresenceStatus } from "../api/client";
import { Avatar, Badge, Menu, MenuItem, MenuLabel, MenuSeparator, PresenceDot, presenceLabel } from "../components/ui";
import { SettingsDialog } from "../components/SettingsDialog";
import { StatusMessageDialog } from "../components/StatusMessageDialog";
import type { Section } from "../store/store";
import { features } from "./features";
import { useController, useMode } from "./context";
import styles from "./AppShell.module.css";

const items: { section: Section; label: string; icon: typeof Bell }[] = [
  ...(features.activity ? [{ section: "activity" as const, label: "Activity", icon: Bell }] : []),
  { section: "chat", label: "Chat", icon: MessageSquare },
  ...(features.channels ? [{ section: "channels" as const, label: "Channels", icon: Hash }] : []),
  { section: "files", label: "Files", icon: FileText },
];

const statuses: PresenceStatus[] = ["Available", "Busy", "DoNotDisturb", "Away"];

export function NavRail() {
  const controller = useController();
  const { section, activityUnread, chatUnread, channelUnread, me, myPresence, searching } = useMode(useShallow((s) => {
    const unread = Object.values(s.unread).filter((u) => !u.isMuted || u.mentionCount > 0);
    return {
      section: s.section,
      activityUnread: s.activityUnread,
      chatUnread: unread.filter((u) => u.room.type === "Conversation" && u.unreadCount > 0).length,
      channelUnread: unread.filter((u) => u.room.type === "Channel").reduce((n, u) => n + u.mentionCount, 0),
      me: s.me,
      myPresence: s.me ? s.presence[s.me.id] : undefined,
      searching: s.searchQuery !== null,
    };
  }));
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [statusOpen, setStatusOpen] = useState(false);
  const counts: Record<Section, number> = { activity: activityUnread, chat: chatUnread, channels: channelUnread, files: 0 };

  const open = (target: Section) => {
    controller.openSection(target);
    controller.setListOpen(true);
  };

  return (
    <nav className={styles.rail} aria-label="Mode">
      {items.map(({ section: target, label, icon: Icon }) => (
        <button key={target} type="button" className={styles.railItem} aria-current={section === target && !searching ? "page" : undefined}
          onClick={() => open(target)}>
          <Icon size={20} aria-hidden />
          <span>{label}</span>
          <span className={styles.railBadge}>
            <Badge count={counts[target]} label={`${counts[target]} unread`} />
          </span>
        </button>
      ))}
      <button type="button" className={styles.railItem} onClick={() => controller.openCommandBar(true)} aria-keyshortcuts="Control+K Meta+K">
        <Search size={20} aria-hidden />
        <span>Search</span>
      </button>
      <span className={styles.railSpacer} />
      {me && (
        <Menu align="start" trigger={
          <button type="button" className={styles.meButton} aria-label={`${me.displayName}, ${presenceLabel(myPresence?.status ?? "Available")}. Status and settings`}>
            <Avatar id={me.id} name={me.displayName} avatarUrl={me.avatarUrl} size={36} status={myPresence?.status ?? "Available"} />
          </button>
        }>
          <MenuLabel>{me.displayName}{myPresence?.message ? ` · ${myPresence.message}` : ""}</MenuLabel>
          {statuses.map((status) => (
            <MenuItem key={status} onSelect={() => void controller.setPresence(status, myPresence?.message ?? null)}>
              <span style={{ position: "relative", width: 12, height: 12 }}><PresenceDot status={status} /></span>
              {presenceLabel(status)}
            </MenuItem>
          ))}
          <MenuSeparator />
          <MenuItem onSelect={() => setStatusOpen(true)}>Set status message</MenuItem>
          <MenuItem onSelect={() => setSettingsOpen(true)}><Settings size={16} aria-hidden /> Notification settings</MenuItem>
        </Menu>
      )}
      <SettingsDialog open={settingsOpen} onOpenChange={setSettingsOpen} />
      <StatusMessageDialog open={statusOpen} onOpenChange={setStatusOpen} />
    </nav>
  );
}
