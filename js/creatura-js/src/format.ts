/**
 * .NET's number formatting, as Creatura's texts use it, so a trace written here is a trace written there byte for byte.
 *
 * The C# side writes every text in `TraceText.Culture` (invariant numbers, the percent sign against the number: "86%")
 * and every JSON number with `Utf8JsonWriter`. The rules below were read off .NET 10 itself, not from its documentation:
 *
 * - `P0`, `F2`: the exact value of the double is rounded, and an exact tie goes to the even digit
 *   (0.125 → "12%", 0.375 → "38%", 2.5 → "2"); a negative value keeps its sign even when it rounds to zero ("-0%");
 *   percents group thousands with "," ("1,235%"), fixed-point numbers do not.
 * - JSON numbers: the shortest digits that round-trip, written in fixed notation from 1E-04 up to 17 integer digits and
 *   in scientific notation outside that ("1E-05", "1E+17"), exponent with a sign and at least two digits.
 * - `Math.Round`: ties to even.
 */

/** C#'s `Math.Round(x)`: the nearest integer, ties to the even one. */
export function roundHalfEven(x: number): number {
  if (!Number.isFinite(x)) return x;
  const floor = Math.floor(x);
  const diff = x - floor;
  const rounded = diff < 0.5 ? floor : diff > 0.5 ? floor + 1 : floor % 2 === 0 ? floor : floor + 1;
  // IEEE keeps the sign of a negative value that rounds to zero, as C# does (Math.Round(-0.5) is -0).
  return rounded === 0 && (x < 0 || Object.is(x, -0)) ? -0 : rounded;
}

/** C#'s `Math.Clamp`. */
export function clamp(x: number, min: number, max: number): number {
  return x < min ? min : x > max ? max : x;
}

function special(value: number): string | undefined {
  if (Number.isNaN(value)) return "NaN";
  if (value === Infinity) return "Infinity";
  if (value === -Infinity) return "-Infinity";
  return undefined;
}

function negative(value: number): boolean {
  return value < 0 || Object.is(value, -0);
}

/**
 * The exact value of a non-negative double rounded to `decimals` places, ties to even, as "123.45".
 * `toFixed` rounds the exact value but sends a tie up; a tie is told apart by the exact expansion (`toFixed(100)`,
 * enough for any double near a tie at a few places, whose expansion ends long before the hundredth digit).
 */
function fixedHalfEven(abs: number, decimals: number): string {
  if (abs >= 1e21) return abs.toString();
  const rounded = abs.toFixed(decimals);
  const exact = abs.toFixed(100);
  const point = exact.indexOf(".");
  const rest = exact.slice(point + 1 + decimals);
  if (!/^50*$/.test(rest)) return rounded;
  // A tie: the candidates are the truncation and the one above it (what toFixed chose). Keep the even one.
  const truncated = decimals === 0 ? exact.slice(0, point) : exact.slice(0, point + 1 + decimals);
  const last = truncated.charCodeAt(truncated.length - 1) - 48;
  return last % 2 === 0 ? truncated : rounded;
}

function group(integer: string): string {
  let out = "";
  for (let i = 0; i < integer.length; i++) {
    if (i > 0 && (integer.length - i) % 3 === 0) out += ",";
    out += integer[i];
  }
  return out;
}

/** `value.ToString("F{decimals}", TraceText.Culture)`: "0.65", "-0.00". */
export function formatFixed(value: number, decimals: number): string {
  const s = special(value);
  if (s !== undefined) return s;
  const body = fixedHalfEven(Math.abs(value), decimals);
  return (negative(value) ? "-" : "") + body;
}

/** `value.ToString("P{decimals}", TraceText.Culture)`: "86%", "1,235%", "-0%", "12.5%" for P1. */
export function formatPercent(value: number, decimals = 0): string {
  const s = special(value);
  if (s !== undefined) return s;
  // Rounding x·100 to d places is rounding x to d + 2 places and moving the point: no multiplication, no new error.
  const raw = fixedHalfEven(Math.abs(value), decimals + 2);
  const point = raw.indexOf(".");
  const digits = point < 0 ? raw + "00" : raw.slice(0, point) + raw.slice(point + 1);
  const integerLength = (point < 0 ? raw.length : point) + 2;
  const integer = digits.slice(0, integerLength).replace(/^0+(?=\d)/, "");
  const fraction = digits.slice(integerLength);
  return (negative(value) ? "-" : "") + group(integer) + (fraction.length > 0 ? "." + fraction : "") + "%";
}

/** The shortest round-trip digits of a positive finite double, and the decimal exponent of the first one. */
function shortest(abs: number): { digits: string; exponent: number } {
  const text = abs.toString();
  const e = text.indexOf("e");
  const mantissa = e < 0 ? text : text.slice(0, e);
  const power = e < 0 ? 0 : parseInt(text.slice(e + 1), 10);
  const point = mantissa.indexOf(".");
  const integer = point < 0 ? mantissa : mantissa.slice(0, point);
  const fraction = point < 0 ? "" : mantissa.slice(point + 1);
  const all = integer + fraction;
  const lead = all.search(/[1-9]/);
  const digits = all.slice(lead).replace(/0+$/, "");
  return { digits, exponent: integer.length - 1 - lead + power };
}

/** A double as `Utf8JsonWriter.WriteNumber` writes it: "0.65", "1", "0.16666666666666666", "1E-05". */
export function formatJsonNumber(value: number): string {
  const s = special(value);
  if (s !== undefined) return s;
  if (value === 0) return Object.is(value, -0) ? "-0" : "0";
  const sign = value < 0 ? "-" : "";
  const { digits, exponent } = shortest(Math.abs(value));
  if (exponent >= 17 || exponent <= -5) {
    const e = Math.abs(exponent).toString().padStart(2, "0");
    return sign + digits[0] + (digits.length > 1 ? "." + digits.slice(1) : "") + "E" + (exponent < 0 ? "-" : "+") + e;
  }

  if (exponent < 0) return sign + "0." + "0".repeat(-exponent - 1) + digits;
  const integer = digits.length > exponent + 1 ? digits.slice(0, exponent + 1) : digits.padEnd(exponent + 1, "0");
  const fraction = digits.slice(exponent + 1);
  return sign + integer + (fraction.length > 0 ? "." + fraction : "");
}

// The characters C#'s char.IsWhiteSpace accepts: JavaScript's trim also takes U+FEFF and leaves U+0085.
const whiteSpace = "\t\n\u000b\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000";

/** C#'s `string.Trim()`. */
export function trimNet(text: string): string {
  let start = 0;
  let end = text.length;
  while (start < end && whiteSpace.includes(text.charAt(start))) start++;
  while (end > start && whiteSpace.includes(text.charAt(end - 1))) end--;
  return text.slice(start, end);
}

/** C#'s `ReplaceLineEndings(replacement)`: CR LF, CR, LF, FF, NEL, LS and PS each become the replacement. */
export function replaceLineEndings(text: string, replacement: string): string {
  return text.replace(/\r\n|[\r\n\f\u0085\u2028\u2029]/g, replacement);
}

/**
 * C#'s `text.Contains(value, StringComparison.OrdinalIgnoreCase)` for an ASCII `value`. Only ASCII
 * letters are folded: the two non-ASCII letters .NET folds onto ASCII ones (ı to I, ſ to S) are not, which no needle
 * here ("NO PATH") can meet.
 */
export function containsIgnoreCase(text: string, value: string): boolean {
  const upper = (s: string) => s.replace(/[a-z]/g, (c) => String.fromCharCode(c.charCodeAt(0) - 32));
  return upper(text).includes(upper(value));
}

/** C#'s `StringComparer.Ordinal`: UTF-16 code units, as JavaScript's own comparison. */
export function ordinal(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
