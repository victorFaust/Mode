import { act, fireEvent, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test } from "vitest";
import type { Conversation, Pin } from "../api/client";
import { ForwardDialog } from "../components/MessageDialogs";
import { previewText } from "../lib/mentions";
import { linkedMessageId } from "../store/controller";
import { ada, bola, chidi, message, renderMode, reply } from "../test/harness";
import { ChatsList } from "./ChatsList";
import { MessageItem } from "./MessageItem";
import { RoomPane } from "./RoomPane";

const chat = (id: string, others: typeof bola[], extra: Partial<Conversation> = {}): Conversation => ({
  id, type: others.length > 1 ? "Group" : "Direct", name: null, members: [ada, ...others], lastSequence: 0,
  createdAt: "2026-11-01T00:00:00Z", isPinned: false, isHidden: false, ...extra,
});

describe("chat list", () => {
  test("pinned chats come first and hidden chats wait behind a toggle", async () => {
    const user = userEvent.setup();
    renderMode(<ChatsList />, {
      conversations: [chat("k1", [bola]), chat("k2", [chidi], { isPinned: true }), chat("k3", [bola, chidi], { isHidden: true })],
    });

    const pinned = screen.getByRole("region", { name: "Pinned" });
    expect(within(pinned).getByRole("button", { name: "Chidi Nwosu" })).toBeInTheDocument();
    expect(within(screen.getByRole("region", { name: "Recent" })).getByRole("button", { name: "Bola Adeyemi" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Bola Adeyemi, Chidi Nwosu" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Show hidden chats (1)" }));
    expect(screen.getByRole("button", { name: "Bola Adeyemi, Chidi Nwosu" })).toBeInTheDocument();
  });

  test("a new message brings a hidden chat back", () => {
    const { store, controller } = renderMode(<ChatsList />, { conversations: [chat("k1", [bola], { isHidden: true })] });

    act(() => controller.handlers.messageCreated({
      room: { type: "Conversation", id: "k1" }, sequence: 1,
      message: message({ id: "m1", sequence: 1, room: { type: "Conversation", id: "k1" } }),
    }));

    expect(store.getState().conversations[0].isHidden).toBe(false);
    expect(screen.getByRole("button", { name: /^Bola Adeyemi/ })).toBeInTheDocument();
  });

  test("hiding a chat saves the setting and moves it out of the list", async () => {
    const { api, controller } = renderMode(<ChatsList />, { conversations: [chat("k1", [bola])] });
    api.PUT.mockImplementationOnce(() => reply(chat("k1", [bola], { isHidden: true })) as never);

    await act(() => controller.setChatSettings("k1", { isHidden: true }));

    expect(api.PUT).toHaveBeenCalledWith("/api/v1/conversations/{conversationId}/settings",
      expect.objectContaining({ params: { path: { conversationId: "k1" } }, body: { isHidden: true } }));
    expect(screen.queryByRole("button", { name: "Bola Adeyemi" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Show hidden chats (1)" })).toBeInTheDocument();
  });
});

describe("messages", () => {
  const room = { type: "Conversation" as const, id: "k1" };

  test("a forwarded message shows a card with its original author and a way to open the original", () => {
    const forwarded = message({
      id: "m2", sequence: 2, room, author: ada, bodyMarkdown: "FYI",
      forwarded: { messageId: "m-orig", author: bola, createdAt: "2026-10-30T09:00:00Z", bodyMarkdown: "Rates go up **Monday**" },
    });
    renderMode(<MessageItem message={forwarded} grouped={false} canPost canManage={false} />);

    expect(screen.getByText("FYI")).toBeInTheDocument();
    expect(screen.getByText(/Forwarded from/)).toHaveTextContent("Forwarded from Bola Adeyemi");
    expect(screen.getByText("Monday").tagName).toBe("STRONG");
    expect(screen.getByRole("button", { name: "View original" })).toBeInTheDocument();
  });

  test("your edited message links to its history; other people's don't", () => {
    renderMode(<>
      <MessageItem message={message({ id: "m1", sequence: 1, room, author: ada, editedAt: "2026-11-01T10:05:00Z" })} grouped={false} canPost canManage={false} />
      <MessageItem message={message({ id: "m2", sequence: 2, room, author: bola, editedAt: "2026-11-01T10:06:00Z" })} grouped={false} canPost canManage={false} />
    </>);

    expect(screen.getAllByRole("button", { name: "Edited. View earlier versions" })).toHaveLength(1);
    expect(screen.getAllByText("Edited")).toHaveLength(2);
  });

  test("the pinned bar shows the latest pin and lists the rest", async () => {
    const user = userEvent.setup();
    const pins: Pin[] = [
      { message: message({ id: "m2", sequence: 2, room, bodyMarkdown: "Use the **new** template" }), pinnedBy: ada, pinnedAt: "2026-11-01T11:00:00Z" },
      { message: message({ id: "m1", sequence: 1, room, bodyMarkdown: "Office closed Friday" }), pinnedBy: bola, pinnedAt: "2026-11-01T10:00:00Z" },
    ];
    renderMode(<RoomPane room={room} />, { conversations: [chat("k1", [bola])], room, pins: { "Conversation:k1": pins } });

    const bar = screen.getByRole("region", { name: "Pinned messages" });
    expect(within(bar).getByRole("button", { name: /^Pinned:\s*Use the new template$/ })).toBeInTheDocument();
    await user.click(within(bar).getByRole("button", { name: /2 pinned/ }));
    expect(within(bar).getByText("Office closed Friday")).toBeInTheDocument();
    expect(within(bar).getByText("Pinned by Bola Adeyemi")).toBeInTheDocument();
  });

  test("forwarding sends the chosen chat and the note", async () => {
    const user = userEvent.setup();
    const original = message({ id: "m1", sequence: 1, room, bodyMarkdown: "Quarter close is Friday" });
    const { api } = renderMode(<ForwardDialog message={original} open onOpenChange={() => undefined} />, {
      conversations: [chat("k1", [bola]), chat("k2", [chidi])],
    });
    api.POST.mockImplementationOnce(() => reply(message({ id: "m9", sequence: 1, room: { type: "Conversation", id: "k2" } })) as never);

    expect(screen.getByText("Quarter close is Friday")).toBeInTheDocument();
    const forward = screen.getByRole("button", { name: "Forward" });
    expect(forward).toBeDisabled();
    await user.click(screen.getByRole("radio", { name: "Chidi Nwosu" }));
    fireEvent.change(screen.getByLabelText("Add a note (optional)"), { target: { value: "For your review" } });
    await user.click(forward);

    await waitFor(() => expect(api.POST).toHaveBeenCalledWith("/api/v1/messages/{messageId}/forward", expect.objectContaining({
      params: { path: { messageId: "m1" } },
      body: expect.objectContaining({ to: { type: "Conversation", id: "k2" }, bodyMarkdown: "For your review" }),
    })));
  });
});

describe("helpers", () => {
  test("previews drop Markdown and show mention names", () => {
    expect(previewText("**Hi** @{u-bola}, see `code` and [the doc](https://x.example)", { [bola.id]: bola }))
      .toBe("Hi @Bola Adeyemi, see code and the doc");
    expect(previewText("> quoted\n- item \\*literal\\*", {})).toBe("quoted item *literal*");
  });

  test("only well-formed message links are followed", () => {
    expect(linkedMessageId("#mode-message=0a1b0001-0000-4000-8000-000000000001")).toBe("0a1b0001-0000-4000-8000-000000000001");
    expect(linkedMessageId("#mode-message=javascript:alert(1)")).toBeNull();
    expect(linkedMessageId("")).toBeNull();
  });
});
