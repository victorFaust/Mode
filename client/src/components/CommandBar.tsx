import { Command } from "cmdk";
import { Hash, Lock, MessageSquare, Search, User as UserIcon } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { useShallow } from "zustand/react/shallow";
import { ok, type User } from "../api/client";
import { useController, useMode, usePortal } from "../app/context";
import { displayName } from "../store/controller";
import { features } from "../app/features";
import styles from "./components.module.css";

/** Ctrl/Cmd + K: jump to a channel, chat or person, or search everything. */
export function CommandBar() {
  const controller = useController();
  const portal = usePortal();
  const { open, workspaces, channels, conversations, me, isGuest } = useMode(useShallow((s) => ({
    open: s.commandBarOpen, workspaces: s.workspaces, channels: s.channels, conversations: s.conversations, me: s.me, isGuest: s.isGuest,
  })));
  const [query, setQuery] = useState("");
  const [people, setPeople] = useState<User[]>([]);

  useEffect(() => {
    if (!open) setQuery("");
  }, [open]);

  useEffect(() => {
    const q = query.trim();
    if (isGuest || q.length < 2) {
      setPeople([]);
      return;
    }
    const handle = setTimeout(() => {
      void ok(controller.api.GET("/api/v1/users/search", { params: { query: { q, limit: 8 } } }))
        .then((found) => setPeople(found.filter((u) => u.id !== me?.id)), () => setPeople([]));
    }, 200);
    return () => clearTimeout(handle);
  }, [query, isGuest, controller, me?.id]);

  const channelItems = useMemo(() => (features.channels ? workspaces.flatMap((w) =>
    (channels[w.id] ?? []).map((c) => ({ channel: c, workspace: w.name }))) : []), [workspaces, channels]);

  const close = () => controller.openCommandBar(false);

  return (
    <Command.Dialog open={open} onOpenChange={controller.openCommandBar.bind(controller)} label="Go to or search"
      container={portal ?? undefined} overlayClassName={styles.commandOverlay} contentClassName={styles.command} loop>
      <div className={styles.commandInputRow}>
        <Search size={18} aria-hidden />
        <Command.Input className={styles.commandInput} value={query} onValueChange={setQuery}
          placeholder={features.channels ? "Jump to a channel, chat or person, or search" : "Jump to a chat or person, or search"} />
        <kbd className={styles.kbd}>Esc</kbd>
      </div>
      <Command.List className={styles.commandList}>
        <Command.Empty className={styles.commandEmpty}>No matches. Press Enter to search messages and files.</Command.Empty>
        {query.trim().length >= 2 && (
          <Command.Group heading="Search" className={styles.commandGroup} forceMount>
            <Command.Item className={styles.commandItem} value={`search ${query}`} onSelect={() => controller.search(query)} forceMount>
              <Search size={16} aria-hidden />
              Search for “{query.trim()}”
            </Command.Item>
          </Command.Group>
        )}
        {channelItems.length > 0 && (
          <Command.Group heading="Channels" className={styles.commandGroup}>
          {channelItems.map(({ channel, workspace }) => (
            <Command.Item key={channel.id} className={styles.commandItem} value={`channel ${channel.name} ${workspace} ${channel.id}`}
              onSelect={() => { close(); controller.openRoom({ type: "Channel", id: channel.id }); }}>
              {channel.type === "Private" ? <Lock size={16} aria-hidden /> : <Hash size={16} aria-hidden />}
              {channel.name}
              <span className={styles.commandMeta}>{workspace}</span>
            </Command.Item>
          ))}
          </Command.Group>
        )}
        <Command.Group heading="Chats" className={styles.commandGroup}>
          {conversations.map((c) => (
            <Command.Item key={c.id} className={styles.commandItem} value={`chat ${displayName(c, me?.id)} ${c.id}`}
              onSelect={() => { close(); controller.openRoom({ type: "Conversation", id: c.id }); }}>
              <MessageSquare size={16} aria-hidden />
              {displayName(c, me?.id)}
            </Command.Item>
          ))}
        </Command.Group>
        {people.length > 0 && (
          <Command.Group heading="People" className={styles.commandGroup} forceMount>
            {people.map((person) => (
              <Command.Item key={person.id} className={styles.commandItem} value={`person ${person.displayName} ${person.email ?? ""} ${person.id}`}
                onSelect={() => { close(); void controller.startConversation([person.id], null); }} forceMount>
                <UserIcon size={16} aria-hidden />
                {person.displayName}
                <span className={styles.commandMeta}>{person.email}</span>
              </Command.Item>
            ))}
          </Command.Group>
        )}
      </Command.List>
    </Command.Dialog>
  );
}
