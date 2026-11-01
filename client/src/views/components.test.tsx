import { act, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test } from "vitest";
import { CommandBar } from "../components/CommandBar";
import { ConnectionBanner } from "../components/ConnectionBanner";
import type { Channel, Workspace } from "../api/client";
import { renderMode } from "../test/harness";
import { ChannelsList } from "./ChannelsList";
import { MessageBody } from "./MessageBody";

const workspace: Workspace = { id: "w1", name: "Treasury", description: null, myRole: "Member", createdAt: "2026-11-01T00:00:00Z" };
const channel = (id: string, name: string, type: Channel["type"] = "Standard"): Channel => ({
  id, workspaceId: "w1", name, description: null, type, isArchived: false, createdAt: "2026-11-01T00:00:00Z", canPost: true, canManage: false,
});
const general = channel("c1", "General");
const deals = channel("c2", "Deals", "Private");
const unread = (id: string, unreadCount: number, mentionCount: number, isMuted = false) => ({
  [`Channel:${id}`]: { room: { type: "Channel" as const, id }, unreadCount, mentionCount, lastReadSequence: 0, firstUnreadSequence: 1, isMuted },
});

describe("channel list", () => {
  test("shows mention badges, bold unread rooms and muted rooms without counts", () => {
    renderMode(<ChannelsList />, {
      workspaces: [workspace],
      channels: { w1: [general, deals, channel("c3", "Quiet")] },
      unread: { ...unread("c1", 4, 0), ...unread("c2", 2, 1), ...unread("c3", 9, 0, true) },
    });

    const generalItem = screen.getByRole("button", { name: "General, 4 unread" });
    expect(generalItem).toHaveAttribute("data-unread", "true");
    expect(within(generalItem).getByLabelText("4 unread")).toHaveTextContent("4");
    expect(screen.getByRole("button", { name: "Deals, 1 mention, 2 unread" })).toBeInTheDocument();
    const quiet = screen.getByRole("button", { name: "Quiet, muted" });
    expect(quiet).toHaveAttribute("data-unread", "false");
    expect(within(quiet).queryByText("9")).not.toBeInTheDocument();
  });

  test("arrow keys move between rooms and Enter opens one", async () => {
    const user = userEvent.setup();
    const { store } = renderMode(<ChannelsList />, { workspaces: [workspace], channels: { w1: [general, deals] } });

    screen.getByRole("button", { name: "General" }).focus();
    await user.keyboard("{ArrowDown}");
    expect(screen.getByRole("button", { name: "Deals" })).toHaveFocus();
    await user.keyboard("{Enter}");
    expect(store.getState().room).toEqual({ type: "Channel", id: "c2" });
  });
});

describe("command bar", () => {
  test("filters chats as you type and jumps to the chosen one", async () => {
    const user = userEvent.setup();
    const members = [{ id: "u-ada", displayName: "Ada Okafor", isActive: true }];
    const chat = (id: string, name: string) => ({ id, type: "Group" as const, name, members, lastSequence: 0, createdAt: "2026-11-01T00:00:00Z" });
    const { store } = renderMode(<CommandBar />, { conversations: [chat("k1", "Treasury desk"), chat("k2", "Deals team")], commandBarOpen: true });

    await user.type(await screen.findByPlaceholderText(/Jump to a/), "dea");
    expect(screen.queryByRole("option", { name: /Treasury desk/ })).not.toBeInTheDocument();
    await user.keyboard("{ArrowDown}{Enter}");

    expect(store.getState().room).toEqual({ type: "Conversation", id: "k2" });
    expect(store.getState().commandBarOpen).toBe(false);
  });

  test("offers a full search for the typed words", async () => {
    const user = userEvent.setup();
    const { store } = renderMode(<CommandBar />, { commandBarOpen: true });

    await user.type(await screen.findByPlaceholderText(/Jump to a/), "quarterly budget");
    await user.click(screen.getByRole("option", { name: /Search for “quarterly budget”/ }));

    expect(store.getState().searchQuery).toBe("quarterly budget");
  });
});

describe("connection banner", () => {
  test("explains offline and reconnecting states and disappears when connected", () => {
    const { store } = renderMode(<ConnectionBanner />, { online: false, connection: "connected" });
    expect(screen.getByRole("status")).toHaveTextContent("You're offline");

    act(() => store.setState({ online: true, connection: "reconnecting" }));
    expect(screen.getByRole("status")).toHaveTextContent("Reconnecting");

    act(() => store.setState({ connection: "connected" }));
    expect(screen.getByRole("status")).toHaveTextContent("");
  });
});

describe("message body", () => {
  test("renders mentions as chips, keeps raw HTML as text and drops unsafe links", () => {
    const { container } = renderMode(
      <MessageBody markdown={"hi @{u-bola} &lt;script>alert(1)&lt;/script> [ok](https://uba.example) [bad](javascript:alert(1))"} />);

    expect(screen.getByText("@Bola Adeyemi")).toBeInTheDocument();
    expect(container.querySelector("script")).toBeNull();
    expect(screen.getByText(/<script>alert\(1\)<\/script>/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "ok" })).toHaveAttribute("rel", "noopener noreferrer nofollow");
    expect(screen.queryByRole("link", { name: "bad" })).toBeNull();
  });
});
