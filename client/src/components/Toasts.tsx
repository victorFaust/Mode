import { X } from "lucide-react";
import { useEffect } from "react";
import { useController, useMode } from "../app/context";
import { IconButton } from "./ui";
import styles from "./components.module.css";

const AUTO_DISMISS_MS = 7000;

export function Toasts() {
  const controller = useController();
  const banners = useMode((s) => s.banners);

  useEffect(() => {
    const timers = banners.map((b) => setTimeout(() => controller.dismissBanner(b.id), AUTO_DISMISS_MS));
    return () => timers.forEach(clearTimeout);
  }, [banners, controller]);

  return (
    <section className={styles.toasts} aria-label="Notifications" aria-live="polite">
      {banners.map((banner) => (
        <div key={banner.id} className={styles.toast}>
          <button type="button" className={styles.toastButton} onClick={() => controller.openBanner(banner)}>
            <span className={styles.toastTitle}>{banner.title}</span>
            {banner.body && <span className={styles.toastBody}>{banner.body}</span>}
          </button>
          <IconButton label="Dismiss" size="small" tooltip={false} onClick={() => controller.dismissBanner(banner.id)}>
            <X size={16} aria-hidden />
          </IconButton>
        </div>
      ))}
    </section>
  );
}
