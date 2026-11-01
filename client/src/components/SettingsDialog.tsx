import { useEffect, useId, useState } from "react";
import { ok, type NotificationSettings } from "../api/client";
import { useController } from "../app/context";
import { features } from "../app/features";
import { devicePushEnabled, devicePushSupported, disableDevicePush, enableDevicePush } from "../lib/push";
import { Button, Dialog, DialogActions, Skeleton } from "./ui";
import styles from "./components.module.css";

type Delivery = NotificationSettings["mentions"];

const deliveryOptions: { value: Delivery; label: string }[] = [
  { value: "BannerAndFeed", label: "Banner and feed" },
  { value: "FeedOnly", label: "Only show in feed" },
  { value: "Off", label: "Off" },
];

const categories: { key: "mentions" | "chats" | "channels"; label: string; hint: string }[] = [
  { key: "mentions", label: "Mentions", hint: features.channels ? "@you and @channel" : "When someone @mentions you" },
  { key: "chats", label: "Chats", hint: "New messages in your chats" },
  ...(features.channels ? [{ key: "channels" as const, label: "Channels", hint: "Replies, reactions and being added" }] : []),
];

export function SettingsDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const controller = useController();
  const [settings, setSettings] = useState<NotificationSettings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [push, setPush] = useState<"unsupported" | "off" | "on" | "busy">("unsupported");
  const swUrl = controller.platform.serviceWorkerUrl;
  const id = useId();

  useEffect(() => {
    if (!open) return;
    setError(null);
    void ok(controller.api.GET("/api/v1/me/notification-settings")).then(setSettings, (e: Error) => setError(e.message));
    if (devicePushSupported(swUrl)) void devicePushEnabled(swUrl).then((on) => setPush(on ? "on" : "off"));
  }, [open, controller, swUrl]);

  const save = async () => {
    if (!settings) return;
    setSaving(true);
    try {
      await ok(controller.api.PUT("/api/v1/me/notification-settings", { body: settings }));
      onOpenChange(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not save.");
    } finally {
      setSaving(false);
    }
  };

  const togglePush = async () => {
    if (!devicePushSupported(swUrl)) return;
    const was = push;
    setPush("busy");
    try {
      if (was === "on") await disableDevicePush(controller.api, swUrl);
      else await enableDevicePush(controller.api, swUrl);
      setPush(was === "on" ? "off" : "on");
    } catch (e) {
      setPush(was);
      setError(e instanceof Error ? e.message : "Could not change push notifications.");
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Notifications" description="Choose what reaches you and how.">
      {!settings ? (
        <div style={{ display: "grid", gap: 12 }} aria-busy="true">
          {[0, 1, 2].map((i) => <Skeleton key={i} width="100%" height={28} />)}
        </div>
      ) : (
        <>
          <div className={styles.settingsSection}>
            {categories.map((c) => (
              <div key={c.key} className={styles.settingsRow}>
                <label htmlFor={`${id}-${c.key}`}>
                  <span className={styles.settingsLabel}>{c.label}</span>
                  <br />
                  <span className={styles.settingsHint}>{c.hint}</span>
                </label>
                <select id={`${id}-${c.key}`} className={styles.select} value={settings[c.key]}
                  onChange={(e) => setSettings({ ...settings, [c.key]: e.target.value as Delivery })}>
                  {deliveryOptions.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              </div>
            ))}
          </div>
          <div className={styles.settingsSection}>
            <div className={styles.settingsRow}>
              <label htmlFor={`${id}-email`}>
                <span className={styles.settingsLabel}>Email summary</span>
                <br />
                <span className={styles.settingsHint}>Unread activity you haven't seen in Mode</span>
              </label>
              <select id={`${id}-email`} className={styles.select} value={settings.emailDigest}
                onChange={(e) => setSettings({ ...settings, emailDigest: e.target.value as NotificationSettings["emailDigest"] })}>
                <option value="Off">Never</option>
                <option value="Hourly">Hourly</option>
                <option value="Daily">Daily</option>
              </select>
            </div>
            <div className={styles.settingsRow}>
              <label htmlFor={`${id}-push`}>
                <span className={styles.settingsLabel}>Push to my devices</span>
                <br />
                <span className={styles.settingsHint}>When Mode isn't open</span>
              </label>
              <input id={`${id}-push`} type="checkbox" checked={settings.pushEnabled}
                onChange={(e) => setSettings({ ...settings, pushEnabled: e.target.checked })} />
            </div>
            {push !== "unsupported" && (
              <div className={styles.settingsRow}>
                <span>
                  <span className={styles.settingsLabel}>This browser</span>
                  <br />
                  <span className={styles.settingsHint}>{push === "on" ? "Receiving push notifications" : "Not receiving push notifications"}</span>
                </span>
                <Button onClick={() => void togglePush()} disabled={push === "busy"}>{push === "on" ? "Turn off" : "Turn on"}</Button>
              </div>
            )}
          </div>
        </>
      )}
      {error && <p role="alert" style={{ color: "var(--m-danger)" }}>{error}</p>}
      <DialogActions>
        <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
        <Button variant="primary" onClick={() => void save()} disabled={!settings || saving}>Save</Button>
      </DialogActions>
    </Dialog>
  );
}
