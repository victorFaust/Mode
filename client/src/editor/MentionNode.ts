import {
  $applyNodeReplacement, TextNode, type EditorConfig, type LexicalNode, type NodeKey, type SerializedTextNode, type Spread,
} from "lexical";
import styles from "./editor.module.css";

export type SerializedMentionNode = Spread<{ userId: string }, SerializedTextNode>;

/** "@Name" shown as a chip; exported to Markdown as the server's "@{userId}" token (or "@channel"). */
export class MentionNode extends TextNode {
  __userId: string;

  static getType(): string {
    return "mention";
  }

  static clone(node: MentionNode): MentionNode {
    return new MentionNode(node.__userId, node.__text, node.__key);
  }

  static importJSON(json: SerializedMentionNode): MentionNode {
    return $createMentionNode(json.userId, json.text);
  }

  constructor(userId: string, text: string, key?: NodeKey) {
    super(text, key);
    this.__userId = userId;
  }

  exportJSON(): SerializedMentionNode {
    return { ...super.exportJSON(), type: "mention", userId: this.__userId, version: 1 };
  }

  createDOM(config: EditorConfig): HTMLElement {
    const dom = super.createDOM(config);
    dom.className = styles.mention;
    dom.dataset.mention = this.__userId;
    return dom;
  }

  getUserId(): string {
    return this.getLatest().__userId;
  }

  isTextEntity(): true {
    return true;
  }

  canInsertTextBefore(): boolean {
    return false;
  }

  canInsertTextAfter(): boolean {
    return false;
  }
}

export function $createMentionNode(userId: string, text: string): MentionNode {
  // Token mode: the chip is selected and deleted as one unit.
  return $applyNodeReplacement(new MentionNode(userId, text).setMode("token"));
}

export function $isMentionNode(node: LexicalNode | null | undefined): node is MentionNode {
  return node instanceof MentionNode;
}
