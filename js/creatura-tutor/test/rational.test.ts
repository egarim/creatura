// Exact arithmetic and how numbers are read out of text.

import assert from "node:assert/strict";
import { test } from "node:test";

import { Answer, Rational, numberTokens } from "../src/index.js";

test("a rational is kept in lowest terms with a positive denominator", () => {
  const r = Rational.of(6, -8);
  assert.deepEqual([r.num, r.den], [-3n, 4n]);
  assert.ok(Rational.of(27).equals(Rational.of(3, 4).mul(Rational.of(36))));
  assert.ok(Rational.of(0, 5).equals(Rational.zero));
  assert.throws(() => Rational.of(1, 0), RangeError);
  assert.throws(() => Rational.of(0.5), RangeError);
});

test("arithmetic is exact: 0,1 + 0,2 is 0,3", () => {
  const sum = Rational.parse("0,1")!.add(Rational.parse("0,2")!);
  assert.ok(sum.equals(Rational.parse("0,3")!));
  assert.equal(sum.toDecimal(), "0,3");
  assert.ok(Rational.of(7, 4).sub(Rational.of(3, 4)).equals(Rational.of(1)));
  assert.ok(Rational.of(7, 4).div(Rational.of(7, 2)).equals(Rational.of(1, 2)));
});

test("one value in every form a student writes it", () => {
  const value = Rational.of(7, 4);
  for (const form of ["7/4", "1 3/4", "1,75", "1.75", "14/8"]) assert.ok(Rational.parse(form)!.equals(value), form);
  assert.equal(value.toString(), "7/4");
  assert.equal(value.toMixed(), "1 3/4");
  assert.equal(value.toDecimal(), "1,75");
  assert.equal(Rational.of(5, 3).toDecimal(), undefined);
  assert.equal(Rational.of(1, 8).toDecimal(), "0,125");
  assert.deepEqual(Answer.numberForms(Rational.of(7, 4)), ["7/4", "1 3/4", "1,75"]);
  assert.deepEqual(Answer.numberForms(Rational.of(144)), ["144"]);
  for (const bad of ["", "1 5/4", "3/0", "a", "1,"]) assert.equal(Rational.parse(bad), undefined, bad);
});

test("numbers in a text: mixed numbers, fractions, decimals, and a comma before a space is a list", () => {
  const texts = (t: string) => numberTokens(t).map((n) => n.text);
  assert.deepEqual(texts("Me dio 1 3/4 metros."), ["1 3/4"]);
  assert.deepEqual(texts("9, sobran 2"), ["9", "2"]);
  assert.deepEqual(texts("es 1,75 o 7/4"), ["1,75", "7/4"]);
  assert.deepEqual(texts("De ellas, 3/4 son blancas; hay 36."), ["3/4", "36"]);
  assert.deepEqual(texts("3 7/4"), ["3", "7/4"]);
  assert.deepEqual(texts("doce y media"), []);
});

test("an attempt is read the way its problem asks", () => {
  assert.ok(Answer.equals(Answer.parse("number", "Creo que son 1 3/4 metros")!, Answer.number(Rational.of(7, 4))));
  assert.ok(Answer.equals(Answer.parse("division", "cociente 9 y residuo 2")!, Answer.division(9, 2)));
  assert.ok(Answer.equals(Answer.parse("division", "9")!, Answer.division(9, 0)));
  assert.equal(Answer.parse("division", "9 y 1/2"), undefined);
  assert.equal(Answer.parse("number", "no sé"), undefined);
  assert.deepEqual(Answer.secrets(Answer.division(9, 2)).map(String), ["9", "2"]);
});
