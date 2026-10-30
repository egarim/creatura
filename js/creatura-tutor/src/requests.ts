import { Answer } from "./answer.js";
import { problem, problemSeeds, splitOf, type Problem, type Split } from "./problems.js";
import { mix, rng, type Rng } from "./rng.js";

/** Who is writing: a student who asks for help, one who gives a right attempt, one who gives a wrong attempt. */
export const Voice = { Help: "help", Right: "right", Wrong: "wrong" } as const;
export type Voice = (typeof Voice)[keyof typeof Voice];

/**
 * What the turn is for, as the app knows it: a request for help, or an attempt to check. The app can't know whether an
 * attempt is right before checking it, so right and wrong attempts share one intent and one goal.
 */
export type Intent = "help" | "attempt";

/** One student message about one problem. Its split is its problem's. */
export interface StudentRequest {
  readonly id: string;
  readonly seed: number;
  readonly split: Split;
  readonly voice: Voice;
  readonly intent: Intent;
  readonly problem: Problem;
  readonly message: string;
  /** The answer the message gives, for an attempt. */
  readonly attempt?: Answer | undefined;
}

export const voices: readonly Voice[] = [Voice.Help, Voice.Right, Voice.Wrong];

const helpMessages = [
  "No entiendo este problema, ¿me ayudas?",
  "¿Me das una pista? No sé por dónde empezar.",
  "Estoy atorado con este problema. ¿Qué hago primero?",
  "No sé qué operación tengo que usar.",
  "Ayuda, por favor, no me sale.",
  "¿Cómo se resuelve este? Ya lo leí dos veces y no le entiendo.",
];

// Right and wrong attempts are written from the same templates, so the wording never tells which one it is.
const attemptMessages = [
  "Ya lo hice: me salió {a}. ¿Está bien?",
  "Creo que la respuesta es {a}.",
  "Mi resultado es {a}, ¿lo puedes revisar?",
  "¿Es {a}?",
  "Me dio {a}. ¿Sí es?",
  "Hice la cuenta y obtuve {a}.",
];

/** An attempt as a student writes it: with or without the unit, a fraction in any of its forms. */
function write(answer: Answer, unit: string, r: Rng): string {
  if (answer.kind === "division") {
    const q = answer.quotient, rest = answer.remainder;
    return r.pick([`${q} y sobran ${rest}`, `${q}, sobran ${rest}`, `${q} y quedan ${rest}`, `cociente ${q} y residuo ${rest}`]);
  }

  const form = r.pick(Answer.numberForms(answer.value));
  return r.pick([form, `${form} ${unit}`]);
}

/**
 * The request in one voice about one problem. Its seed is the problem's times three plus the voice, and everything in
 * it is drawn from that seed: the message's template and how the attempt is written.
 */
export function request(p: Problem, voice: Voice): StudentRequest {
  const seed = p.seed * 3 + voices.indexOf(voice);
  const r = rng(mix(seed ^ 0x5eed));
  const attempt = voice === Voice.Right ? p.answer : voice === Voice.Wrong ? p.wrong : undefined;
  const message = attempt === undefined
    ? r.pick(helpMessages)
    : r.pick(attemptMessages).replace("{a}", write(attempt, p.unit, r));
  return {
    id: `${p.id}-${voice}`, seed, split: p.split, voice, intent: voice === Voice.Help ? "help" : "attempt",
    problem: p, message, attempt,
  };
}

/** The requests, all of them or one split's: every problem in the three voices, in seed order. */
export function requests(split?: Split): StudentRequest[] {
  return problemSeeds
    .filter((s) => split === undefined || splitOf(s) === split)
    .flatMap((s) => voices.map((v) => request(problem(s), v)));
}
