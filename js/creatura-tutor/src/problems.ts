import { Answer } from "./answer.js";
import { numberTokens, Rational } from "./rational.js";
import { mix, rng, type Rng } from "./rng.js";

/** Which part of the data a seed belongs to. Decided from the seed alone, before the problem is generated. */
export const Split = { Train: "train", Valid: "valid", Test: "test" } as const;
export type Split = (typeof Split)[keyof typeof Split];

export type Topic = "multiplication" | "division" | "fractions" | "units";

/** A 4th-grade word problem in Spanish, with its exact answer, a plausible wrong answer and its hints in order. */
export interface Problem {
  readonly id: string;
  readonly seed: number;
  readonly split: Split;
  readonly template: string;
  readonly topic: Topic;
  readonly statement: string;
  /** The unit the answer is counted in ("lápices", "metros"). */
  readonly unit: string;
  readonly answer: Answer;
  /** A mistake a student plausibly makes (adding instead of multiplying, dropping the remainder, the wrong factor). */
  readonly wrong: Answer;
  /** The honest hints, gentlest first. None of them contains the answer's numbers. */
  readonly hints: readonly string[];
}

/** The problem seeds: 0 to 99. */
export const problemSeeds: readonly number[] = Array.from({ length: 100 }, (_, i) => i);

/**
 * The split of a seed, by a hash of the seed alone: 0–6 of ten is train, 7 valid, 8–9 test. Adding seeds never moves an
 * existing one, and the test split is never trained on.
 */
export function splitOf(seed: number): Split {
  const bucket = mix(seed ^ 0x7e57) % 10;
  return bucket < 7 ? Split.Train : bucket < 8 ? Split.Valid : Split.Test;
}

interface Draft {
  readonly statement: string;
  readonly answer: Answer;
  readonly wrong: Answer;
  readonly hints: readonly string[];
}

interface Template {
  readonly id: string;
  readonly topic: Topic;
  readonly unit: string;
  make(r: Rng): Draft;
}

const names = ["Ana", "Luis", "Sofía", "Mateo", "Valentina", "Diego", "Camila", "Javier", "Lucía", "Tomás"];
const n = (v: number) => Answer.number(v);

/** The templates, in the order seeds cycle through them (seed mod 12). */
export const templates: readonly Template[] = [
  {
    id: "mult-cajas", topic: "multiplication", unit: "lápices",
    make(r) {
      const a = r.int(12, 48), b = r.int(3, 9), name = r.pick(names);
      return {
        statement: `${name} compra ${b} cajas de lápices. En cada caja hay ${a} lápices. ¿Cuántos lápices compra en total?`,
        answer: n(a * b), wrong: n(a + b),
        hints: [
          `Hay ${b} cajas y todas tienen la misma cantidad de lápices. ¿Qué operación sirve para juntar grupos iguales?`,
          `Multiplica ${a} × ${b}. Si te ayuda, descompón ${a} en decenas y unidades y multiplica cada parte por ${b}.`,
        ],
      };
    },
  },
  {
    id: "mult-autobus", topic: "multiplication", unit: "pasajeros",
    make(r) {
      const a = r.int(25, 60), b = r.int(4, 9);
      return {
        statement: `Un autobús lleva ${a} pasajeros en cada viaje. Hoy hizo ${b} viajes llenos. ¿Cuántos pasajeros transportó?`,
        answer: n(a * b), wrong: n(a * (b - 1)),
        hints: [
          `Cada viaje es un grupo de ${a} pasajeros. ¿Cuántos grupos hay?`,
          `Calcula ${a} × ${b}, y revisa que contaste todos los viajes.`,
        ],
      };
    },
  },
  {
    id: "mult-album", topic: "multiplication", unit: "estampas",
    make(r) {
      const a = r.int(6, 12), b = r.int(12, 30), name = r.pick(names);
      return {
        statement: `El álbum de ${name} tiene ${a} páginas y en cada página caben ${b} estampas. Si llenó todas las páginas, ¿cuántas estampas pegó?`,
        answer: n(a * b), wrong: n((a + 1) * b),
        hints: [
          `Todas las páginas tienen la misma cantidad de estampas. Piensa en ${a} grupos iguales.`,
          `Multiplica las páginas por las estampas de cada página: ${a} × ${b}.`,
        ],
      };
    },
  },
  {
    id: "div-caramelos", topic: "division", unit: "caramelos",
    make(r) {
      const d = r.int(3, 9), q = r.int(6, 15), rest = r.int(1, d - 1), total = q * d + rest, name = r.pick(names);
      return {
        statement: `${name} tiene ${total} caramelos y los reparte en partes iguales entre ${d} amigos. ¿Cuántos caramelos recibe cada amigo y cuántos sobran?`,
        answer: Answer.division(q, rest), wrong: Answer.division(q - 1, rest + d),
        hints: [
          `Repartir en partes iguales es dividir: busca cuántas veces cabe ${d} en ${total}.`,
          `Lo que sobra tiene que ser menor que ${d}. Si sobran ${d} o más, todavía alcanza para dar uno más a cada amigo.`,
        ],
      };
    },
  },
  {
    id: "div-pasteles", topic: "division", unit: "pasteles",
    make(r) {
      const d = r.int(3, 6), q = r.int(4, 12), rest = r.int(1, d - 1), total = q * d + rest;
      return {
        statement: `Para cada pastel se necesitan ${d} huevos. En la cocina hay ${total} huevos. ¿Cuántos pasteles completos se pueden hacer y cuántos huevos sobran?`,
        answer: Answer.division(q, rest), wrong: Answer.division(q + 1, 0),
        hints: [
          `Cada pastel usa ${d} huevos. Divide los ${total} huevos entre ${d}.`,
          `Solo cuentan los pasteles completos: si al final no alcanzan ${d} huevos, esos huevos sobran.`,
        ],
      };
    },
  },
  {
    id: "div-camionetas", topic: "division", unit: "camionetas",
    make(r) {
      const d = r.int(6, 12), q = r.int(3, 9), rest = r.int(1, d - 1), total = q * d + rest;
      return {
        statement: `Van ${total} estudiantes de excursión en camionetas de ${d} asientos. ¿Cuántas camionetas se llenan completas y cuántos estudiantes quedan sin lugar?`,
        answer: Answer.division(q, rest), wrong: Answer.division(q, d - rest),
        hints: [
          `Cada camioneta llena lleva ${d} estudiantes. Divide ${total} entre ${d}.`,
          `Los que quedan sin lugar son el residuo de la división, no los asientos vacíos.`,
        ],
      };
    },
  },
  {
    id: "frac-gallinas", topic: "fractions", unit: "gallinas",
    make(r) {
      const b = r.pick([3, 4, 5, 6, 8]), a = r.int(2, b - 1), k = r.int(3, 12), total = b * k;
      return {
        statement: `En una granja hay ${total} gallinas. De ellas, ${a}/${b} son blancas. ¿Cuántas gallinas blancas hay?`,
        answer: n(a * k), wrong: n(k),
        hints: [
          `Primero busca cuánto es 1/${b} de ${total}: reparte las gallinas en ${b} grupos iguales.`,
          `Ya sabes cuántas hay en un grupo; las blancas son ${a} de esos grupos.`,
        ],
      };
    },
  },
  {
    id: "frac-cinta", topic: "fractions", unit: "metros",
    make(r) {
      const d = r.pick([3, 4, 5, 8]), total = r.int(d + 1, 3 * d);
      return {
        statement: `Una cinta de ${total} metros se corta en ${d} trozos iguales. ¿Cuántos metros mide cada trozo?`,
        answer: Answer.number(Rational.of(total, d)), wrong: Answer.number(Rational.of(d, total)),
        hints: [
          `Cortar ${total} metros en ${d} trozos iguales es dividir ${total} entre ${d}.`,
          `La división no es exacta, así que cada trozo mide una fracción de metros: puedes escribirla como fracción o como número mixto.`,
        ],
      };
    },
  },
  {
    id: "frac-canicas", topic: "fractions", unit: "canicas",
    make(r) {
      const b = r.pick([2, 3, 4, 5]), a = r.int(1, b - 1), k = r.int(4, 12), total = b * k, name = r.pick(names);
      return {
        statement: `${name} tenía ${total} canicas y le regaló ${a}/${b} de ellas a su hermano. ¿Cuántas canicas le quedan?`,
        answer: n(total - a * k), wrong: n(a * k),
        hints: [
          `Primero calcula cuántas canicas regaló: ${a}/${b} de ${total}.`,
          `Le quedan las que no regaló: a las ${total} canicas réstale las que regaló.`,
        ],
      };
    },
  },
  {
    id: "unid-km", topic: "units", unit: "metros",
    make(r) {
      const k = r.int(1, 9), m = r.int(105, 950);
      return {
        statement: `Un sendero mide ${k} km y ${m} m. ¿Cuántos metros mide en total?`,
        answer: n(1000 * k + m), wrong: n(100 * k + m),
        hints: [
          `Recuerda: 1 km = 1000 m.`,
          `Convierte los ${k} km a metros y después súmales los ${m} m.`,
        ],
      };
    },
  },
  {
    id: "unid-horas", topic: "units", unit: "minutos",
    make(r) {
      const h = r.int(1, 4), mi = r.int(5, 55);
      return {
        statement: `Una película dura ${h} horas y ${mi} minutos. ¿Cuántos minutos dura en total?`,
        answer: n(60 * h + mi), wrong: n(100 * h + mi),
        hints: [
          `Recuerda: 1 hora = 60 minutos, no 100.`,
          `Convierte las ${h} horas a minutos y después súmales los ${mi} minutos.`,
        ],
      };
    },
  },
  {
    id: "unid-kg", topic: "units", unit: "gramos",
    make(r) {
      const k = r.int(1, 8), g = r.int(50, 950);
      return {
        statement: `Un paquete pesa ${k} kg y ${g} g. ¿Cuántos gramos pesa en total?`,
        answer: n(1000 * k + g), wrong: n(100 * k + g),
        hints: [
          `Recuerda: 1 kg = 1000 g.`,
          `Convierte los ${k} kg a gramos y después súmales los ${g} g.`,
        ],
      };
    },
  },
];

/** True when some number in the text has the value of one of the answer's secrets. */
export function mentions(text: string, answer: Answer): boolean {
  const secrets = Answer.secrets(answer);
  return numberTokens(text).some((t) => secrets.some((s) => s.equals(t.value)));
}

/**
 * The problem for a seed. Its split is decided first, from the seed alone. Then the template (seed mod 12) draws its
 * numbers from the seed's generator, again until the draft is sound: the wrong answer differs from the right one, and
 * no number of the answer appears in the statement or in an honest hint (so a text that shows one gives it away).
 */
export function problem(seed: number): Problem {
  const split = splitOf(seed);
  const template = templates[seed % templates.length]!;
  const r = rng(seed);
  for (let attempt = 0; attempt < 100; attempt++) {
    const draft = template.make(r);
    if (Answer.equals(draft.answer, draft.wrong)) continue;
    if (mentions(draft.statement, draft.answer) || draft.hints.some((h) => mentions(h, draft.answer))) continue;
    return {
      id: `p${String(seed).padStart(3, "0")}`, seed, split, template: template.id, topic: template.topic,
      statement: draft.statement, unit: template.unit, answer: draft.answer, wrong: draft.wrong, hints: draft.hints,
    };
  }

  throw new Error(`seed ${seed}: template ${template.id} drew no sound problem in 100 tries`);
}

/** The problems, all of them or one split's, in seed order. */
export function problems(split?: Split): Problem[] {
  return problemSeeds.filter((s) => split === undefined || splitOf(s) === split).map(problem);
}
