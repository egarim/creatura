/**
 * Exact rational arithmetic over BigInt: no floating point anywhere, so "3/4 de 36" is 27 and "0,1 + 0,2" is "0,3".
 * A value is kept in lowest terms with a positive denominator, so two equal values have equal parts.
 */
export class Rational {
  readonly num: bigint;
  readonly den: bigint;

  private constructor(num: bigint, den: bigint) {
    if (den === 0n) throw new RangeError("a rational with denominator 0");
    if (den < 0n) {
      num = -num;
      den = -den;
    }
    const g = gcd(num < 0n ? -num : num, den);
    this.num = num / g;
    this.den = den / g;
  }

  /** `num / den` from integers. A `number` must be a safe integer; nothing here goes through a float. */
  static of(num: bigint | number, den: bigint | number = 1n): Rational {
    return new Rational(toBigInt(num), toBigInt(den));
  }

  static readonly zero = Rational.of(0n);

  add(other: Rational): Rational {
    return new Rational(this.num * other.den + other.num * this.den, this.den * other.den);
  }

  sub(other: Rational): Rational {
    return new Rational(this.num * other.den - other.num * this.den, this.den * other.den);
  }

  mul(other: Rational): Rational {
    return new Rational(this.num * other.num, this.den * other.den);
  }

  div(other: Rational): Rational {
    return new Rational(this.num * other.den, this.den * other.num);
  }

  equals(other: Rational): boolean {
    return this.num === other.num && this.den === other.den;
  }

  get isInteger(): boolean {
    return this.den === 1n;
  }

  /** "144", or "7/4". */
  toString(): string {
    return this.isInteger ? this.num.toString() : `${this.num}/${this.den}`;
  }

  /** "1 3/4" for 7/4; a proper fraction or an integer as `toString`. Non-negative values only. */
  toMixed(): string {
    const whole = this.num / this.den;
    const rest = this.num % this.den;
    if (whole === 0n || rest === 0n) return this.toString();
    return `${whole} ${rest}/${this.den}`;
  }

  /**
   * "1,75" for 7/4, with the decimal comma used in Spanish; undefined when the decimal does not terminate (the
   * denominator has a prime factor other than 2 and 5). Non-negative values only.
   */
  toDecimal(): string | undefined {
    let d = this.den;
    let twos = 0;
    let fives = 0;
    while (d % 2n === 0n) { d /= 2n; twos++; }
    while (d % 5n === 0n) { d /= 5n; fives++; }
    if (d !== 1n) return undefined;
    const places = Math.max(twos, fives);
    if (places === 0) return this.num.toString();
    const scaled = (this.num * 10n ** BigInt(places)) / this.den;
    const digits = scaled.toString().padStart(places + 1, "0");
    return `${digits.slice(0, -places)},${digits.slice(-places)}`;
  }

  /**
   * One number as a student or a text writes it: "144", "7/4", "1 3/4", "1,75" or "1.75". Undefined for anything
   * else, and for a fraction with denominator 0. Decimals are read digit by digit, never through a float.
   */
  static parse(text: string): Rational | undefined {
    const t = text.trim();
    let m = /^(\d+)\s+(\d+)\/(\d+)$/.exec(t);
    if (m) {
      const den = BigInt(m[3]!);
      const num = BigInt(m[2]!);
      if (den === 0n || num >= den) return undefined;
      return new Rational(BigInt(m[1]!) * den + num, den);
    }
    m = /^(\d+)\/(\d+)$/.exec(t);
    if (m) return BigInt(m[2]!) === 0n ? undefined : new Rational(BigInt(m[1]!), BigInt(m[2]!));
    m = /^(\d+)(?:[.,](\d+))?$/.exec(t);
    if (m) {
      const fraction = m[2] ?? "";
      return new Rational(BigInt(m[1]! + fraction), 10n ** BigInt(fraction.length));
    }
    return undefined;
  }
}

/** A number found in a text, where it was, and its value. */
export interface NumberToken {
  readonly text: string;
  readonly index: number;
  readonly value: Rational;
}

/**
 * Every number in a text, in order: integers, decimals ("1,75"), fractions ("3/4") and mixed numbers ("1 3/4", an
 * integer, one space, a proper fraction). A comma followed by a space is a list, not a decimal ("9, sobran 2" is two
 * numbers). Numbers written as words are not read.
 */
export function numberTokens(text: string): NumberToken[] {
  const raw: { text: string; index: number }[] = [];
  for (const m of text.matchAll(/\d+(?:[.,]\d+)?(?:\/\d+)?/g)) raw.push({ text: m[0], index: m.index ?? 0 });

  const out: NumberToken[] = [];
  for (let i = 0; i < raw.length; i++) {
    const here = raw[i]!;
    const next = raw[i + 1];
    // An integer, one space, a proper fraction: a mixed number.
    if (next !== undefined && /^\d+$/.test(here.text) && next.index === here.index + here.text.length + 1 &&
        text[here.index + here.text.length] === " ") {
      const mixed = Rational.parse(`${here.text} ${next.text}`);
      if (mixed !== undefined) {
        out.push({ text: `${here.text} ${next.text}`, index: here.index, value: mixed });
        i++;
        continue;
      }
    }
    const value = Rational.parse(here.text);
    if (value !== undefined) out.push({ text: here.text, index: here.index, value });
  }

  return out;
}

function gcd(a: bigint, b: bigint): bigint {
  while (b !== 0n) [a, b] = [b, a % b];
  return a === 0n ? 1n : a;
}

function toBigInt(n: bigint | number): bigint {
  if (typeof n === "bigint") return n;
  if (!Number.isSafeInteger(n)) throw new RangeError(`${n} is not a safe integer`);
  return BigInt(n);
}
