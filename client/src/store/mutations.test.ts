import { describe, expect, test } from "vitest";
import { message } from "../test/harness";
import { applyMessage, upsertSorted } from "./mutations";
import { initialState, type LocalMessage } from "./store";

const room = { type: "Channel" as const, id: "c-general" };

describe("store mutations", () => {
  test("keeps messages in sequence order with pending sends last", () => {
    const pending: LocalMessage = { ...message({ id: "p", sequence: 0 }), pending: { state: "sending", send: { bodyMarkdown: "x", parentMessageId: null, fileIds: [] } } };
    let items = upsertSorted([], message({ id: "2", sequence: 2 }));
    items = upsertSorted(items, pending);
    items = upsertSorted(items, message({ id: "1", sequence: 1 }));
    items = upsertSorted(items, message({ id: "3", sequence: 3 }));
    expect(items.map((m) => m.id)).toEqual(["1", "2", "3", "p"]);

    const confirmed = upsertSorted(items, message({ id: "p", sequence: 4 }));
    expect(confirmed.map((m) => m.id)).toEqual(["1", "2", "3", "p"]);
    expect(confirmed[3].pending).toBeUndefined();
  });

  test("a new reply bumps the parent's reply count once", () => {
    const state = { ...initialState(), rooms: { "Channel:c-general": { items: [message({ id: "parent", sequence: 1 })], loaded: true, loading: false, hasOlder: false, lastReadOnOpen: null } } };
    const reply = message({ id: "r1", sequence: 2, parentMessageId: "parent", room });

    const once = { ...state, ...applyMessage(state, reply) };
    const twice = { ...once, ...applyMessage(once, reply) };

    expect(twice.rooms["Channel:c-general"].items[0].replyCount).toBe(1);
    expect(twice.threads.parent.items.map((m) => m.id)).toEqual(["r1"]);
    expect(twice.roomSequence["Channel:c-general"]).toBe(2);
  });
});
