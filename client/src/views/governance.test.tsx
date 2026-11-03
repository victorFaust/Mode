import { act, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, test } from "vitest";
import { roomKey } from "../api/client";
import { DlpWarningDialog } from "../components/DlpWarningDialog";
import { message, renderMode, reply } from "../test/harness";
import { AdminView } from "./AdminView";
import { MessageItem } from "./MessageItem";

const room = { type: "Conversation" as const, id: "k1" };
const problem = (status: number, body: object) =>
  Promise.resolve({ data: undefined, error: body, response: new Response(null, { status }) });

describe("data-loss rules", () => {
  test("a warning asks before sending and resends with the confirmation", async () => {
    const user = userEvent.setup();
    const { api, controller } = renderMode(<DlpWarningDialog />, { room });
    api.POST
      .mockImplementationOnce(() => problem(409, { code: "dlp_warning", rules: ["Project names"] }) as never)
      .mockImplementationOnce(() => reply(message({ id: "m1", sequence: 1, room })) as never);

    await act(() => controller.send(room, "about Falcon", [], [], null));

    const dialog = await screen.findByRole("dialog", { name: "Check before sending" });
    expect(within(dialog).getByText("Matched: Project names")).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Send anyway" }));

    await waitFor(() => expect(api.POST).toHaveBeenCalledTimes(2));
    expect(api.POST.mock.calls[1]).toEqual([
      "/api/v1/conversations/{conversationId}/messages",
      expect.objectContaining({ body: expect.objectContaining({ acknowledgeDlpWarnings: true }) }),
    ]);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  test("a blocked message stays unsent with the reason", async () => {
    const loaded = { items: [], loaded: true, loading: false, hasOlder: false, lastReadOnOpen: null };
    const { api, controller, store } = renderMode(<div />, { room, rooms: { [roomKey(room)]: loaded } });
    api.POST.mockImplementationOnce(() => problem(422, {
      code: "dlp_blocked", rules: ["Card numbers"], detail: "This message matches your organization's data-loss rules and can't be sent.",
    }) as never);

    await act(() => controller.send(room, "card 4111111111111111", [], [], null));

    const sent = store.getState().rooms[roomKey(room)].items[0];
    expect(sent.pending).toMatchObject({ state: "failed", error: expect.stringContaining("data-loss rules") });
    expect(store.getState().dlpWarning).toBeNull();
  });
});

describe("bots", () => {
  test("webhook messages are labelled as a bot", () => {
    const bot = message({ id: "b1", sequence: 1, room, isBot: true, author: { id: "bot:1", displayName: "Build bot", isActive: true } });
    renderMode(<MessageItem message={bot} grouped={false} canManage={false} canPost />);
    expect(screen.getByText("Build bot")).toBeInTheDocument();
    expect(screen.getByText("Bot")).toBeInTheDocument();
  });
});

describe("admin", () => {
  test("only administrators see the admin pages", () => {
    renderMode(<AdminView />, { isAdmin: false });
    expect(screen.getByText("Administrators only")).toBeInTheDocument();
  });

  test("workspaces list shows retention and saves a new period", async () => {
    const user = userEvent.setup();
    const workspace = {
      id: "w1", name: "Treasury", description: null, createdAt: "2026-11-01T00:00:00Z", memberCount: 3, channelCount: 2, retentionDays: null,
    };
    const { api } = renderMode(<AdminView />, { isAdmin: true });
    expect(await screen.findByText("No workspaces yet.")).toBeInTheDocument();
    api.GET.mockImplementation(((url: string) => reply(url === "/api/v1/admin/workspaces" ? [workspace] : [])) as never);
    api.PUT.mockImplementation(() => reply({ ...workspace, retentionDays: 90 }) as never);
    await user.click(screen.getByRole("tab", { name: "Legal holds" }));
    await user.click(screen.getByRole("tab", { name: "Workspaces" }));

    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual(
      ["Workspaces", "Audit log", "Legal holds", "Exports", "Webhooks", "Data loss"]);
    const input = await screen.findByLabelText("Days to keep messages in Treasury");
    expect(input).toHaveAttribute("placeholder", "Forever");
    await user.type(input, "90");
    await user.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() => expect(api.PUT).toHaveBeenCalledWith("/api/v1/admin/workspaces/{workspaceId}/retention",
      { params: { path: { workspaceId: "w1" } }, body: { keepDays: 90 } }));
  });
});
