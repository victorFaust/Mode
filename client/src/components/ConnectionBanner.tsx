import { Loader2, WifiOff } from "lucide-react";
import { useShallow } from "zustand/react/shallow";
import { useMode } from "../app/context";
import styles from "./components.module.css";

/** Tells people when messages may be delayed, and clears itself once reconnected. */
export function ConnectionBanner() {
  const { online, connection } = useMode(useShallow((s) => ({ online: s.online, connection: s.connection })));

  if (!online) {
    return (
      <div className={`${styles.connection} ${styles.offline}`} role="status">
        <WifiOff size={16} aria-hidden />
        You're offline. Messages you send will fail until the connection is back.
      </div>
    );
  }
  if (connection === "reconnecting" || connection === "disconnected") {
    return (
      <div className={styles.connection} role="status">
        <Loader2 size={16} className={styles.spin} aria-hidden />
        {connection === "reconnecting" ? "Reconnecting… new messages may be delayed." : "Can't reach Mode. Retrying…"}
      </div>
    );
  }
  return <div role="status" className="m-visually-hidden" />;
}
