/**
 * Seeded integers, no floating point: the same seed gives the same problems and the same messages on every machine.
 * `next` is mulberry32; `mix` is a 32-bit avalanche hash (the murmur3 finaliser), used to derive one seed from another.
 */
export interface Rng {
  /** The next unsigned 32-bit integer. */
  next(): number;
  /** An integer in [lo, hi], both included. */
  int(lo: number, hi: number): number;
  /** One element of a non-empty list. */
  pick<T>(items: readonly T[]): T;
}

export function mix(seed: number): number {
  let h = seed >>> 0;
  h = Math.imul(h ^ (h >>> 16), 0x85ebca6b);
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35);
  return (h ^ (h >>> 16)) >>> 0;
}

export function rng(seed: number): Rng {
  let a = mix(seed);
  const next = (): number => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return (t ^ (t >>> 14)) >>> 0;
  };
  return {
    next,
    int(lo: number, hi: number): number {
      if (hi < lo) throw new RangeError(`int(${lo}, ${hi})`);
      return lo + (next() % (hi - lo + 1));
    },
    pick<T>(items: readonly T[]): T {
      if (items.length === 0) throw new RangeError("pick from an empty list");
      return items[next() % items.length]!;
    },
  };
}
