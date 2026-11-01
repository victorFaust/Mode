import { act, screen, waitFor } from "@testing-library/react";
import {
  $createParagraphNode, $createTextNode, $getRoot, createEditor, FORMAT_TEXT_COMMAND, KEY_ENTER_COMMAND, type LexicalEditor,
} from "lexical";
import { describe, expect, test } from "vitest";
import { $exportMessage, $importMessage, editorNodes, messageTransformers } from "../editor/markdown";
import { $isMentionNode } from "../editor/MentionNode";
import { ada, bola, message, renderMode, reply } from "../test/harness";
import { Composer } from "./Composer";

const room = { type: "Conversation" as const, id: "k-bola" };
const transformers = messageTransformers((id) => (id === "u-bola" ? "Bola Adeyemi" : undefined));

function headless(): LexicalEditor {
  return createEditor({ nodes: editorNodes, onError: (e) => { throw e; } });
}

describe("message Markdown", () => {
  test("round-trips formatting, lists, code and mentions", () => {
    const editor = headless();
    const source = "Hi @{u-bola}, **bold** *italic* `code` ~~gone~~\n\n- one\n- two\n\n```\nconst x = 1;\n```";
    editor.update(() => $importMessage(source, transformers), { discrete: true });

    const { markdown, mention } = editor.getEditorState().read(() => ({
      markdown: $exportMessage(transformers),
      mention: $getRoot().getAllTextNodes().find($isMentionNode)?.getTextContent(),
    }));
    expect(mention).toBe("@Bola Adeyemi");
    expect(markdown).toBe(source);
  });

  test("shows escaped HTML from the server as plain characters", () => {
    const editor = headless();
    editor.update(() => $importMessage("a &lt;b> c", transformers), { discrete: true });
    expect(editor.getEditorState().read(() => $getRoot().getTextContent())).toBe("a <b> c");
  });
});

function setup(draft?: string) {
  localStorage.clear();
  if (draft) localStorage.setItem("mode.draft.u-ada.Conversation:k-bola", draft);
  const result = renderMode(<Composer room={room} members={[ada, bola]} allowChannelMention={false} placeholder="Message Bola Adeyemi" />);
  result.api.POST.mockImplementation(((_: string, init: { body: { id: string; bodyMarkdown: string } }) =>
    reply(message({ id: init.body.id, sequence: 1, author: ada, room, bodyMarkdown: init.body.bodyMarkdown }), 201)) as never);
  const box = screen.getByRole("textbox", { name: "Message Bola Adeyemi" });
  const editor = (box as HTMLElement & { __lexicalEditor: LexicalEditor }).__lexicalEditor;
  return { ...result, box, editor };
}

function typeText(editor: LexicalEditor, text: string) {
  act(() => editor.update(() => {
    const paragraph = $createParagraphNode();
    const node = $createTextNode(text);
    paragraph.append(node);
    $getRoot().clear().append(paragraph);
    node.select(text.length, text.length);
  }, { discrete: true }));
}

const enter = (editor: LexicalEditor, shiftKey = false) =>
  act(async () => { editor.dispatchCommand(KEY_ENTER_COMMAND, new KeyboardEvent("keydown", { key: "Enter", shiftKey })); });

const sentBodies = (api: ReturnType<typeof setup>["api"]) =>
  (api.POST.mock.calls as unknown as [string, { body: { bodyMarkdown: string } }][]).map(([, init]) => init.body.bodyMarkdown);

describe("Composer", () => {
  test("Enter sends the formatted message as Markdown and clears the box", async () => {
    const { api, editor } = setup();
    typeText(editor, "ship it");
    act(() => editor.update(() => $getRoot().getAllTextNodes()[0].select(0, "ship it".length), { discrete: true }));
    act(() => { editor.dispatchCommand(FORMAT_TEXT_COMMAND, "bold"); });
    await enter(editor);

    await waitFor(() => expect(sentBodies(api)).toEqual(["**ship it**"]));
    expect(editor.getEditorState().read(() => $getRoot().getTextContent())).toBe("");
  });

  test("Shift+Enter does not send", async () => {
    const { api, editor } = setup();
    typeText(editor, "first line");
    await enter(editor, true);
    expect(api.POST).not.toHaveBeenCalled();
  });

  test("@ suggests people in the chat and sends the mention as a token", async () => {
    const { api, editor } = setup();
    typeText(editor, "thanks @bo");

    const option = await screen.findByRole("option", { name: /Bola Adeyemi/ });
    expect(option).toHaveAttribute("aria-selected", "true");
    await enter(editor);
    await waitFor(() => expect(editor.getEditorState().read(() => $getRoot().getTextContent())).toBe("thanks @Bola Adeyemi "));

    await enter(editor);
    await waitFor(() => expect(sentBodies(api)).toEqual(["thanks @{u-bola}"]));
  });

  test("restores the saved draft and keeps Send disabled while empty", () => {
    setup("half a **thought**");
    expect(screen.getByRole("textbox", { name: "Message Bola Adeyemi" })).toHaveTextContent("half a thought");
    expect(screen.getByRole("button", { name: "Send" })).toBeEnabled();
  });

  test("Send is disabled for an empty message", () => {
    setup();
    expect(screen.getByRole("button", { name: "Send" })).toBeDisabled();
  });
});
