import { FileText, UserPlus, Users, X } from "lucide-react";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ok, type FileDto, type RoomRef } from "../api/client";
import { useController, useMode } from "../app/context";
import { AddMemberDialog } from "../components/RoomDialogs";
import { MessageSkeleton } from "../components/ShellSkeleton";
import { Avatar, Button, EmptyState, IconButton, presenceLabel } from "../components/ui";
import { buildRows } from "../lib/rows";
import type { SidePane } from "../store/store";
import { AttachmentCard } from "./AttachmentCard";
import { Composer } from "./Composer";
import { MessageItem } from "./MessageItem";
import { useRoomInfo, useRoomMembers } from "./RoomPane";
import styles from "./views.module.css";

export function SidePanel({ room, side }: { room: RoomRef; side: SidePane }) {
  const controller = useController();
  const title = side.kind === "thread" ? "Thread" : side.kind === "files" ? "Files" : "Members";
  return (
    <>
      <header className={styles.sideHeader}>
        <h2 className={styles.sideTitle}>{title}</h2>
        <IconButton label={`Close ${title.toLowerCase()}`} onClick={() => controller.setSide(null)}><X size={18} aria-hidden /></IconButton>
      </header>
      {side.kind === "thread" ? <ThreadPane key={side.messageId} room={room} parentId={side.messageId} />
        : side.kind === "files" ? <FilesPane room={room} />
        : <MembersPane room={room} />}
    </>
  );
}

function ThreadPane({ room, parentId }: { room: RoomRef; parentId: string }) {
  const info = useRoomInfo(room);
  const { members } = useRoomMembers(room);
  const meId = useMode((s) => s.me?.id);
  const thread = useMode((s) => s.threads[parentId]);
  const parent = useMode((s) => s.rooms[`${room.type}:${room.id}`]?.items.find((m) => m.id === parentId) ?? s.threads[parentId]?.parent);
  const rows = useMemo(() => buildRows(thread?.items ?? [], meId, null).filter((r) => r.kind === "message"), [thread?.items, meId]);

  if (!parent || !thread?.loaded) {
    return <div className={styles.sideBody} aria-busy="true" style={{ padding: 20, display: "grid", gap: 20 }}><MessageSkeleton /><MessageSkeleton /></div>;
  }
  return (
    <>
      <div className={styles.sideBody} role="log" aria-live="off" aria-label="Thread replies">
        <div className={styles.threadParent}>
          <MessageItem message={parent} grouped={false} canPost={info.canPost} canManage={info.canManage} inThread />
        </div>
        <p className={styles.replyCount}>{thread.items.length === 0 ? "No replies yet" : `${thread.items.length} ${thread.items.length === 1 ? "reply" : "replies"}`}</p>
        {rows.map((r) => r.kind === "message" && (
          <MessageItem key={r.key} message={r.message} grouped={r.grouped} canPost={info.canPost} canManage={info.canManage} inThread />
        ))}
      </div>
      {info.canPost && !parent.isDeleted && (
        <Composer room={room} parentMessageId={parentId} members={members} allowChannelMention={false} placeholder="Reply" />
      )}
    </>
  );
}

function FilesPane({ room }: { room: RoomRef }) {
  const controller = useController();
  const [files, setFiles] = useState<FileDto[] | null>(null);

  const load = useCallback(() => {
    const call = room.type === "Channel"
      ? controller.api.GET("/api/v1/channels/{channelId}/files", { params: { path: { channelId: room.id } } })
      : controller.api.GET("/api/v1/conversations/{conversationId}/files", { params: { path: { conversationId: room.id } } });
    void ok(call).then(setFiles, (e: Error) => controller.toastError(e.message));
  }, [controller, room.type, room.id]);

  useEffect(() => {
    load();
    const onUpdate = () => load();
    window.addEventListener("mode:file-updated", onUpdate);
    return () => window.removeEventListener("mode:file-updated", onUpdate);
  }, [load]);

  if (!files) return <div className={styles.sideBody} aria-busy="true" style={{ padding: 20 }}><MessageSkeleton /></div>;
  if (files.length === 0) {
    return <div className={styles.sideBody}><EmptyState icon={<FileText />} title="No files yet">Drop files on the conversation or use the paperclip to share them here.</EmptyState></div>;
  }
  return (
    <div className={styles.sideBody}>
      <ul style={{ listStyle: "none", margin: 0, padding: 16, display: "grid", gap: 8 }}>
        {files.map((f) => (
          <li key={f.id}>
            <AttachmentCard attachment={{
              fileId: f.id, name: f.name, sizeBytes: f.current.sizeBytes, contentType: f.current.contentType,
              scanStatus: f.current.scanStatus, previewStatus: f.current.previewStatus,
            }} />
          </li>
        ))}
      </ul>
    </div>
  );
}

function MembersPane({ room }: { room: RoomRef }) {
  const controller = useController();
  const info = useRoomInfo(room);
  const { members, roles, reload } = useRoomMembers(room);
  const presence = useMode((s) => s.presence);
  const [adding, setAdding] = useState(false);
  const sorted = useMemo(() => [...members].sort((a, b) => a.displayName.localeCompare(b.displayName)), [members]);
  const canAdd = room.type === "Channel" ? info.canManage : !info.direct;

  return (
    <div className={styles.sideBody}>
      {canAdd && (
        <div style={{ padding: "12px 20px" }}>
          <Button onClick={() => setAdding(true)}><UserPlus size={16} aria-hidden /> Add people</Button>
        </div>
      )}
      {sorted.length === 0 ? <EmptyState icon={<Users />} title="No members to show" /> : (
        <ul style={{ listStyle: "none", margin: 0, padding: 0 }} aria-label={`${sorted.length} members`}>
          {sorted.map((m) => {
            const status = presence[m.id];
            return (
              <li key={m.id} className={styles.member}>
                <Avatar id={m.id} name={m.displayName} avatarUrl={m.avatarUrl} size={32} status={status?.status ?? "Offline"} />
                <span className={styles.memberName}>
                  <span>{m.displayName}</span>
                  <span className={styles.memberRole}>
                    {[roles[m.id], status ? presenceLabel(status.status) : null, status?.message].filter(Boolean).join(" · ")}
                  </span>
                </span>
              </li>
            );
          })}
        </ul>
      )}
      <AddMemberDialog room={room} existing={members.map((m) => m.id)} open={adding} onOpenChange={setAdding}
        onAdded={() => {
          reload();
          if (room.type === "Conversation") void controller.bootstrapConversations();
        }} />
    </div>
  );
}
