import { formatJsonNumber } from "./format.js";
import type { ActionVerdict, HandOffReason, TraceKind } from "./types.js";

/** One line of the run, with the numbers that make the architecture visible. */
export interface TraceEvent {
  readonly tick: number;
  readonly level: string;
  readonly setPoint: string;
  readonly stress: number;
  readonly freedom: number;
  readonly action: string;
  readonly kind: TraceKind;
  readonly detail: string;
  /** The typed verdict behind a cell row, when the writer set one. */
  readonly verdict?: ActionVerdict | undefined;
  /** The typed hand-off reason behind an escalated row, when the writer set one. */
  readonly reason?: HandOffReason | undefined;
}

/** The result of one agent against one world. */
export interface AgentRun {
  readonly agent: string;
  readonly goalReached: boolean;
  readonly ticks: number;
  readonly trace: readonly TraceEvent[];
  readonly notes: readonly string[];
}

/** Two rows are the same row: C#'s record equality over every field. */
export function sameEvent(a: TraceEvent | undefined, b: TraceEvent | undefined): boolean {
  if (a === undefined || b === undefined) return a === b;
  return (
    a.tick === b.tick &&
    a.level === b.level &&
    a.setPoint === b.setPoint &&
    Object.is(a.stress, b.stress) &&
    Object.is(a.freedom, b.freedom) &&
    a.action === b.action &&
    a.kind === b.kind &&
    a.detail === b.detail &&
    a.verdict === b.verdict &&
    a.reason === b.reason
  );
}

const hex = (code: number) => "\\u" + code.toString(16).toUpperCase().padStart(4, "0");

/**
 * Whether `Utf8JsonWriter` with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` writes this UTF-16 unit as `\uXXXX`.
 * Read off .NET 10: the C0 and C1 controls and DEL, every space separator but the space itself, the line and paragraph
 * separators, the byte-order mark, the private-use area and the BMP noncharacters. Surrogates are handled by the caller.
 * Not reproduced: .NET also escapes code points unassigned in its Unicode version, which needs its tables; no text the
 * control layer writes holds one.
 */
function escaped(code: number): boolean {
  return (
    code < 0x20 ||
    (code >= 0x7f && code <= 0x9f) ||
    code === 0xa0 ||
    code === 0x1680 ||
    (code >= 0x2000 && code <= 0x200a) ||
    code === 0x2028 ||
    code === 0x2029 ||
    code === 0x202f ||
    code === 0x205f ||
    code === 0x3000 ||
    (code >= 0xe000 && code <= 0xf8ff) ||
    (code >= 0xfdd0 && code <= 0xfdef) ||
    code === 0xfeff ||
    code === 0xfffe ||
    code === 0xffff
  );
}

/** A string as the C# writer quotes it: `"` and `\` with a backslash, the short escapes, the rest as `\uXXXX`. */
export function jsonString(text: string): string {
  let out = '"';
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = i + 1 < text.length ? text.charCodeAt(i + 1) : 0;
      if (next >= 0xdc00 && next <= 0xdfff) {
        out += hex(code) + hex(next);
        i++;
      } else {
        out += hex(0xfffd);
      }
      continue;
    }

    if (code >= 0xdc00 && code <= 0xdfff) {
      out += hex(0xfffd);
      continue;
    }

    switch (code) {
      case 0x22: out += '\\"'; break;
      case 0x5c: out += "\\\\"; break;
      case 0x08: out += "\\b"; break;
      case 0x09: out += "\\t"; break;
      case 0x0a: out += "\\n"; break;
      case 0x0c: out += "\\f"; break;
      case 0x0d: out += "\\r"; break;
      default: out += escaped(code) ? hex(code) : text.charAt(i);
    }
  }

  return out + '"';
}

/**
 * The run as JSON lines, exactly as C#'s `AgentRun.WriteJsonl` writes it: the header first (agent, goal, ticks), then
 * every trace row in order, then every note in order, each line ended by "\n". The golden traces are this text.
 */
export function writeJsonl(run: AgentRun): string {
  let out = `{"agent":${jsonString(run.agent)},"goalReached":${run.goalReached},"ticks":${run.ticks}}\n`;
  for (const e of run.trace) {
    out +=
      `{"tick":${e.tick},"level":${jsonString(e.level)},"setPoint":${jsonString(e.setPoint)},` +
      `"stress":${formatJsonNumber(e.stress)},"freedom":${formatJsonNumber(e.freedom)},` +
      `"action":${jsonString(e.action)},"kind":${jsonString(e.kind)},"detail":${jsonString(e.detail)}` +
      (e.verdict !== undefined ? `,"verdict":${jsonString(e.verdict)}` : "") +
      (e.reason !== undefined ? `,"reason":${jsonString(e.reason)}` : "") +
      "}\n";
  }

  for (const note of run.notes) out += `{"note":${jsonString(note)}}\n`;
  return out;
}

/** Read a run written by {@link writeJsonl}. Rows and notes come back in file order. */
export function readJsonl(text: string): AgentRun {
  let agent: string | undefined;
  let goalReached = false;
  let ticks = 0;
  const trace: TraceEvent[] = [];
  const notes: string[] = [];
  const lines = text.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!;
    if (line.trim() === "") continue;
    const o = JSON.parse(line) as Record<string, unknown>;
    if ("note" in o) {
      notes.push(String(o["note"] ?? ""));
    } else if ("tick" in o) {
      trace.push({
        tick: o["tick"] as number,
        level: String(o["level"] ?? ""),
        setPoint: String(o["setPoint"] ?? ""),
        stress: o["stress"] as number,
        freedom: o["freedom"] as number,
        action: String(o["action"] ?? ""),
        kind: o["kind"] as TraceKind,
        detail: String(o["detail"] ?? ""),
        verdict: o["verdict"] as ActionVerdict | undefined,
        reason: o["reason"] as HandOffReason | undefined,
      });
    } else if ("agent" in o && agent === undefined) {
      agent = String(o["agent"] ?? "");
      goalReached = o["goalReached"] === true;
      ticks = o["ticks"] as number;
    } else {
      throw new Error(`line ${i + 1}: not a run header, a trace row or a note`);
    }
  }

  if (agent === undefined) throw new Error("no run header");
  return { agent, goalReached, ticks, trace, notes };
}
