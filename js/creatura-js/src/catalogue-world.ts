import { ordinal } from "./format.js";
import type { ActionOutcome, IWorld, Tool } from "./world.js";

/** What a {@link FaultRule} does when its tool is run with its preconditions met. */
export const FaultKind = {
  /** The tool reports failure and grants nothing. Not a lie; another means usually exists. */
  HonestFailure: "HonestFailure",
  /** The tool reports success and grants nothing: the lie convention. */
  Lie: "Lie",
  /** The tool lands as written and the registry also learns something that changes what a later step needs. */
  ChangedRule: "ChangedRule",
  /** The tool is taken away: hidden from `availableTools`, "tool unavailable" when run. */
  Revoked: "Revoked",
} as const;
export type FaultKind = (typeof FaultKind)[keyof typeof FaultKind];

/**
 * One row of a {@link CatalogueWorld}'s fault table: which tool, what happens instead of the grant, what the registry
 * learns on the way (`adds`), until which fact the row holds (`until`), and whether it fires once or every time. Rows
 * are read in order; the first that applies wins. Barriers are data here, not branches in `act`.
 */
export interface FaultRule {
  readonly tool: string;
  readonly kind: FaultKind;
  /** What the tool says. Undefined for the toy world's silent grant. */
  readonly observation?: string | undefined;
  /** Facts the registry gains when the row fires, whatever the kind. */
  readonly adds: readonly string[];
  /** The row applies while this fact is absent; undefined means always. */
  readonly until?: string | undefined;
  /** Spent after the first time it fires. */
  readonly once: boolean;
}

export const FaultRule = {
  /** Reports failure and grants nothing, every time until `until` holds (or always when undefined). */
  honestFailure(tool: string, observation?: string, until?: string, ...adds: string[]): FaultRule {
    return { tool, kind: FaultKind.HonestFailure, observation, until, adds, once: false };
  },
  /** Reports success and grants nothing, the first time only. */
  lieOnce(tool: string, observation: string | undefined = "reported success", ...adds: string[]): FaultRule {
    return { tool, kind: FaultKind.Lie, observation, adds, once: true };
  },
  /** Reports success and grants nothing, every time. */
  lieAlways(tool: string, observation: string | undefined = "reported success", ...adds: string[]): FaultRule {
    return { tool, kind: FaultKind.Lie, observation, adds, once: false };
  },
  /** Lands as written, says `observation` instead of the usual, and the registry learns `adds`. */
  reveals(tool: string, observation: string | undefined, ...adds: string[]): FaultRule {
    return { tool, kind: FaultKind.ChangedRule, observation, adds, once: false };
  },
  /** The tool no longer exists for this agent. */
  revoked(tool: string): FaultRule {
    return { tool, kind: FaultKind.Revoked, adds: [], once: false };
  },
} as const;

interface Row {
  readonly rule: FaultRule;
  spent: boolean;
}

/**
 * The base of every catalogue-style world: a fact registry (the only truth), a tool catalogue, and a fault table.
 *
 * - `effectsLanded` is "every grant is in the registry", read live, never a before/after diff; a tool that promises
 *   nothing has landed.
 * - The refusal order in `act`: unknown tool → "tool unavailable" → "preconditions unmet: a, b" → the fault table →
 *   the grant. `ticks` moves on every call, refused or not.
 * - The lie convention: a lie reports success and grants nothing.
 * - `shape` rewrites a tool from what the registry has learned; `offered` hides a tool from `availableTools`.
 * - `clone` gives an isolated copy.
 */
export abstract class CatalogueWorld implements IWorld {
  private readonly _catalogue: readonly Tool[];
  private _facts: Set<string>;
  private _rows: Row[];
  private _ticks = 0;

  protected constructor(catalogue: Iterable<Tool>, facts: Iterable<string>, faults?: Iterable<FaultRule>) {
    this._catalogue = [...catalogue];
    this._facts = new Set(facts);
    this._rows = [...(faults ?? [])].map((rule) => ({ rule, spent: false }));
  }

  /** The catalogue as written, before `shape` and `offered`. */
  get catalogue(): readonly Tool[] {
    return this._catalogue;
  }

  /** The fault table, in the order it is read. */
  get faults(): readonly FaultRule[] {
    return this._rows.map((r) => r.rule);
  }

  get facts(): ReadonlySet<string> {
    return this._facts;
  }

  get ticks(): number {
    return this._ticks;
  }

  /** Every tool of the catalogue, shaped by what the registry knows; a revoked tool stays here. */
  get allTools(): readonly Tool[] {
    return this._catalogue.map((t) => this.shape(t));
  }

  /** The tools the agent is offered: the catalogue without what is not `offered` or is revoked, shaped. */
  get availableTools(): readonly Tool[] {
    return this._catalogue.filter((t) => this.offered(t) && !this.isRevoked(t.id)).map((t) => this.shape(t));
  }

  has(fact: string): boolean {
    return this._facts.has(fact);
  }

  /** The tool by id as the agent would see it, from `allTools`; undefined is "unknown tool". */
  tool(id: string): Tool | undefined {
    return this.allTools.find((t) => t.id === id);
  }

  /** The one honest success check: the registry holds everything the tool promised. */
  effectsLanded(tool: Tool): boolean {
    return tool.grants.every((g) => this._facts.has(g));
  }

  /** Adds a row at the end of the fault table. */
  inject(rule: FaultRule): void {
    this._rows.push({ rule, spent: false });
  }

  /** A barrier: the tool no longer exists for this agent. */
  revoke(toolId: string): void {
    if (!this.isRevoked(toolId)) this.inject(FaultRule.revoked(toolId));
  }

  isRevoked(toolId: string): boolean {
    return this._rows.some((r) => r.rule.kind === FaultKind.Revoked && r.rule.tool === toolId);
  }

  act(toolId: string, _payload?: string): ActionOutcome {
    this._ticks++;
    const tool = this.tool(toolId);
    if (tool === undefined) return { tool: toolId, succeeded: false, granted: [], observation: "unknown tool" };
    if (this.isRevoked(toolId)) return { tool: toolId, succeeded: false, granted: [], observation: "tool unavailable" };

    const missing = tool.requires.filter((r) => !this._facts.has(r));
    if (missing.length > 0) {
      return { tool: toolId, succeeded: false, granted: [], observation: `preconditions unmet: ${missing.join(", ")}` };
    }

    const row = this._rows.find((r) => this.applies(r, toolId));
    if (row !== undefined) {
      if (row.rule.once) row.spent = true;
      for (const fact of row.rule.adds) this._facts.add(fact);
      switch (row.rule.kind) {
        case FaultKind.HonestFailure:
          return { tool: toolId, succeeded: false, granted: [], observation: row.rule.observation };
        case FaultKind.Lie:
          return { tool: toolId, succeeded: true, granted: [], observation: row.rule.observation };
        case FaultKind.ChangedRule:
          return this.land(tool, row.rule.observation);
        default:
          break;
      }
    }

    return this.land(tool, this.observation(tool));
  }

  private applies(row: Row, toolId: string): boolean {
    return (
      row.rule.tool === toolId &&
      !row.spent &&
      row.rule.kind !== FaultKind.Revoked &&
      (row.rule.until === undefined || !this._facts.has(row.rule.until))
    );
  }

  /** The grant: the registry gains what the tool promised and the outcome says so. */
  protected land(tool: Tool, observation: string | undefined): ActionOutcome {
    for (const g of tool.grants) this._facts.add(g);
    return { tool: tool.id, succeeded: true, granted: tool.grants, observation };
  }

  /** What a landed act of this tool says. Undefined by default (the toy world says nothing). */
  protected observation(_tool: Tool): string | undefined {
    return undefined;
  }

  /** The tool as the registry's knowledge shapes it. Identity by default. */
  protected shape(tool: Tool): Tool {
    return tool;
  }

  /** Whether the tool is on offer at all: the shortcut switches. Everything by default. */
  protected offered(_tool: Tool): boolean {
    return true;
  }

  /** The registry learns a fact outside any tool: an approval, a condition the world reveals. */
  protected reveal(fact: string): void {
    this._facts.add(fact);
  }

  /** An independent copy: same facts, same catalogue, same rows with their state, same ticks. */
  clone(): this {
    const copy = Object.create(Object.getPrototypeOf(this)) as this;
    Object.assign(copy, this);
    const target = copy as unknown as { _facts: Set<string>; _rows: Row[] };
    target._facts = new Set(this._facts);
    target._rows = this._rows.map((r) => ({ rule: r.rule, spent: r.spent }));
    return copy;
  }
}

/**
 * A tiny deterministic world: a set of facts, a set of tools, and injected faults. Faults are the barriers the
 * designs are evaluated against. Tools are kept in id order, the first of each id winning.
 */
export class World extends CatalogueWorld {
  constructor(tools: Iterable<Tool>, facts: Iterable<string>) {
    const seen = new Set<string>();
    const distinct: Tool[] = [];
    for (const t of tools) {
      if (seen.has(t.id)) continue;
      seen.add(t.id);
      distinct.push(t);
    }
    super(distinct.sort((a, b) => ordinal(a.id, b.id)), facts);
  }

  /**
   * The nastiest barrier: the tool reports success and grants nothing, once. An agent that trusts self-report never
   * notices; one that checks postconditions does.
   */
  makeLiar(toolId: string): void {
    if (!this.faults.some((f) => f.kind === FaultKind.Lie && f.tool === toolId)) this.inject(FaultRule.lieOnce(toolId));
  }
}
