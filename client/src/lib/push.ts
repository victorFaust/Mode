import { ok, type Api } from "../api/client";

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const padded = (value + "===".slice((value.length + 3) % 4)).replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(padded);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

export function devicePushSupported(serviceWorkerUrl: string | undefined): serviceWorkerUrl is string {
  return !!serviceWorkerUrl && "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
}

/** Registers this browser for Web Push. The service worker shows the notification when Mode is closed. */
export async function enableDevicePush(api: Api, serviceWorkerUrl: string): Promise<void> {
  const config = await ok(api.GET("/api/v1/push/public-key"));
  if (!config.enabled || !config.publicKey) throw new Error("Push notifications are not set up on this server.");
  if (await Notification.requestPermission() !== "granted") throw new Error("Notifications are blocked for this site in your browser settings.");

  const registration = await navigator.serviceWorker.register(serviceWorkerUrl);
  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: base64UrlToBytes(config.publicKey),
  });
  const json = subscription.toJSON();
  await ok(api.POST("/api/v1/push/subscriptions", {
    body: { endpoint: json.endpoint ?? null, keys: { p256dh: json.keys?.p256dh ?? null, auth: json.keys?.auth ?? null } },
  }));
}

export async function disableDevicePush(api: Api, serviceWorkerUrl: string): Promise<void> {
  const registration = await navigator.serviceWorker.getRegistration(serviceWorkerUrl);
  const subscription = await registration?.pushManager.getSubscription();
  if (!subscription) return;
  await api.DELETE("/api/v1/push/subscriptions", { params: { query: { endpoint: subscription.endpoint } } });
  await subscription.unsubscribe();
}

export async function devicePushEnabled(serviceWorkerUrl: string): Promise<boolean> {
  const registration = await navigator.serviceWorker.getRegistration(serviceWorkerUrl);
  return !!(await registration?.pushManager.getSubscription());
}
