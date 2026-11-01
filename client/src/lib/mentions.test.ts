import { describe, expect, test } from "vitest";
import { activeMentionQuery, renderableMarkdown, toEditableText, toWireMarkdown } from "./mentions";

describe("mentions", () => {
  test("readable names become server tokens, longest name first", () => {
    const wire = toWireMarkdown("hi @Ada Okafor and @Ada, ping @Ada Okafor", [
      { id: "u-ada", name: "Ada Okafor" },
      { id: "u-a", name: "Ada" },
    ]);
    expect(wire).toBe("hi @{u-ada} and @{u-a}, ping @{u-ada}");
  });

  test("tokens become readable names for editing", () => {
    const { text, picked } = toEditableText("thanks @{u-ada}", { "u-ada": { id: "u-ada", displayName: "Ada Okafor", isActive: true } });
    expect(text).toBe("thanks @Ada Okafor");
    expect(picked).toEqual([{ id: "u-ada", name: "Ada Okafor" }]);
  });

  test("mentions inside code are left alone", () => {
    expect(renderableMarkdown("see @{u-1} `@{u-2}` @channel")).toBe(
      "see [@u-1](mode-mention:u-1) `@{u-2}` [@channel](mode-mention:channel)");
  });

  test("finds the @query being typed at the caret", () => {
    expect(activeMentionQuery("hello @ad", 9)).toEqual({ start: 6, query: "ad" });
    expect(activeMentionQuery("email a@b.com", 13)).toBeNull();
    expect(activeMentionQuery("done @ada ", 10)).toBeNull();
  });
});
