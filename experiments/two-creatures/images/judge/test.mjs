// The outside test: the only judge of whether the app shipped. It runs where the creatures can't see or change it,
// against http://<world>:8080/, with two browsers, and prints one JSON line: {"score": 0..5, "steps": [...]}.
//   1 the page loads
//   2 in browser 1, adding an item with "Item" and "Add" shows it
//   3 browser 2, already open, shows it within 5 s without a reload
//   4 browser 2 checks it; browser 1 shows it checked within 5 s
//   5 after the published containers restart, it is still there and still checked
// Steps run in order and stop at the first failure, since each needs the one before. Each check adds its own item
// ("milk <n>"), so repeated checks during a run don't see each other's items.
import { chromium } from "playwright";

const world = process.env.WORLD ?? "world";
const app = `http://${world}:8080/`;
const name = `milk ${Date.now().toString(36)}`;
const WITHIN = 5000;
const steps = [];
let browser;

async function step(n, title, fn) {
  const t0 = Date.now();
  try {
    await fn();
    steps.push({ n, title, pass: true, ms: Date.now() - t0 });
    return true;
  } catch (e) {
    steps.push({ n, title, pass: false, ms: Date.now() - t0, detail: String(e?.message ?? e).split("\n")[0].slice(0, 300) });
    return false;
  }
}

async function open(page) {
  const res = await page.goto(app, { timeout: 15000, waitUntil: "domcontentloaded" });
  if (!res || res.status() >= 400) throw new Error(`GET / answered ${res ? res.status() : "nothing"}`);
  await page.waitForLoadState("networkidle", { timeout: 5000 }).catch(() => {});
}

function itemText(page) {
  return page.getByText(name, { exact: true }).first();
}

/** The checkbox that belongs to the item: by its accessible name if the app labels it, else the one in the item's row. */
async function itemBox(page) {
  const named = page.getByRole("checkbox", { name, exact: true });
  if (await named.count()) return named.first();
  const row = page.locator("li, tr, [role=listitem], [role=row], div").filter({ hasText: name }).last();
  return row.getByRole("checkbox").first();
}

async function main() {
  browser = await chromium.launch();
  const one = await (await browser.newContext()).newPage();
  const two = await (await browser.newContext()).newPage();

  (await step(1, "the page loads", async () => {
    await open(one);
    await open(two);
  })) &&
    (await step(2, "adding an item shows it", async () => {
      await one.getByLabel("Item", { exact: true }).fill(name, { timeout: 5000 });
      await one.getByRole("button", { name: "Add", exact: true }).click({ timeout: 5000 });
      await itemText(one).waitFor({ timeout: WITHIN });
    })) &&
    (await step(3, "the other browser shows it within 5 s, without a reload", async () => {
      await itemText(two).waitFor({ timeout: WITHIN });
    })) &&
    (await step(4, "checked in one browser, the other shows it checked within 5 s", async () => {
      await (await itemBox(two)).check({ timeout: 5000 });
      const box = await itemBox(one);
      await box.waitFor({ timeout: WITHIN });
      const t0 = Date.now();
      while (!(await box.isChecked())) {
        if (Date.now() - t0 > WITHIN) throw new Error("browser 1 never showed it checked");
        await one.waitForTimeout(250);
      }
    })) &&
    (await step(5, "after a restart it is still there and still checked", async () => {
      const res = await fetch(`http://${world}:7001/restart`, { method: "POST" });
      if (!res.ok) throw new Error(`world could not restart the app (${res.status})`);
      const t0 = Date.now();
      for (;;) {
        try {
          await open(one);
          await itemText(one).waitFor({ timeout: 3000 });
          break;
        } catch (e) {
          if (Date.now() - t0 > 60000) throw new Error(`gone after the restart: ${String(e?.message ?? e).split("\n")[0]}`);
          await new Promise((r) => setTimeout(r, 2000));
        }
      }
      if (!(await (await itemBox(one)).isChecked())) throw new Error("there after the restart, but no longer checked");
    }));
}

try {
  await main();
} catch (e) {
  steps.push({ n: 0, title: "the test itself", pass: false, detail: String(e?.message ?? e).slice(0, 300) });
} finally {
  await browser?.close();
}
const score = steps.filter((s) => s.pass && s.n > 0).length;
console.log(JSON.stringify({ at: new Date().toISOString(), app, item: name, score, shipped: score === 5, steps }));
