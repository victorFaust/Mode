import { act, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test } from "vitest";
import { ada, bola, chidi, message, renderMode, reply } from "../test/harness";
import { Composer } from "./Composer";

const room = { type: "Channel" as const, id: "c-general" };

function setup() {
  localStorage.clear();
  const result = renderMode(<Composer room={room} members={[ada, bola]} allowChannelMention placeholder="Message #General" />);
  result.api.POST.mockImplementation(((_: string, init: { body: { id: string; bodyMarkdown: string } }) =>
    reply(message({ id: init.body.id, sequence: 1, author: ada, bodyMarkdown: init.body.bodyMarkdown }), 201)) as never);
  result.api.GET.mockImplementation(() => reply([chidi]) as never);
  return result;
}

describe("Composer", () => {
  test("suggests room members first and sends picked mentions as tokens", async () => {
    const user = userEvent.setup();
    const { api } = setup();
    const box = screen.getByRole("textbox", { name: "Message #General" });

    await user.type(box, "thanks @bo");
    const options = await screen.findAllByRole("option");
    expect(options[0]).toHaveTextContent("Bola Adeyemi");
    await user.keyboard("{Enter}");
    expect(box).toHaveValue("thanks @Bola Adeyemi ");

    await user.keyboard("{Enter}");
    const body = (api.POST.mock.calls[0] as unknown as [string, { body: { bodyMarkdown: string } }])[1].body;
    expect(body.bodyMarkdown).toBe("thanks @{u-bola}");
    expect(box).toHaveValue("");
  });

  test("Shift+Enter adds a line and Ctrl+B wraps the selection in bold", async () => {
    const user = userEvent.setup();
    const { api } = setup();
    const box = screen.getByRole("textbox", { name: "Message #General" });

    await user.type(box, "first{Shift>}{Enter}{/Shift}second");
    expect(box).toHaveValue("first\nsecond");
    expect(api.POST).not.toHaveBeenCalled();

    await user.clear(box);
    await user.type(box, "important");
    (box as HTMLTextAreaElement).setSelectionRange(0, 9);
    await user.keyboard("{Control>}b{/Control}");
    expect(box).toHaveValue("**important**");
  });

  test("keeps an unsent draft per room", async () => {
    const user = userEvent.setup();
    setup();
    await user.type(screen.getByRole("textbox", { name: "Message #General" }), "half a thought");
    await act(() => new Promise((r) => setTimeout(r, 350)));

    expect(localStorage.getItem("mode.draft.u-ada.Channel:c-general")).toBe("half a thought");
  });

  test("does not send while empty", async () => {
    const user = userEvent.setup();
    const { api } = setup();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
    await user.type(screen.getByRole("textbox", { name: "Message #General" }), "   {Enter}");
    expect(api.POST).not.toHaveBeenCalled();
  });
});
