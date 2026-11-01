import * as RadixTooltip from "@radix-ui/react-tooltip";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { vi } from "vitest";
import type { Api, Message, User } from "../api/client";
import { ModeProvider } from "../app/context";
import { ModeController } from "../store/controller";
import { createModeStore, type ModeState } from "../store/store";

export const ada: User = { id: "u-ada", displayName: "Ada Okafor", email: "ada@uba.example", avatarUrl: null, isActive: true };
export const bola: User = { id: "u-bola", displayName: "Bola Adeyemi", email: "bola@uba.example", avatarUrl: null, isActive: true };
export const chidi: User = { id: "u-chidi", displayName: "Chidi Nwosu", email: "chidi@uba.example", avatarUrl: null, isActive: true };

/** A fake openapi-fetch result. */
export function reply<T>(data: T, status = 200) {
  return Promise.resolve({ data, error: undefined, response: new Response(null, { status }) });
}

export function fakeApi() {
  return {
    GET: vi.fn(() => reply([])),
    POST: vi.fn(() => reply({})),
    PUT: vi.fn(() => reply({})),
    PATCH: vi.fn(() => reply({})),
    DELETE: vi.fn(() => reply(null, 204)),
    upload: vi.fn(() => Promise.resolve()),
    baseUrl: "",
  };
}

export function message(overrides: Partial<Message> & { id: string; sequence: number }): Message {
  return {
    room: { type: "Channel", id: "c-general" },
    updatedSequence: overrides.sequence,
    parentMessageId: null,
    author: bola,
    bodyMarkdown: "hello",
    createdAt: "2026-11-01T10:00:00Z",
    editedAt: null,
    isDeleted: false,
    replyCount: 0,
    lastReplyAt: null,
    reactions: [],
    mentions: [],
    files: [],
    ...overrides,
  };
}

export function renderMode(ui: ReactElement, state: Partial<ModeState> = {}) {
  const api = fakeApi();
  const store = createModeStore({ me: ada, bootstrapped: true, users: { [ada.id]: ada, [bola.id]: bola }, ...state });
  const controller = new ModeController(api as unknown as Api, store, async () => "token");
  const portal = document.createElement("div");
  document.body.appendChild(portal);
  const result = render(
    <div data-mode-root data-theme="light">
      <RadixTooltip.Provider>
        <ModeProvider value={{ controller, portal }}>{ui}</ModeProvider>
      </RadixTooltip.Provider>
    </div>,
  );
  return { ...result, api, store, controller };
}
