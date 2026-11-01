import { CodeNode } from "@lexical/code";
import { LinkNode } from "@lexical/link";
import { ListItemNode, ListNode } from "@lexical/list";
import {
  $convertFromMarkdownString, $convertToMarkdownString, BOLD_ITALIC_STAR, BOLD_ITALIC_UNDERSCORE, BOLD_STAR, BOLD_UNDERSCORE,
  CODE, INLINE_CODE, ITALIC_STAR, ITALIC_UNDERSCORE, LINK, ORDERED_LIST, QUOTE, STRIKETHROUGH, UNORDERED_LIST,
  type TextMatchTransformer, type Transformer,
} from "@lexical/markdown";
import { QuoteNode } from "@lexical/rich-text";
import type { Klass, LexicalNode } from "lexical";
import { $createMentionNode, $isMentionNode, MentionNode } from "./MentionNode";

export const editorNodes: Klass<LexicalNode>[] = [QuoteNode, ListNode, ListItemNode, CodeNode, LinkNode, MentionNode];

function mentionTransformer(nameOf: (userId: string) => string | undefined): TextMatchTransformer {
  return {
    type: "text-match",
    dependencies: [MentionNode],
    export: (node) => {
      if (!$isMentionNode(node)) return null;
      const id = node.getUserId();
      return id === "channel" || id === "workspace" ? `@${id}` : `@{${id}}`;
    },
    importRegExp: /@\{([^{}\s]{1,64})\}/,
    regExp: /@\{([^{}\s]{1,64})\}$/,
    replace: (textNode, match) => {
      const id = match[1];
      textNode.replace($createMentionNode(id, `@${nameOf(id) ?? "Unknown user"}`));
    },
    trigger: "}",
  };
}

/** The Markdown the server accepts: no headings, tables, images or highlights. */
export function messageTransformers(nameOf: (userId: string) => string | undefined): Transformer[] {
  return [
    mentionTransformer(nameOf),
    CODE, QUOTE, UNORDERED_LIST, ORDERED_LIST,
    INLINE_CODE, BOLD_ITALIC_STAR, BOLD_ITALIC_UNDERSCORE, BOLD_STAR, BOLD_UNDERSCORE, ITALIC_STAR, ITALIC_UNDERSCORE, STRIKETHROUGH,
    LINK,
  ];
}

/** Must run inside editor.update or the initial editorState callback. */
export function $importMessage(markdown: string, transformers: Transformer[]): void {
  // The server stores raw "<" escaped; the editor shows the character and the server escapes it again on save.
  $convertFromMarkdownString(markdown.replace(/&lt;/g, "<"), transformers, undefined, true);
}

/** Must run inside editor.read. */
export function $exportMessage(transformers: Transformer[]): string {
  return $convertToMarkdownString(transformers, undefined, true).trim();
}
