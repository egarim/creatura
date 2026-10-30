import { numberTokens, Rational } from "./rational.js";

/**
 * A problem's answer: one number (an integer or a fraction, in the problem's unit), or a division with remainder
 * (quotient and remainder, both integers). Equality is exact.
 */
export type Answer =
  | { readonly kind: "number"; readonly value: Rational }
  | { readonly kind: "division"; readonly quotient: bigint; readonly remainder: bigint };

export const Answer = {
  number(value: Rational | bigint | number): Answer {
    return { kind: "number", value: value instanceof Rational ? value : Rational.of(value) };
  },

  division(quotient: bigint | number, remainder: bigint | number): Answer {
    return { kind: "division", quotient: BigInt(quotient), remainder: BigInt(remainder) };
  },

  equals(a: Answer, b: Answer): boolean {
    if (a.kind === "number") return b.kind === "number" && a.value.equals(b.value);
    return b.kind === "division" && a.quotient === b.quotient && a.remainder === b.remainder;
  },

  /**
   * The numbers that would give the answer away, as values: the number itself, or the quotient and the remainder. A
   * text that contains any of them, in any form ("7/4", "1 3/4", "1,75"), reveals the answer.
   */
  secrets(answer: Answer): Rational[] {
    return answer.kind === "number"
      ? [answer.value]
      : [Rational.of(answer.quotient), Rational.of(answer.remainder)];
  },

  /**
   * An attempt read the way the problem asks for it. A number: the first number in the text. A division: the first
   * two integers, quotient then remainder; one integer alone is a quotient with nothing left over. Undefined when the
   * text has no number of the right kind.
   */
  parse(kind: Answer["kind"], text: string): Answer | undefined {
    const tokens = numberTokens(text);
    if (kind === "number") {
      const first = tokens[0];
      return first === undefined ? undefined : Answer.number(first.value);
    }

    const integers = tokens.filter((t) => t.value.isInteger);
    if (integers.length === 0 || integers.length !== tokens.length) return undefined;
    return Answer.division(integers[0]!.value.num, integers[1]?.value.num ?? 0n);
  },

  /** The ways a student may write the answer's number: "144"; "7/4", "1 3/4", "1,75" for a fraction. */
  numberForms(value: Rational): string[] {
    if (value.isInteger) return [value.toString()];
    const forms = [value.toString()];
    if (value.toMixed() !== value.toString()) forms.push(value.toMixed());
    const decimal = value.toDecimal();
    if (decimal !== undefined) forms.push(decimal);
    return forms;
  },

  /** The answer said plainly, as a leaky hint would say it: "144 lápices", "9 caramelos y sobran 2". */
  say(answer: Answer, unit: string): string {
    return answer.kind === "number"
      ? `${answer.value.toMixed()} ${unit}`
      : `${answer.quotient} y sobran ${answer.remainder}`;
  },
} as const;
