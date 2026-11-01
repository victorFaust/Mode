import { MessageSquarePlus, PanelLeftClose, Users } from "lucide-react";
import { useState } from "react";
import { useShallow } from "zustand/react/shallow";
import { useController, useMode } from "../app/context";
import { Avatar, Button, EmptyState, IconButton } from "../components/ui";
import { NewChatDialog } from "../components/RoomDialogs";
import { displayName } from "../store/controller";
import { rovingKeyDown } from "../lib/roving";
import { RoomListItem } from "./RoomListItem";
import styles from "./views.module.css";

export function ChatsList() {
  const controller = useController();
  const { conversations, me, presence } = useMode(useShallow((s) => ({ conversations: s.conversations, me: s.me, presence: s.presence })));
  const [newChat, setNewChat] = useState(false);

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
          <ul className={styles.roomList}>
            {conversations.map((c) => {
              const others = c.members.filter((m) => m.id !== me?.id);
              const direct = c.type === "Direct" && others.length === 1 ? others[0] : null;
              return (
                <RoomListItem key={c.id} room={{ type: "Conversation", id: c.id }} name={displayName(c, me?.id)}
                  icon={direct
                    ? <Avatar id={direct.id} name={direct.displayName} avatarUrl={direct.avatarUrl} size={28} status={presence[direct.id]?.status ?? "Offline"} />
                    : <span style={{ width: 28, display: "grid", placeItems: "center" }}><Users size={18} aria-hidden /></span>}
                  preview={c.type === "Group" ? `${c.members.length} people` : (presence[direct?.id ?? ""]?.message ?? "")} />
              );
            })}
          </ul>
        )}
      </div>
      <NewChatDialog open={newChat} onOpenChange={setNewChat} />
    </>
  );
}
