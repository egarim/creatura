// Cellspeak v1: the typed language two creatures speak on the board (issue #47, docs/design-agent-language.md).
// v0 (lang/v0.mjs) stays as it was, so the runs made with it (log #95, #96) decode as they were. v1 adds what the
// first paid run (log #96) wrote as prose because v0 had no word for it, and a tool to speak it:
//
//   claim    <task> [until <n>m]                         what I am working on
//   propose  <topic> <key>=<value>...                    a plan, a split of the work, an API choice
//   accept   #<n>                                        I agree to proposal #n
//   object   #<n> because <text>                         I don't, and why
//   offer    <name> at <repo>:<path>                     a contract (a file in my repository)   world: the file exists
//   done     <task> check <check> [and <check>]...       I finished my work                     world: every check passes;
//                                                         one check must be on my own repository
//   verified <task> of <a|b> check <check> [and ...]     NEW: I checked my peer's work          world: every check passes
//   need     <thing> from <a|b> [because <check>]        I need something from my peer          world: it does not pass yet
//   ask      <action> of <a|b> [because <text>]          NEW: please do this                    (answered by a done or verified)
//   block    <task> on <thing>                           I cannot go on until <thing>
//   change   <field> <old> -> <new>                      a field of a plan or contract changes
//   note     <text>                                      prose, 120 characters at most: counted, never translated
//
//   NEW: a name (<task>, <topic>, <thing>, <action>, ...) is one lower-case word, or several words in double quotes:
//   claim "shared shopping list" until 40m.
//
// THE TOOL. An agent need not write the text at all: `encode(fields)` takes the message as typed fields (the JSON a
// model's tool call carries, TOOL below) and writes its one canonical text. The same fields always give the same text,
// the text parses back to the same message, and that message always decodes to the same sentence in each language.
// So what was sent, what the peer reads and what the owner reads are three views of one value, and nothing in between
// is free text a model wrote, apart from a note's or a reason's own words.

export const VERSION = "cellspeak/1";
export const TYPES = ["claim", "propose", "accept", "object", "offer", "done", "verified", "need", "ask", "block", "change", "note"];
export const LANGS = ["en", "es", "ru"];
export const NOTE_LIMIT = 120;
export const CHECK_KINDS = ["deployed", "file", "http", "array"];

const ID = /^[a-z][a-z0-9._-]{0,39}$/;
const WORDS = /^[A-Za-z0-9._-]+(?: [A-Za-z0-9._-]+)*$/;
const VALUE = /^[A-Za-z0-9._:/-]{1,60}$/;
const REPO = /^repo-[ab]$/;
const PATH = /^(?!.*\.\.)[A-Za-z0-9._/-]{1,100}$/;
const URL_ = /^http:\/\/world:(8080|7001)(\/[A-Za-z0-9._~%/?&=-]*)?$/;
const WHO = /^(?:creature-)?([ab])$/;

export const GRAMMAR = `cellspeak v1, one message per line:
  claim <task> [until <n>m] | propose <topic> <key>=<value>... | accept #<n> | object #<n> because <text>
  offer <name> at <repo>:<path> | done <task> check <check> [and <check>]... (one check on your own repository)
  verified <task> of <a|b> check <check> [and <check>]... | need <thing> from <a|b> [because <check>]
  ask <action> of <a|b> [because <text>] | block <task> on <thing> | change <field> <old> -> <new> | note <text, at most ${NOTE_LIMIT} characters>
  <check>: deployed <repo> | file <repo>:<path> | http <url> <status> | array <url>
  a name is one lower-case word (a-z, 0-9, . _ -) or words in double quotes; <repo> is repo-a or repo-b; <url> is on http://world:8080 or http://world:7001`;

class Refusal extends Error {}
const refuse = (why) => { throw new Refusal(why); };

/** A name: a bare lower-case word, or up to 40 characters of words in double quotes (the quotes are not part of it). */
function name(token, what) {
  if (!token) refuse(`${what} is missing`);
  const quoted = /^"(.*)"$/.exec(token);
  if (quoted) {
    if (!WORDS.test(quoted[1]) || quoted[1].length > 40) refuse(`${what} "${quoted[1].slice(0, 50)}" is not a name (words of letters, digits, . _ -, one space apart, at most 40)`);
    return quoted[1];
  }
  if (!ID.test(token)) refuse(`${what} '${token}' is not a name (one lower-case word, or several in double quotes)`);
  return token;
}
const fmtName = (n) => (ID.test(n) ? n : `"${n}"`);

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

const who = (token, what) => WHO.exec(token ?? "")?.[1] ?? refuse(`${what} takes a or b`);

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

function checks(t, type) {
  if (t.shift() !== "check") refuse(`${type} <task> ... check <check>: it carries its proof`);
  const out = [check(t)];
  while (t.length) {
    if (t.shift() !== "and") refuse("checks are joined with and");
    out.push(check(t));
  }
  if (out.length > 5) refuse("at most 5 checks");
  return out;
}

const end = (t, type) => { if (t.length) refuse(`unexpected '${t.join(" ")}' at the end of ${type}`); };

function text(rest, what) {
  const s = rest.join(" ");
  if (!s) refuse(`${what} needs text`);
  if (s.length > NOTE_LIMIT) refuse(`${what} is ${NOTE_LIMIT} characters at most (this one is ${s.length}); say it as typed messages`);
  return s;
}

/**
 * Parse one line of cellspeak v1. Returns the message as data ({type, ...fields}), or throws an Error whose message
 * says what is wrong; `parse.safe` returns {ok, message} or {ok:false, error} instead.
 */
export function parse(line) {
  const src = String(line ?? "").trim();
  if (!src) refuse("empty message");
  if (/[\r\n]/.test(src)) refuse("one message per line");
  const [type, ...rest] = src.match(/"[^"]*"|\S+/g);
  if (!TYPES.includes(type)) refuse(`'${type.slice(0, 20)}' is not a message type; the types are ${TYPES.join(", ")}`);
  const t = [...rest];
  switch (type) {
    case "claim": {
      const task = name(t.shift(), "the task");
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
      const topic = name(t.shift(), "the topic");
      if (!t.length) refuse("propose needs at least one <key>=<value>");
      const terms = {};
      for (const kv of t) {
        const m = /^([^=]+)=(.+)$/.exec(kv);
        if (!m) refuse(`'${kv}' is not <key>=<value>`);
        if (!ID.test(m[1])) refuse(`the key '${m[1]}' is not one lower-case word`);
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
      return { type, ref: n, because: text(t, "the reason") };
    }
    case "offer": {
      const n = name(t.shift(), "the contract's name");
      if (t.shift() !== "at") refuse("offer <name> at <repo>:<path>");
      const where = repoPath(t.shift(), "offer");
      end(t, type);
      return { type, name: n, ...where };
    }
    case "done": {
      const task = name(t.shift(), "the task");
      return { type, task, checks: checks(t, type) };
    }
    case "verified": {
      const task = name(t.shift(), "the task");
      if (t.shift() !== "of") refuse("verified <task> of <a|b> check <check>");
      const of = who(t.shift(), "verified ... of");
      return { type, task, of, checks: checks(t, type) };
    }
    case "need": {
      const thing = name(t.shift(), "what you need");
      if (t.shift() !== "from") refuse("need <thing> from <a|b>");
      const from = who(t.shift(), "need ... from");
      let because;
      if (t.length) {
        if (t.shift() !== "because") refuse("need <thing> from <who> because <check>");
        because = check(t);
        end(t, type);
      }
      return because ? { type, thing, from, because } : { type, thing, from };
    }
    case "ask": {
      const action = name(t.shift(), "what you ask for");
      if (t.shift() !== "of") refuse("ask <action> of <a|b>");
      const of = who(t.shift(), "ask ... of");
      if (!t.length) return { type, action, of };
      if (t.shift() !== "because") refuse("ask <action> of <who> because <text>");
      return { type, action, of, because: text(t, "the reason") };
    }
    case "block": {
      const task = name(t.shift(), "the task");
      if (t.shift() !== "on") refuse("block <task> on <thing>");
      const on = name(t.shift(), "what you wait for");
      end(t, type);
      return { type, task, on };
    }
    case "change": {
      const field = name(t.shift(), "the field");
      const from = t.shift(), arrow = t.shift(), to = t.shift();
      if (arrow !== "->" || !from || !to) refuse("change <field> <old> -> <new>");
      for (const v of [from, to]) if (!VALUE.test(v)) refuse(`'${v}' must be one word (letters, digits, . _ : / -)`);
      if (from === to) refuse("a change must change something");
      end(t, type);
      return { type, field, from, to };
    }
    case "note": return { type, text: text(rest, "a note") };
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
const fmtChecks = (cs) => `check ${cs.map(fmtCheck).join(" and ")}`;

/** The canonical text of a message: parse(format(m)) is m again. */
export function format(m) {
  switch (m.type) {
    case "claim": return `claim ${fmtName(m.task)}${m.until ? ` until ${m.until}m` : ""}`;
    case "propose": return `propose ${fmtName(m.topic)} ${Object.entries(m.terms).map(([k, v]) => `${k}=${v}`).join(" ")}`;
    case "accept": return `accept #${m.ref}`;
    case "object": return `object #${m.ref} because ${m.because}`;
    case "offer": return `offer ${fmtName(m.name)} at ${m.repo}:${m.path}`;
    case "done": return `done ${fmtName(m.task)} ${fmtChecks(m.checks)}`;
    case "verified": return `verified ${fmtName(m.task)} of ${m.of} ${fmtChecks(m.checks)}`;
    case "need": return `need ${fmtName(m.thing)} from ${m.from}${m.because ? ` because ${fmtCheck(m.because)}` : ""}`;
    case "ask": return `ask ${fmtName(m.action)} of ${m.of}${m.because ? ` because ${m.because}` : ""}`;
    case "block": return `block ${fmtName(m.task)} on ${fmtName(m.on)}`;
    case "change": return `change ${fmtName(m.field)} ${m.from} -> ${m.to}`;
    case "note": return `note ${m.text}`;
  }
  throw new Error(`not a message: ${JSON.stringify(m)}`);
}

// ---- the tool: typed fields in, one canonical message out ------------------------------------------------------

/**
 * The message tool, as a function a model can call (OpenAI-style JSON schema). One flat object, so every model's tool
 * calling can fill it: `type` picks the message, and only the fields that type uses are read.
 */
export const TOOL = {
  name: "message",
  description: "Send your peer one cellspeak message on the board. Fill `type` and the fields that type uses; the tool writes the message, the world runs its checks, and you get back the message number or the reason it was refused. "
    + "claim: task [until]. propose: topic, terms. accept: ref. object: ref, because. offer: name, repo, path. done: task, checks (one on your own repository). verified: task, who (your peer), checks. "
    + "need: thing, who [checks: one check that does not hold yet]. ask: action, who [because]. block: task, on. change: field, old, new. note: text (120 characters, prose; use it only when no other type fits).",
  parameters: {
    type: "object",
    properties: {
      type: { type: "string", enum: TYPES },
      task: { type: "string", description: "a name: one lower-case word, or a few words, at most 40 characters" },
      topic: { type: "string" }, name: { type: "string" }, thing: { type: "string" }, action: { type: "string" }, on: { type: "string" }, field: { type: "string" },
      until: { type: "integer", description: "minutes" },
      terms: { type: "object", additionalProperties: { type: "string" }, description: "propose: key to one-word value" },
      ref: { type: "integer", description: "the number of the proposal you answer" },
      because: { type: "string", description: "object, ask: the reason, 120 characters at most" },
      repo: { type: "string", enum: ["repo-a", "repo-b"] },
      path: { type: "string" },
      who: { type: "string", enum: ["a", "b"], description: "verified, need, ask: your peer" },
      checks: {
        type: "array", maxItems: 5,
        items: {
          type: "object",
          properties: { kind: { type: "string", enum: CHECK_KINDS }, repo: { type: "string", enum: ["repo-a", "repo-b"] }, path: { type: "string" }, url: { type: "string", description: "on http://world:8080 or http://world:7001" }, status: { type: "integer" } },
          required: ["kind"],
        },
      },
      old: { type: "string" }, new: { type: "string" },
      text: { type: "string", description: "note: 120 characters at most" },
    },
    required: ["type"],
  },
};

const squash = (s) => (typeof s === "string" ? s.replace(/\s+/g, " ").trim() : s);
const need_ = (f, k) => { const v = squash(f[k]); if (v === undefined || v === null || v === "") refuse(`${f.type} needs ${k}`); return v; };
const checksIn = (f) => (Array.isArray(f.checks) ? f.checks : []).map((c) => {
  if (!CHECK_KINDS.includes(c?.kind)) refuse(`a check's kind is one of ${CHECK_KINDS.join(", ")}`);
  if (c.kind === "deployed") return { kind: c.kind, repo: c.repo };
  if (c.kind === "file") return { kind: c.kind, repo: c.repo, path: c.path };
  if (c.kind === "http") return { kind: c.kind, url: c.url, status: Number(c.status ?? 200) };
  return { kind: c.kind, url: c.url };
});

/**
 * Typed fields (a tool call's arguments) to {message, text}: the message as data and its one canonical text. Pure and
 * deterministic; it refuses what the grammar refuses, with the same reasons, because the text it writes is parsed
 * again before it is returned.
 */
export function encode(fields) {
  const f = fields ?? {};
  if (!TYPES.includes(f.type)) refuse(`type is one of ${TYPES.join(", ")}`);
  let m;
  switch (f.type) {
    case "claim": m = { type: f.type, task: need_(f, "task"), ...(f.until ? { until: Number(f.until) } : {}) }; break;
    case "propose": m = { type: f.type, topic: need_(f, "topic"), terms: Object.fromEntries(Object.entries(f.terms ?? {}).map(([k, v]) => [squash(k), squash(String(v))])) }; break;
    case "accept": m = { type: f.type, ref: Number(need_(f, "ref")) }; break;
    case "object": m = { type: f.type, ref: Number(need_(f, "ref")), because: need_(f, "because") }; break;
    case "offer": m = { type: f.type, name: need_(f, "name"), repo: need_(f, "repo"), path: need_(f, "path") }; break;
    case "done": m = { type: f.type, task: need_(f, "task"), checks: checksIn(f) }; break;
    case "verified": m = { type: f.type, task: need_(f, "task"), of: need_(f, "who"), checks: checksIn(f) }; break;
    case "need": { const cs = checksIn(f); m = { type: f.type, thing: need_(f, "thing"), from: need_(f, "who"), ...(cs.length ? { because: cs[0] } : {}) }; break; }
    case "ask": m = { type: f.type, action: need_(f, "action"), of: need_(f, "who"), ...(squash(f.because) ? { because: squash(f.because) } : {}) }; break;
    case "block": m = { type: f.type, task: need_(f, "task"), on: need_(f, "on") }; break;
    case "change": m = { type: f.type, field: need_(f, "field"), from: need_(f, "old"), to: need_(f, "new") }; break;
    case "note": m = { type: f.type, text: need_(f, "text") }; break;
  }
  if ((m.type === "done" || m.type === "verified") && !m.checks.length) refuse(`${m.type} needs checks: it carries its proof`);
  // a name the grammar keeps lower case when it is one word: the tool lowers it, so "Server" and "server" are one task
  for (const k of ["task", "topic", "name", "thing", "action", "on", "field"]) if (typeof m[k] === "string" && !m[k].includes(" ")) m[k] = m[k].toLowerCase();
  const text_ = format(m);
  const message = parse(text_);
  return { message, text: text_ };
}

encode.safe = (fields) => {
  try { return { ok: true, ...encode(fields) }; } catch (e) {
    if (e instanceof Refusal) return { ok: false, error: e.message };
    throw e;
  }
};

/**
 * Whether a message claims the app is done: a done whose checks look at the published app (http://world:8080). A done
 * of one part is settled by its own checks; only a claim about the app is the judge's to score.
 */
export const claimsApp = (m) => m?.type === "done" && m.checks.some((c) => c.url?.startsWith("http://world:8080"));

/** The checks the world must run before a message lands, and whether they must pass (done, verified, offer) or fail (need). */
export function checksFor(m) {
  if (m.type === "done" || m.type === "verified") return { checks: m.checks, mustPass: true };
  if (m.type === "offer") return { checks: [{ kind: "file", repo: m.repo, path: m.path }], mustPass: true };
  if (m.type === "need" && m.because) return { checks: [m.because], mustPass: false };
  return { checks: [], mustPass: true };
}

/**
 * The board's rules that need only its messages ({n, from, text, message}), for a message from `from`: an accept or
 * object names a proposal by the other creature; an offer is in the sender's own repository; a done carries a check on
 * the sender's own repository (to report the peer's work, say verified); a verified, need or ask is about the peer;
 * nothing repeats the sender's last message word for word (the threshold). Returns the refusal, or null.
 */
export function admit(m, { from, messages }) {
  const me = WHO.exec(from)?.[1] ?? from;
  if (m.type === "accept" || m.type === "object") {
    const target = messages.find((x) => x.n === m.ref);
    if (!target) return `there is no message #${m.ref}`;
    if (target.message?.type !== "propose") return `#${m.ref} is not a proposal`;
    if ((WHO.exec(target.from)?.[1] ?? target.from) === me) return `#${m.ref} is your own proposal`;
  }
  if (m.type === "offer" && m.repo !== `repo-${me}`) return `you offer from your own repository, repo-${me}`;
  if (m.type === "done" && !m.checks.some((c) => c.repo === `repo-${me}`)) return `a done is about your own work: add a check on repo-${me} (deployed repo-${me} or file repo-${me}:<path>); to report your peer's work, say verified`;
  if (m.type === "verified" && m.of === me) return "verified is for your peer's work; for your own, say done";
  if ((m.type === "need" && m.from === me) || (m.type === "ask" && m.of === me)) return `you cannot ${m.type} something of yourself`;
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
    verified: (w, m) => `${w} checked creature-${m.of}'s ${m.task}: ${checkText("en", m.checks)}.`,
    need: (w, m) => `${w} needs ${m.thing} from creature-${m.from}${m.because ? `, because this does not hold yet: ${checkText("en", [m.because])}` : ""}.`,
    ask: (w, m) => `${w} asks creature-${m.of} to do ${m.action}${m.because ? `: “${m.because}”` : ""}.`,
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
    verified: (w, m) => `${w} comprobó ${m.task} de creature-${m.of}: ${checkText("es", m.checks)}.`,
    need: (w, m) => `${w} necesita ${m.thing} de creature-${m.from}${m.because ? `, porque todavía no se cumple: ${checkText("es", [m.because])}` : ""}.`,
    ask: (w, m) => `${w} pide a creature-${m.of} que haga ${m.action}${m.because ? `: «${m.because}»` : ""}.`,
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
    verified: (w, m) => `${w} проверил(а) ${m.task} у creature-${m.of}: ${checkText("ru", m.checks)}.`,
    need: (w, m) => `${w}: нужно ${m.thing} от creature-${m.from}${m.because ? `, потому что пока не выполнено: ${checkText("ru", [m.because])}` : ""}.`,
    ask: (w, m) => `${w} просит creature-${m.of} сделать ${m.action}${m.because ? `: «${m.because}»` : ""}.`,
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
  en: { doesNot: (c) => `does not hold: ${c}`, already: (c) => `already holds: ${c}`, language: "not cellspeak", rule: "breaks a board rule", repeat: "a repeat", claim: (t) => `said ${t} was done` },
  es: { doesNot: (c) => `no se cumple: ${c}`, already: (c) => `ya se cumple: ${c}`, language: "no es cellspeak", rule: "rompe una regla del tablero", repeat: "una repetición", claim: (t) => `dijo que ${t} estaba terminado` },
  ru: { doesNot: (c) => `не выполнено: ${c}`, already: (c) => `уже выполнено: ${c}`, language: "не cellspeak", rule: "нарушает правило доски", repeat: "повтор", claim: (t) => `сообщил(а), что ${t} готово` },
};

/**
 * Why the world refused a message, as the owner reads it: the board records the reason's kind and the check that
 * failed ({from, text, reason: language|rule|repeat|world|needless, check}), so the sentence is built here, in the
 * owner's language, and never copied from the board's English.
 */
export function refusalText(r, lang = "en") {
  const R = REFUSAL[lang] ?? refuse(`no decoder for '${lang}'`);
  const p = parse.safe(r.text);
  const what = p.ok && (p.message.type === "done" || p.message.type === "verified") ? R.claim(p.message.task) : `“${String(r.text).slice(0, 50)}”`;
  const why = r.reason === "world" && r.check ? R.doesNot(checkText(lang, [r.check]))
    : r.reason === "needless" && r.check ? R.already(checkText(lang, [r.check]))
    : R[r.reason] ?? R.language;
  return `${creature(r.from)} ${what} (${why})`;
}

// ---- the digest: what the person in the loop reads -------------------------------------------------------------

const DIGEST = {
  en: { title: "Where things stand", plan: "Agreed", offer: "Contract", done: "Done (checked by the world)", verified: "Verified by the peer", block: "Blocked", need: "Needs", ask: "Asked", refused: "Refused by the world", changed: "Changed", other: (n) => `${n} more message${n === 1 ? "" : "s"} hidden (claims, notes, answers); ask for the full log`, nothing: "Nothing yet.", more: (n) => `and ${n} more line${n === 1 ? "" : "s"} of state; ask for the full digest`, stats: (l, r) => `${l} messages, ${r} refused` },
  es: { title: "Cómo van las cosas", plan: "Acordado", offer: "Contrato", done: "Hecho (comprobado por el mundo)", verified: "Comprobado por el compañero", block: "Bloqueado", need: "Necesita", ask: "Pedido", refused: "Rechazado por el mundo", changed: "Cambiado", other: (n) => `${n} mensaje${n === 1 ? "" : "s"} más oculto${n === 1 ? "" : "s"} (encargos, notas, respuestas); pide el registro completo`, nothing: "Nada todavía.", more: (n) => `y ${n} línea${n === 1 ? "" : "s"} más de estado; pide el resumen completo`, stats: (l, r) => `${l} mensajes, ${r} rechazados` },
  ru: { title: "Как идут дела", plan: "Согласовано", offer: "Контракт", done: "Сделано (проверено миром)", verified: "Проверено напарником", block: "Заблокировано", need: "Нужно", ask: "Просьба", refused: "Отклонено миром", changed: "Изменено", other: (n) => `ещё скрыто сообщений: ${n} (задачи, заметки, ответы); полный журнал по запросу`, nothing: "Пока ничего.", more: (n) => `и ещё строк состояния: ${n}; полная сводка по запросу`, stats: (l, r) => `сообщений: ${l}, отклонено: ${r}` },
};

/**
 * What a person can take in at a glance (issue #47, H1–H4): the state the messages leave, not the messages. Claims the
 * world refused come first (a refused done or verified; a refusal for grammar is a typo, not a lie, and is only
 * counted), then contracts, agreed plans, dones, what the peer verified, and the blocks, needs and asks still open, in
 * at most `limit` lines, whatever the number of messages. A done masks the blocks and needs it resolves; a done or
 * verified by the one asked answers an ask. `landed` are the board's messages ({n, from, message}); `refused` its
 * refusals ({from, text, reason, check}).
 */
export function digest(landed, refused = [], { lang = "en", limit = 8 } = {}) {
  const L = DIGEST[lang] ?? refuse(`no digest for '${lang}'`);
  const msgs = landed.filter((x) => x.message);
  const byN = new Map(msgs.map((x) => [x.n, x]));
  const lines = [];
  const shown = new Set();
  const add = (label, t, n) => { lines.push(`${label}: ${t}`); if (n) shown.add(n); };

  const lies = refused.filter((r) => (r.reason === "world" || r.reason === "rule") && /^\s*(done|verified)\b/.test(r.text));
  if (lies.length) add(L.refused, lies.map((r) => refusalText(r, lang)).join("; "));
  for (const x of msgs) if (x.message.type === "offer") add(L.offer, `${x.message.name}: ${x.message.repo}/${x.message.path} (${creature(x.from)})`, x.n);
  for (const x of msgs) {
    if (x.message.type !== "accept") continue;
    const p = byN.get(x.message.ref);
    if (p) { add(L.plan, `${p.message.topic}: ${termsText(p.message.terms)} (#${p.n}, ${creature(p.from)} ↔ ${creature(x.from)})`, x.n); shown.add(p.n); }
  }
  const of = (type) => msgs.filter((x) => x.message.type === type);
  if (of("done").length) add(L.done, of("done").map((x) => `${x.message.task} (${creature(x.from)})`).join(", "));
  of("done").forEach((x) => shown.add(x.n));
  if (of("verified").length) add(L.verified, of("verified").map((x) => `${x.message.task} (${creature(x.message.of)} ← ${creature(x.from)})`).join(", "));
  of("verified").forEach((x) => shown.add(x.n));

  const later = (x, pred) => msgs.some((y) => y.n > x.n && pred(y.message, y));
  const resolved = (x) => later(x, (m, y) => (m.type === "done" && [x.message.on, x.message.task, x.message.thing].includes(m.task)) || (y.from === x.from && m.type === "claim" && m.task === x.message.task));
  for (const x of of("block").filter((x) => !resolved(x))) add(L.block, `${creature(x.from)}: ${x.message.task} ← ${x.message.on}`, x.n);
  for (const x of of("need").filter((x) => !resolved(x))) add(L.need, `${creature(x.from)}: ${x.message.thing} ← creature-${x.message.from}`, x.n);
  const answered = (x) => later(x, (m, y) => (WHO.exec(y.from)?.[1] ?? y.from) === x.message.of && (m.type === "done" || m.type === "verified"));
  for (const x of of("ask").filter((x) => !answered(x))) add(L.ask, `${creature(x.from)} → creature-${x.message.of}: ${x.message.action}`, x.n);
  const changes = new Map();
  for (const x of of("change")) changes.set(x.message.field, x);
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
