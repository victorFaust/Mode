import { ChevronDown, Hash, Lock, PanelLeftClose, Plus, Users } from "lucide-react";
import { useState } from "react";
import { useShallow } from "zustand/react/shallow";
import { useController, useMode } from "../app/context";
import { Button, EmptyState, IconButton, Menu, MenuItem } from "../components/ui";
import { NewChannelDialog, NewWorkspaceDialog } from "../components/RoomDialogs";
import { rovingKeyDown } from "../lib/roving";
import { RoomListItem } from "./RoomListItem";
import styles from "./views.module.css";

export function ChannelsList() {
  const controller = useController();
  const { workspaces, channels, isGuest } = useMode(useShallow((s) => ({ workspaces: s.workspaces, channels: s.channels, isGuest: s.isGuest })));
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>(() => JSON.parse(localStorage.getItem("mode.collapsed") ?? "{}"));
  const [newChannelIn, setNewChannelIn] = useState<string | null>(null);
  const [newWorkspace, setNewWorkspace] = useState(false);

  const toggle = (id: string) => {
    const next = { ...collapsed, [id]: !collapsed[id] };
    setCollapsed(next);
    localStorage.setItem("mode.collapsed", JSON.stringify(next));
  };
  const creatable = workspaces.filter((w) => w.myRole !== "Guest");

  return (
    <>
      <div className={styles.listHeader}>
        <h2 className={styles.listTitle}>Channels</h2>
        {(creatable.length > 0 || !isGuest) && (
          <Menu align="end" trigger={<IconButton label="Create"><Plus size={18} aria-hidden /></IconButton>}>
            {creatable.length > 0 && <MenuItem onSelect={() => setNewChannelIn(creatable[0].id)}><Hash size={16} aria-hidden /> New channel</MenuItem>}
            {!isGuest && <MenuItem onSelect={() => setNewWorkspace(true)}><Users size={16} aria-hidden /> New workspace</MenuItem>}
          </Menu>
        )}
        <IconButton label="Hide list" onClick={() => controller.setListOpen(false)}><PanelLeftClose size={18} aria-hidden /></IconButton>
      </div>
      <div className={styles.listBody} onKeyDown={rovingKeyDown}>
        {workspaces.length === 0 ? (
          <EmptyState icon={<Users />} title="No workspaces yet"
            action={!isGuest && <Button variant="primary" onClick={() => setNewWorkspace(true)}>Create a workspace</Button>}>
            {isGuest ? "Ask a workspace owner to add you." : "Create one for your team, or ask to be added to an existing one."}
          </EmptyState>
        ) : workspaces.map((workspace) => {
          const list = (channels[workspace.id] ?? []).filter((c) => !c.isArchived);
          const open = !collapsed[workspace.id];
          return (
            <div key={workspace.id} className={styles.group}>
              <div style={{ display: "flex", alignItems: "center" }}>
                <button type="button" className={styles.groupHeader} aria-expanded={open} onClick={() => toggle(workspace.id)}>
                  <ChevronDown size={14} aria-hidden />
                  <span className={styles.groupName}>{workspace.name}</span>
                </button>
                {workspace.myRole !== "Guest" && (
                  <span className={styles.groupActions}>
                    <IconButton label={`New channel in ${workspace.name}`} size="small" onClick={() => setNewChannelIn(workspace.id)}>
                      <Plus size={14} aria-hidden />
                    </IconButton>
                  </span>
                )}
              </div>
              {open && (
                <ul className={styles.roomList}>
                  {list.map((channel) => (
                    <RoomListItem key={channel.id} room={{ type: "Channel", id: channel.id }} name={channel.name}
                      icon={channel.type === "Private" ? <Lock size={16} aria-label="Private" /> : <Hash size={16} aria-hidden />} />
                  ))}
                </ul>
              )}
            </div>
          );
        })}
      </div>
      <NewChannelDialog workspaceId={newChannelIn} onClose={() => setNewChannelIn(null)} />
      <NewWorkspaceDialog open={newWorkspace} onOpenChange={setNewWorkspace} />
    </>
  );
}
