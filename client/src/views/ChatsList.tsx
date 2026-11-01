import { Eye, EyeOff, MessageSquarePlus, MoreHorizontal, PanelLeftClose, Pin, PinOff, Users } from "lucide-react";
import { useState } from "react";
import { useShallow } from "zustand/react/shallow";
import type { Conversation } from "../api/client";
import { useController, useMode } from "../app/context";
import { Avatar, Button, EmptyState, IconButton, Menu, MenuItem } from "../components/ui";
import { NewChatDialog } from "../components/RoomDialogs";
import { displayName } from "../store/controller";
import { rovingKeyDown } from "../lib/roving";
import { RoomListItem } from "./RoomListItem";
import styles from "./views.module.css";

export function ChatsList() {
  const controller = useController();
  const { conversations, me, presence } = useMode(useShallow((s) => ({ conversations: s.conversations, me: s.me, presence: s.presence })));
  const [newChat, setNewChat] = useState(false);
  const [showHidden, setShowHidden] = useState(false);

  const pinned = conversations.filter((c) => c.isPinned && !c.isHidden);
  const recent = conversations.filter((c) => !c.isPinned && !c.isHidden);
  const hidden = conversations.filter((c) => c.isHidden);

  const settings = (c: Conversation, change: { isPinned?: boolean; isHidden?: boolean }) =>
    void controller.setChatSettings(c.id, change).catch((e: Error) => controller.toastError(e.message));

  const item = (c: Conversation) => {
    const name = displayName(c, me?.id);
    const others = c.members.filter((m) => m.id !== me?.id);
    const direct = c.type === "Direct" && others.length === 1 ? others[0] : null;
    return (
      <RoomListItem key={c.id} room={{ type: "Conversation", id: c.id }} name={name}
        icon={direct
          ? <Avatar id={direct.id} name={direct.displayName} avatarUrl={direct.avatarUrl} size={28} status={presence[direct.id]?.status ?? "Offline"} />
          : <span style={{ width: 28, display: "grid", placeItems: "center" }}><Users size={18} aria-hidden /></span>}
        preview={c.type === "Group" ? `${c.members.length} people` : (presence[direct?.id ?? ""]?.message ?? "")}
        menu={
          <Menu align="end" trigger={<IconButton label={`Options for ${name}`} size="small"><MoreHorizontal size={16} aria-hidden /></IconButton>}>
            {c.isPinned
              ? <MenuItem onSelect={() => settings(c, { isPinned: false })}><PinOff size={16} aria-hidden /> Unpin</MenuItem>
              : <MenuItem onSelect={() => settings(c, { isPinned: true })}><Pin size={16} aria-hidden /> Pin</MenuItem>}
            {c.isHidden
              ? <MenuItem onSelect={() => settings(c, { isHidden: false })}><Eye size={16} aria-hidden /> Show</MenuItem>
              : <MenuItem onSelect={() => settings(c, { isHidden: true })}><EyeOff size={16} aria-hidden /> Hide</MenuItem>}
          </Menu>
        } />
    );
  };

  return (
    <>
      <div className={styles.listHeader}>
        <h2 className={styles.listTitle}>Chat</h2>
        <IconButton label="New chat" onClick={() => setNewChat(true)}><MessageSquarePlus size={18} aria-hidden /></IconButton>
        <IconButton label="Hide list" onClick={() => controller.setListOpen(false)}><PanelLeftClose size={18} aria-hidden /></IconButton>
      </div>
      <div className={styles.listBody} onKeyDown={rovingKeyDown}>
        {conversations.length === 0 ? (
          <EmptyState icon={<MessageSquarePlus />} title="No chats yet" action={<Button variant="primary" onClick={() => setNewChat(true)}>Start a chat</Button>}>
            Message one person, or a small group, outside any channel.
          </EmptyState>
        ) : (
          <>
            {pinned.length > 0 && (
              <section aria-labelledby="mode-chats-pinned">
                <h3 id="mode-chats-pinned" className={styles.listSectionLabel}>Pinned</h3>
                <ul className={styles.roomList}>{pinned.map(item)}</ul>
              </section>
            )}
            <section aria-labelledby="mode-chats-recent">
              <h3 id="mode-chats-recent" className={pinned.length > 0 ? styles.listSectionLabel : "m-visually-hidden"}>Recent</h3>
              <ul className={styles.roomList}>{recent.map(item)}</ul>
            </section>
            {hidden.length > 0 && (
              <section aria-label="Hidden chats">
                <button type="button" className={styles.hiddenToggle} aria-expanded={showHidden} onClick={() => setShowHidden(!showHidden)}>
                  {showHidden ? "Hide" : "Show"} hidden chats ({hidden.length})
                </button>
                {showHidden && <ul className={styles.roomList}>{hidden.map(item)}</ul>}
              </section>
            )}
          </>
        )}
      </div>
      <NewChatDialog open={newChat} onOpenChange={setNewChat} />
    </>
  );
}
