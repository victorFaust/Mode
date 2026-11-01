import { expect, test } from "@playwright/test";
import { expectAccessible, signIn, startChat, unique } from "./helpers";

test("sign in, post a message in a chat and reply in a thread", async ({ page }) => {
  const id = unique();
  await signIn(page);
  await startChat(page);

  const composer = page.getByRole("textbox", { name: "Message Bola Adeyemi" });
  await composer.fill(`Kick-off notes ${id} are **ready**`);
  await composer.press("Enter");

  const feed = page.getByRole("log", { name: "Messages in Bola Adeyemi" });
  const message = feed.getByRole("article").filter({ hasText: `Kick-off notes ${id}` });
  await expect(message).toBeVisible();
  await expect(message.locator("strong", { hasText: "ready" })).toBeVisible();
  await expect(composer).toHaveValue("");

  await message.hover();
  await message.getByRole("button", { name: "Reply in thread" }).click();
  const thread = page.getByRole("complementary", { name: "Details" });
  const reply = thread.getByRole("textbox", { name: "Reply" });
  await reply.fill(`Thanks, reviewing ${id}`);
  await reply.press("Enter");

  await expect(thread.getByRole("article").filter({ hasText: `Thanks, reviewing ${id}` })).toBeVisible();
  await expect(message.getByRole("button", { name: /1 reply/ })).toBeVisible();
  await expectAccessible(page);
});
