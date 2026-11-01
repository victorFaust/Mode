import { describe, expect, test } from "vitest";
import { message, ada, bola } from "../test/harness";
import { buildRows } from "./rows";

const at = (iso: string) => ({ createdAt: iso });

describe("buildRows", () => {
  test("groups consecutive messages by the same author within five minutes", () => {
    const rows = buildRows([
      message({ id: "1", sequence: 1, ...at("2026-11-01T10:00:00Z") }),
      message({ id: "2", sequence: 2, ...at("2026-11-01T10:03:00Z") }),
      message({ id: "3", sequence: 3, ...at("2026-11-01T10:09:00Z") }),
      message({ id: "4", sequence: 4, author: ada, ...at("2026-11-01T10:10:00Z") }),
    ], ada.id, null);

    const grouped = rows.filter((r) => r.kind === "message").map((r) => r.kind === "message" && r.grouped);
    expect(grouped).toEqual([false, true, false, false]);
  });

  test("adds a separator for each day", () => {
    const rows = buildRows([
      message({ id: "1", sequence: 1, ...at("2026-10-30T10:00:00") }),
      message({ id: "2", sequence: 2, ...at("2026-10-30T11:00:00") }),
      message({ id: "3", sequence: 3, ...at("2026-11-01T09:00:00") }),
    ], ada.id, null);

    expect(rows.map((r) => r.kind)).toEqual(["day", "message", "message", "day", "message"]);
  });

  test("places the New messages marker before the first unread message from someone else", () => {
    const rows = buildRows([
      message({ id: "1", sequence: 1 }),
      message({ id: "2", sequence: 2, author: ada, ...at("2026-11-01T10:01:00Z") }),
      message({ id: "3", sequence: 3, author: bola, ...at("2026-11-01T10:02:00Z") }),
    ], ada.id, 1);

    const kinds = rows.map((r) => (r.kind === "message" ? r.message.id : r.kind));
    expect(kinds).toEqual(["day", "1", "2", "new", "3"]);
    const afterMarker = rows[rows.length - 1];
    expect(afterMarker.kind === "message" && afterMarker.grouped).toBe(false);
  });

  test("shows no marker when nothing was unread", () => {
    const rows = buildRows([message({ id: "1", sequence: 1 })], ada.id, null);
    expect(rows.some((r) => r.kind === "new")).toBe(false);
  });
});
