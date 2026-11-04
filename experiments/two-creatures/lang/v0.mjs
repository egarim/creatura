// Latin 0: the typed language two creatures may speak on the board (issue #47, docs/design-agent-language.md).
// One message per line, one of ten types. Every message decodes word for word into the owner's language (en, es, ru),
// and the messages that claim something about the world carry a check the world runs before the message lands:
//
//   claim   <task> [until <n>m]                          what I am working on
//   propose <topic> <key>=<value>...                     a plan, a split of the work, an API choice
//   accept  #<n>                                         I agree to proposal #n
//   object  #<n> because <text>                          I don't, and why
//   offer   <name> at <repo>:<path>                      a contract (a file in my repository)   check: the file exists
//   done    <task> check <check> [and <check>]...        finished, with the proof              check: every check passes
//   need    <thing> from <who> [because <check>]         I need something from my peer         check: it does not pass yet
//   block   <task> on <thing>                            I cannot go on until <thing>
//   change  <field> <old> -> <new>                       a field of a plan or contract changes
//   note    <text>                                       prose, 120 characters at most: counted, never translated
//
//   <check>: deployed <repo> | file <repo>:<path> | http <url> <status> | array <url>
//   <repo>: repo-a | repo-b      <url>: http://world:8080/... or http://world:7001/...      <who>: a | b
//
// This module is pure: parsing, the rules that need only the board's messages, decoding and the digest. Running a
// check is the board's job (images/world/board.mjs), because only the world can say what the world is.

export const VERSION = "latin/0";
export const TYPES = ["claim", "propose", "accept", "object", "offer", "done", "need", "block", "change", "note"];
export const LANGS = ["en", "es", "ru"];
export const NOTE_LIMIT = 120;

const ID = /^[a-z][a-z0-9._-]{0,39}$/;
const VALUE = /^[A-Za-z0-9._:/-]{1,60}$/;
const REPO = /^repo-[ab]$/;
const PATH = /^(?!.*\.\.)[A-Za-z0-9._/-]{1,100}$/;
const URL_ = /^http:\/\/world:(8080|7001)(\/[A-Za-z0-9._~%/?&=-]*)?$/;
const WHO = /^(?:creature-)?([ab])$/;

export const GRAMMAR = `Latin 0, one message per line:
  claim <task> [until <n>m] | propose <topic> <key>=<value>... | accept #<n> | object #<n> because <text>
  offer <name> at <repo>:<path> | done <task> check <check> [and <check>]... | need <thing> from <a|b> [because <check>]
  block <task> on <thing> | change <field> <old> -> <new> | note <text, at most ${NOTE_LIMIT} characters>
  <check>: deployed <repo> | file <repo>:<path> | http <url> <status> | array <url>
  names are lower case (a-z, 0-9, . _ -); <repo> is repo-a or repo-b; <url> is on http://world:8080 or http://world:7001`;

class Refusal extends Error {}
const refuse = (why) => { throw new Refusal(why); };

function id(token, what) {
  if (!token) refuse(`${what} is missing`);
  if (!ID.test(token)) refuse(`${what} '${token}' is not a name (lower case a-z, 0-9, . _ -, at most 40)`);
  return token;
}

function ref(token) {
  const m = /^#([1-9][0-9]{0,6})$/.exec(token ?? "");
  if (!m) refuse(`expected a message number like #3, got '${token ?? ""}'`);
  return Number(m[1]);
}

function repoPath(token, what) {
  const m = /^(repo-[ab]):(.+)$/.exec(token ?? "");
  if (!m) refuse(`${what} must be <repo>:<path>, like repo-a:API.md`);
  if (!PATH.test(m[2])) refuse(`'${m[2]}' is not a plain path`);
  return { repo: m[1], path: m[2] };
}

/** One check from the front of `t` (an array of tokens, consumed). */
function check(t) {
  const kind = t.shift();
  switch (kind) {
    case "deployed": {
      const repo = t.shift();
      if (!REPO.test(repo ?? "")) refuse(`deployed takes repo-a or repo-b, got '${repo ?? ""}'`);
      return { kind, repo };
    }
    case "file": return { kind, ...repoPath(t.shift(), "file") };
    case "http": {
      const url = t.shift();
      if (!URL_.test(url ?? "")) refuse(`'${url ?? ""}' is not a URL on world:8080 or world:7001`);
      const status = t.shift();
      if (!/^[1-5][0-9]{2}$/.test(status ?? "")) refuse(`http <url> takes a status like 200, got '${status ?? ""}'`);
      return { kind, url, status: Number(status) };
    }
    case "array": {
      const url = t.shift();
      if (!URL_.test(url ?? "")) refuse(`'${url ?? ""}' is not a URL on world:8080 or world:7001`);
      return { kind, url };
    }
    default: return refuse(`unknown check '${kind ?? ""}': deployed, file, http or array`);
  }
}

const end = (t, type) => { if (t.length) refuse(`unexpected '${t.join(" ")}' at the end of ${type}`); };

/**
 * Parse one line of Latin 0. Returns the message as data ({type, ...fields}), or throws an Error whose message
 * says what is wrong; `parse.safe` returns {ok, message} or {ok:false, error} instead.
 */
export function parse(line) {
  const text = String(line ?? "").trim();
  if (!text) refuse("empty message");
  if (/[\r\n]/.test(text)) refuse("one message per line");
  const [type, ...rest] = text.split(/\s+/);
  if (!TYPES.includes(type)) refuse(`'${type.slice(0, 20)}' is not a message type; the types are ${TYPES.join(", ")}`);
  const t = [...rest];
  switch (type) {
    case "claim": {
      const task = id(t.shift(), "the task");
      let until;
      if (t[0] === "until") {
        t.shift();
        const m = /^([1-9][0-9]{0,3})m$/.exec(t.shift() ?? "");
        if (!m) refuse("until takes minutes, like 30m");
        until = Number(m[1]);
      }
      end(t, type);
      return until ? { type, task, until } : { type, task };
    }
    case "propose": {
      const topic = id(t.shift(), "the topic");
      if (!t.length) refuse("propose needs at least one <key>=<value>");
      const terms = {};
      for (const kv of t) {
        const m = /^([^=]+)=(.+)$/.exec(kv);
        if (!m) refuse(`'${kv}' is not <key>=<value>`);
        id(m[1], "a key");
        if (!VALUE.test(m[2])) refuse(`the value of ${m[1]} must be one word (letters, digits, . _ : / -)`);
        if (m[1] in terms) refuse(`${m[1]} is given twice`);
        terms[m[1]] = m[2];
      }
      return { type, topic, terms };
    }
    case "accept": {
      const n = ref(t.shift());
      end(t, type);
      return { type, ref: n };
    }
    case "object": {
      const n = ref(t.shift());
      if (t.shift() !== "because") refuse("object #<n> because <text>");
      const because = t.join(" ");
      if (!because) refuse("object needs a reason after because");
      if (because.length > NOTE_LIMIT) refuse(`the reason is ${because.length} characters; at most ${NOTE_LIMIT}`);
      return { type, ref: n, because };
    }
    case "offer": {
      const name = id(t.shift(), "the contract's name");
      if (t.shift() !== "at") refuse("offer <name> at <repo>:<path>");
      const where = repoPath(t.shift(), "offer");
      end(t, type);
      return { type, name, ...where };
    }
    case "done": {
      const task = id(t.shift(), "the task");
      if (t.shift() !== "check") refuse("done <task> check <check>: a done carries its proof");
      const checks = [check(t)];
      while (t.length) {
        if (t.shift() !== "and") refuse("checks are joined with and");
        checks.push(check(t));
      }
      if (checks.length > 5) refuse("at most 5 checks");
      return { type, task, checks };
    }
    case "need": {
      const thing = id(t.shift(), "what you need");
      if (t.shift() !== "from") refuse("need <thing> from <a|b>");
      const who = WHO.exec(t.shift() ?? "")?.[1] ?? refuse("need ... from takes a or b");
      let because;
      if (t.length) {
        if (t.shift() !== "because") refuse("need <thing> from <who> because <check>");
        because = check(t);
        end(t, type);
      }
      return because ? { type, thing, from: who, because } : { type, thing, from: who };
    }
    case "block": {
      const task = id(t.shift(), "the task");
      if (t.shift() !== "on") refuse("block <task> on <thing>");
      const on = id(t.shift(), "what you wait for");
      end(t, type);
      return { type, task, on };
    }
    case "change": {
      const field = id(t.shift(), "the field");
      const from = t.shift(), arrow = t.shift(), to = t.shift();
      if (arrow !== "->" || !from || !to) refuse("change <field> <old> -> <new>");
      for (const v of [from, to]) if (!VALUE.test(v)) refuse(`'${v}' must be one word (letters, digits, . _ : / -)`);
      if (from === to) refuse("a change must change something");
      end(t, type);
      return { type, field, from, to };
    }
    case "note": {
      const text_ = rest.join(" ");
      if (!text_) refuse("note needs text");
      if (text_.length > NOTE_LIMIT) refuse(`a note is ${NOTE_LIMIT} characters at most (this one is ${text_.length}); say it as typed messages`);
      return { type, text: text_ };
    }
  }
  return refuse("unreachable");
}

parse.safe = (line) => {
  try { return { ok: true, message: parse(line) }; } catch (e) {
    if (e instanceof Refusal) return { ok: false, error: e.message };
    throw e;
  }
};

const fmtCheck = (c) => c.kind === "deployed" ? `deployed ${c.repo}` : c.kind === "file" ? `file ${c.repo}:${c.path}` : c.kind === "http" ? `http ${c.url} ${c.status}` : `array ${c.url}`;

/** The canonical text of a message: parse(format(m)) is m again. */
export function format(m) {
  switch (m.type) {
    case "claim": return `claim ${m.task}${m.until ? ` until ${m.until}m` : ""}`;
    case "propose": return `propose ${m.topic} ${Object.entries(m.terms).map(([k, v]) => `${k}=${v}`).join(" ")}`;
    case "accept": return `accept #${m.ref}`;
    case "object": return `object #${m.ref} because ${m.because}`;
    case "offer": return `offer ${m.name} at ${m.repo}:${m.path}`;
    case "done": return `done ${m.task} check ${m.checks.map(fmtCheck).join(" and ")}`;
    case "need": return `need ${m.thing} from ${m.from}${m.because ? ` because ${fmtCheck(m.because)}` : ""}`;
    case "block": return `block ${m.task} on ${m.on}`;
    case "change": return `change ${m.field} ${m.from} -> ${m.to}`;
    case "note": return `note ${m.text}`;
  }
  throw new Error(`not a message: ${JSON.stringify(m)}`);
}

/**
 * Whether a message claims the app is done: a done whose checks look at the published app (http://world:8080). A done
 * of one part ("done server check deployed repo-a") is a claim about that part, which its own checks settle; only a
 * claim about the app is the judge's to score, as the goal asks.
 */
export const claimsApp = (m) => m?.type === "done" && m.checks.some((c) => c.url?.startsWith("http://world:8080"));

/** The checks the world must run before a message lands, and whether they must pass (done, offer) or fail (need). */
export function checksFor(m) {
  if (m.type === "done") return { checks: m.checks, mustPass: true };
  if (m.type === "offer") return { checks: [{ kind: "file", repo: m.repo, path: m.path }], mustPass: true };
  if (m.type === "need" && m.because) return { checks: [m.because], mustPass: false };
  return { checks: [], mustPass: true };
}

/**
 * The board's rules that need only its messages ({n, from, text, message}), for a message from `from`: an accept or
 * object names a proposal by the other creature; an offer is in the sender's own repository; a need is from the peer;
 * and nothing repeats the sender's last message word for word, since a repeat changes nothing the peer does (the
 * threshold). Returns the refusal, or null.
 */
export function admit(m, { from, messages }) {
  const who = WHO.exec(from)?.[1] ?? from;
  if (m.type === "accept" || m.type === "object") {
    const target = messages.find((x) => x.n === m.ref);
    if (!target) return `there is no message #${m.ref}`;
    if (target.message?.type !== "propose") return `#${m.ref} is not a proposal`;
    if ((WHO.exec(target.from)?.[1] ?? target.from) === who) return `#${m.ref} is your own proposal`;
  }
  if (m.type === "offer" && m.repo !== `repo-${who}`) return `you offer from your own repository, repo-${who}`;
  if (m.type === "need" && m.from === who) return "you cannot need something from yourself";
  const canonical = format(m);
  const last = [...messages].reverse().find((x) => x.from === from && x.message);
  if (last && format(last.message) === canonical) return `no change: the same as your #${last.n}`;
  return null;
}

// ---- decoding into the owner's language ------------------------------------------------------------------------

const creature = (from) => `creature-${WHO.exec(from)?.[1] ?? from}`;

const CHECK_TEXT = {
  en: { deployed: (c) => `${c.repo} is deployed`, file: (c) => `${c.repo} has ${c.path}`, http: (c) => `${c.url} answers ${c.status}`, array: (c) => `${c.url} returns a JSON list`, and: " and " },
  es: { deployed: (c) => `${c.repo} está desplegado`, file: (c) => `${c.repo} contiene ${c.path}`, http: (c) => `${c.url} responde ${c.status}`, array: (c) => `${c.url} devuelve una lista JSON`, and: " y " },
  ru: { deployed: (c) => `${c.repo} развёрнут`, file: (c) => `в ${c.repo} есть ${c.path}`, http: (c) => `${c.url} отвечает ${c.status}`, array: (c) => `${c.url} возвращает JSON-список`, and: " и " },
};

const checkText = (lang, cs) => cs.map((c) => CHECK_TEXT[lang][c.kind](c)).join(CHECK_TEXT[lang].and);
const termsText = (terms) => Object.entries(terms).map(([k, v]) => `${k} → ${v}`).join(", ");

const SENTENCE = {
  en: {
    claim: (w, m) => `${w} takes on ${m.task}${m.until ? ` for ${m.until} min` : ""}.`,
    propose: (w, m) => `${w} proposes ${m.topic}: ${termsText(m.terms)}.`,
    accept: (w, m) => `${w} accepts proposal #${m.ref}.`,
    object: (w, m) => `${w} objects to proposal #${m.ref}: “${m.because}”.`,
    offer: (w, m) => `${w} offers the contract ${m.name}, in ${m.repo}/${m.path}.`,
    done: (w, m) => `${w} has finished ${m.task}; checked: ${checkText("en", m.checks)}.`,
    need: (w, m) => `${w} needs ${m.thing} from creature-${m.from}${m.because ? `, because this does not hold yet: ${checkText("en", [m.because])}` : ""}.`,
    block: (w, m) => `${w} is blocked on ${m.task}, waiting for ${m.on}.`,
    change: (w, m) => `${w} changes ${m.field} from ${m.from} to ${m.to}.`,
    note: (w, m) => `${w} notes (prose, not translated): “${m.text}”`,
  },
  es: {
    claim: (w, m) => `${w} se encarga de ${m.task}${m.until ? ` durante ${m.until} min` : ""}.`,
    propose: (w, m) => `${w} propone ${m.topic}: ${termsText(m.terms)}.`,
    accept: (w, m) => `${w} acepta la propuesta #${m.ref}.`,
    object: (w, m) => `${w} se opone a la propuesta #${m.ref}: «${m.because}».`,
    offer: (w, m) => `${w} ofrece el contrato ${m.name}, en ${m.repo}/${m.path}.`,
    done: (w, m) => `${w} terminó ${m.task}; comprobado: ${checkText("es", m.checks)}.`,
    need: (w, m) => `${w} necesita ${m.thing} de creature-${m.from}${m.because ? `, porque todavía no se cumple: ${checkText("es", [m.because])}` : ""}.`,
    block: (w, m) => `${w} está bloqueado en ${m.task}, esperando ${m.on}.`,
    change: (w, m) => `${w} cambia ${m.field} de ${m.from} a ${m.to}.`,
    note: (w, m) => `${w} anota (texto libre, sin traducir): «${m.text}»`,
  },
  ru: {
    claim: (w, m) => `${w} берёт на себя ${m.task}${m.until ? ` на ${m.until} мин` : ""}.`,
    propose: (w, m) => `${w} предлагает ${m.topic}: ${termsText(m.terms)}.`,
    accept: (w, m) => `${w} принимает предложение #${m.ref}.`,
    object: (w, m) => `${w} возражает против предложения #${m.ref}: «${m.because}».`,
    offer: (w, m) => `${w} предлагает контракт ${m.name}: ${m.repo}/${m.path}.`,
    done: (w, m) => `${w}: задача ${m.task} выполнена; проверено: ${checkText("ru", m.checks)}.`,
    need: (w, m) => `${w}: нужно ${m.thing} от creature-${m.from}${m.because ? `, потому что пока не выполнено: ${checkText("ru", [m.because])}` : ""}.`,
    block: (w, m) => `${w}: задача ${m.task} заблокирована, ждёт ${m.on}.`,
    change: (w, m) => `${w} меняет ${m.field}: ${m.from} → ${m.to}.`,
    note: (w, m) => `${w} отмечает (свободный текст, без перевода): «${m.text}»`,
  },
};

/** One message, as a sentence in the owner's language: deterministic, the same message always the same sentence. */
export function decode(m, from, lang = "en") {
  const table = SENTENCE[lang] ?? refuse(`no decoder for '${lang}': ${LANGS.join(", ")}`);
  return table[m.type](creature(from), m);
}

// ---- refusals, in the owner's language -------------------------------------------------------------------------

const REFUSAL = {
  en: { doesNot: (c) => `does not hold: ${c}`, already: (c) => `already holds: ${c}`, language: "not Latin", rule: "breaks a board rule", repeat: "a repeat", claim: (t) => `said ${t} was done` },
  es: { doesNot: (c) => `no se cumple: ${c}`, already: (c) => `ya se cumple: ${c}`, language: "no está en Latin", rule: "rompe una regla del tablero", repeat: "una repetición", claim: (t) => `dijo que ${t} estaba terminado` },
  ru: { doesNot: (c) => `не выполнено: ${c}`, already: (c) => `уже выполнено: ${c}`, language: "не на Latin", rule: "нарушает правило доски", repeat: "повтор", claim: (t) => `сообщил(а), что ${t} готово` },
};

/**
 * Why the world refused a message, as the owner reads it: the board records the reason's kind and the check that
 * failed ({from, text, reason: language|rule|repeat|world|needless, check}), so the sentence is built here, in the
 * owner's language, and never copied from the board's English.
 */
export function refusalText(r, lang = "en") {
  const R = REFUSAL[lang] ?? refuse(`no decoder for '${lang}'`);
  const p = parse.safe(r.text);
  const what = p.ok && p.message.type === "done" ? R.claim(p.message.task) : `“${String(r.text).slice(0, 50)}”`;
  const why = r.reason === "world" && r.check ? R.doesNot(checkText(lang, [r.check]))
    : r.reason === "needless" && r.check ? R.already(checkText(lang, [r.check]))
    : R[r.reason] ?? R.language;
  return `${creature(r.from)} ${what} (${why})`;
}

// ---- the digest: what the person in the loop reads -------------------------------------------------------------

const DIGEST = {
  en: { title: "Where things stand", plan: "Agreed", offer: "Contract", done: "Done (checked by the world)", block: "Blocked", need: "Needs", refused: "Refused by the world", changed: "Changed", other: (n) => `${n} more message${n === 1 ? "" : "s"} hidden (claims, notes, answers); ask for the full log`, nothing: "Nothing yet.", more: (n) => `and ${n} more line${n === 1 ? "" : "s"} of state; ask for the full digest`, stats: (l, r) => `${l} messages, ${r} refused` },
  es: { title: "Cómo van las cosas", plan: "Acordado", offer: "Contrato", done: "Hecho (comprobado por el mundo)", block: "Bloqueado", need: "Necesita", refused: "Rechazado por el mundo", changed: "Cambiado", other: (n) => `${n} mensaje${n === 1 ? "" : "s"} más oculto${n === 1 ? "" : "s"} (encargos, notas, respuestas); pide el registro completo`, nothing: "Nada todavía.", more: (n) => `y ${n} línea${n === 1 ? "" : "s"} más de estado; pide el resumen completo`, stats: (l, r) => `${l} mensajes, ${r} rechazados` },
  ru: { title: "Как идут дела", plan: "Согласовано", offer: "Контракт", done: "Сделано (проверено миром)", block: "Заблокировано", need: "Нужно", refused: "Отклонено миром", changed: "Изменено", other: (n) => `ещё скрыто сообщений: ${n} (задачи, заметки, ответы); полный журнал по запросу`, nothing: "Пока ничего.", more: (n) => `и ещё строк состояния: ${n}; полная сводка по запросу`, stats: (l, r) => `сообщений: ${l}, отклонено: ${r}` },
};

/**
 * What a person can take in at a glance (issue #47, H1–H4): the state the messages leave, not the messages. Agreed
 * plans, landed dones, the blocks and needs still open, and the claims the world refused, in at most `limit` lines,
 * whatever the number of messages. A done masks the blocks and needs it resolves; claims, notes and answers are
 * counted, not shown. `landed` are the board's messages ({n, from, message}); `refused` its refusals ({from, text, reason, check}).
 */
export function digest(landed, refused = [], { lang = "en", limit = 8 } = {}) {
  const L = DIGEST[lang] ?? refuse(`no digest for '${lang}'`);
  const msgs = landed.filter((x) => x.message);
  const byN = new Map(msgs.map((x) => [x.n, x]));
  const lines = [];
  const shown = new Set();
  const add = (label, text, n) => { lines.push(`${label}: ${text}`); if (n) shown.add(n); };

  // refused claims first: a claim the world did not believe is the one thing a person must not miss
  const refusedDones = refused.filter((r) => /^\s*done\b/.test(r.text));
  if (refusedDones.length) add(L.refused, refusedDones.map((r) => refusalText(r, lang)).join("; "));
  for (const x of msgs) {
    if (x.message.type === "offer") { add(L.offer, `${x.message.name}: ${x.message.repo}/${x.message.path} (${creature(x.from)})`, x.n); }
  }

  for (const x of msgs) {
    if (x.message.type !== "accept") continue;
    const p = byN.get(x.message.ref);
    if (p) { add(L.plan, `${p.message.topic}: ${termsText(p.message.terms)} (#${p.n}, ${creature(p.from)} ↔ ${creature(x.from)})`, x.n); shown.add(p.n); }
  }
  const doneLines = msgs.filter((x) => x.message.type === "done");
  if (doneLines.length) add(L.done, doneLines.map((x) => `${x.message.task} (${creature(x.from)})`).join(", "));
  doneLines.forEach((x) => shown.add(x.n));

  const resolved = (x) => msgs.some((y) => y.n > x.n && ((y.message.type === "done" && (y.message.task === x.message.on || y.message.task === x.message.task || y.message.task === x.message.thing)) || (y.from === x.from && y.message.type === "claim" && y.message.task === x.message.task)));
  const open = (type) => msgs.filter((x) => x.message.type === type && !resolved(x));
  for (const x of open("block")) add(L.block, `${creature(x.from)}: ${x.message.task} ← ${x.message.on}`, x.n);
  for (const x of open("need")) add(L.need, `${creature(x.from)}: ${x.message.thing} ← creature-${x.message.from}`, x.n);
  const changes = new Map();
  for (const x of msgs) if (x.message.type === "change") changes.set(x.message.field, x);
  for (const x of changes.values()) add(L.changed, `${x.message.field} → ${x.message.to}`, x.n);

  const hidden = msgs.filter((x) => !shown.has(x.n)).length;
  let body = lines;
  if (body.length > limit - 1) {
    const cut = body.length - (limit - 2);
    body = [...body.slice(0, limit - 2), L.more(cut)];
  }
  const out = [`${L.title} (${L.stats(msgs.length, refused.length)})`, ...(body.length ? body : [L.nothing])];
  if (hidden && out.length < limit) out.push(L.other(hidden));
  return out.slice(0, limit);
}
