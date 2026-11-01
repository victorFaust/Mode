import { Download, FileText, Image as ImageIcon, Loader2, ShieldAlert } from "lucide-react";
import { useEffect, useState } from "react";
import type { Attachment } from "../api/client";
import { useController } from "../app/context";
import { IconButton } from "../components/ui";
import { formatBytes } from "../lib/format";
import styles from "./views.module.css";

const blocked = new Set(["Infected", "Blocked", "Failed"]);

export function scanLabel(status: Attachment["scanStatus"]): string | null {
  switch (status) {
    case "Pending": return "Checking for viruses…";
    case "Infected": return "Blocked: a virus was found";
    case "Blocked": return "Blocked: the file type doesn't match its name";
    case "Failed": return "Blocked: the file couldn't be checked";
    default: return null;
  }
}

export function AttachmentCard({ attachment }: { attachment: Pick<Attachment, "fileId" | "name" | "sizeBytes" | "contentType" | "scanStatus" | "previewStatus"> }) {
  const controller = useController();
  const [thumbnail, setThumbnail] = useState<string | null>(null);
  const clean = attachment.scanStatus === "Clean";
  const isImage = attachment.contentType.startsWith("image/");

  useEffect(() => {
    if (!clean || !isImage || attachment.previewStatus !== "Ready") return;
    let cancelled = false;
    void controller.fileLink(attachment.fileId, "thumbnail").then((url) => !cancelled && setThumbnail(url), () => undefined);
    return () => { cancelled = true; };
  }, [clean, isImage, attachment.previewStatus, attachment.fileId, controller]);

  const download = async () => {
    try {
      window.location.assign(await controller.fileLink(attachment.fileId));
    } catch (e) {
      controller.toastError(e instanceof Error ? e.message : "Download failed.");
    }
  };

  const status = scanLabel(attachment.scanStatus);
  return (
    <div className={styles.attachment}>
      <span className={styles.attachmentIcon}>
        {thumbnail ? <img src={thumbnail} alt="" />
          : attachment.scanStatus === "Pending" ? <Loader2 size={18} aria-hidden />
          : blocked.has(attachment.scanStatus) ? <ShieldAlert size={18} aria-hidden />
          : isImage ? <ImageIcon size={18} aria-hidden /> : <FileText size={18} aria-hidden />}
      </span>
      <span style={{ minWidth: 0, display: "grid" }}>
        <span className={styles.attachmentName} title={attachment.name}>{attachment.name}</span>
        <span className={`${styles.attachmentMeta} ${blocked.has(attachment.scanStatus) ? styles.attachmentBlocked : ""}`}>
          {status ?? formatBytes(attachment.sizeBytes)}
        </span>
      </span>
      <IconButton label={`Download ${attachment.name}`} size="small" disabled={!clean} onClick={() => void download()}>
        <Download size={16} aria-hidden />
      </IconButton>
    </div>
  );
}
