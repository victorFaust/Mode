import createClient, { type Middleware } from "openapi-fetch";
import type { components, paths } from "./schema";

export type Schemas = components["schemas"];
export type Message = Schemas["MessageDto"];
export type RoomRef = Schemas["RoomRef"];
export type RoomType = Schemas["RoomType"];
export type User = Schemas["UserDto"];
export type Channel = Schemas["ChannelDto"];
export type Conversation = Schemas["ConversationDto"];
export type Workspace = Schemas["WorkspaceDto"];
export type FileDto = Schemas["FileDto"];
export type Attachment = Schemas["AttachmentDto"];
export type Presence = Schemas["PresenceDto"];
export type PresenceStatus = Schemas["PresenceStatus"];
export type ActivityFeedItem = Schemas["ActivityFeedItemDto"];
export type RoomUnread = Schemas["RoomUnreadDto"];
export type NotificationSettings = Schemas["NotificationSettings"];
export type SearchResponse = Schemas["SearchResponse"];
export type HighlightedText = Schemas["HighlightedText"];
export type Reaction = Schemas["ReactionSummaryDto"];

export interface Problem {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly problem: Problem | undefined,
  ) {
    super(problem?.detail ?? firstValidationError(problem) ?? problem?.title ?? `Request failed (${status})`);
  }
}

function firstValidationError(problem: Problem | undefined): string | undefined {
  const errors = problem?.errors;
  return errors ? Object.values(errors).flat()[0] : undefined;
}

export interface ApiOptions {
  /** Origin and path prefix of the Mode API, e.g. "https://mode.bank.example". Empty for same origin. */
  baseUrl: string;
  /** Returns a valid host access token; the host is expected to cache and refresh it. */
  getToken: () => Promise<string>;
  onUnauthorized?: () => void;
}

export function createApi(options: ApiOptions) {
  const auth: Middleware = {
    async onRequest({ request }) {
      request.headers.set("Authorization", `Bearer ${await options.getToken()}`);
      return request;
    },
    onResponse({ response }) {
      if (response.status === 401) options.onUnauthorized?.();
      return response;
    },
  };
  const client = createClient<paths>({ baseUrl: options.baseUrl });
  client.use(auth);

  /** PUT raw bytes with upload progress, which fetch cannot report. */
  async function upload(url: string, file: Blob, onProgress: (fraction: number) => void, signal?: AbortSignal) {
    const token = await options.getToken();
    await new Promise<void>((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      xhr.open("PUT", options.baseUrl + url);
      xhr.setRequestHeader("Authorization", `Bearer ${token}`);
      xhr.upload.onprogress = (e) => e.lengthComputable && onProgress(e.loaded / e.total);
      xhr.onload = () => {
        if (xhr.status >= 200 && xhr.status < 300) resolve();
        else reject(new ApiError(xhr.status, safeJson(xhr.responseText)));
      };
      xhr.onerror = () => reject(new ApiError(0, { title: "Network error" }));
      xhr.onabort = () => reject(new DOMException("Upload cancelled", "AbortError"));
      signal?.addEventListener("abort", () => xhr.abort());
      xhr.send(file);
    });
  }

  return { ...client, upload, baseUrl: options.baseUrl };
}

function safeJson(text: string): Problem | undefined {
  try {
    return JSON.parse(text) as Problem;
  } catch {
    return undefined;
  }
}

export type Api = ReturnType<typeof createApi>;

/** Returns the data or throws an ApiError carrying the server's problem details. */
export async function ok<T>(call: Promise<{ data?: T; error?: unknown; response: Response }>): Promise<T> {
  const { data, error, response } = await call;
  if (!response.ok) throw new ApiError(response.status, error as Problem | undefined);
  return data as T;
}

export function roomKey(room: RoomRef): string {
  return `${room.type}:${room.id}`;
}
