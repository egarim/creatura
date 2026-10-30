// .NET's formatting, as the C# side writes traces, notes and prompts. Every expected string here was printed by
// .NET 10 itself (a throwaway console program with TraceText.Culture's settings and Utf8JsonWriter with
// UnsafeRelaxedJsonEscaping), so the port matches the runtime, not its documentation.

import assert from "node:assert/strict";
import { test } from "node:test";

import { formatFixed, formatJsonNumber, formatPercent, jsonString, replaceLineEndings, roundHalfEven, trimNet } from "../src/index.js";

test("P0 and P1: the exact value rounded, ties to even, thousands grouped, the sign kept", () => {
  const cases: [number, string, string][] = [
    [0.125, "12%", "12.5%"],
    [0.375, "38%", "37.5%"],
    [0.625, "62%", "62.5%"],
    [0.875, "88%", "87.5%"],
    [0.005, "1%", "0.5%"],
    [0.015, "1%", "1.5%"],
    [0.025, "3%", "2.5%"],
    [0.145, "14%", "14.5%"],
    [1 / 3, "33%", "33.3%"],
    [2 / 3, "67%", "66.7%"],
    [0.995, "99%", "99.5%"],
    [-0.125, "-12%", "-12.5%"],
    [-0.001, "-0%", "-0.1%"],
    [-0, "-0%", "-0.0%"],
    [0, "0%", "0.0%"],
    [1.5, "150%", "150.0%"],
    [0.0049999999999999, "0%", "0.5%"],
    [12.345, "1,235%", "1,234.5%"],
    [NaN, "NaN", "NaN"],
    [Infinity, "Infinity", "Infinity"],
    [6 / 7, "86%", "85.7%"],
    [5 / 6, "83%", "83.3%"],
  ];
  for (const [value, p0, p1] of cases) {
    assert.equal(formatPercent(value), p0, `P0 of ${value}`);
    assert.equal(formatPercent(value, 1), p1, `P1 of ${value}`);
  }
});

test("F0, F1 and F2: the exact value rounded, ties to even", () => {
  const cases: [number, string, string, string][] = [
    [0.5, "0", "0.5", "0.50"],
    [1.5, "2", "1.5", "1.50"],
    [2.5, "2", "2.5", "2.50"],
    [0.125, "0", "0.1", "0.12"],
    [0.375, "0", "0.4", "0.38"],
    [2.675, "3", "2.7", "2.67"],
    [1.005, "1", "1.0", "1.00"],
    [0.045, "0", "0.0", "0.04"],
    [-0.125, "-0", "-0.1", "-0.12"],
    [-0.001, "-0", "-0.0", "-0.00"],
    [12.345, "12", "12.3", "12.35"],
    [0.65, "1", "0.7", "0.65"],
  ];
  for (const [value, f0, f1, f2] of cases) {
    assert.equal(formatFixed(value, 0), f0, `F0 of ${value}`);
    assert.equal(formatFixed(value, 1), f1, `F1 of ${value}`);
    assert.equal(formatFixed(value, 2), f2, `F2 of ${value}`);
  }
});

test("JSON numbers: shortest round-trip digits, fixed from 1E-04 to 17 integer digits", () => {
  const cases: [number, string][] = [
    [1e17, "1E+17"], [1e18, "1E+18"], [1e19, "1E+19"], [1e20, "1E+20"], [1.5e20, "1.5E+20"], [9.99e20, "9.99E+20"],
    [123456789e12, "1.23456789E+20"], [1e-6, "1E-06"], [0.000099, "9.9E-05"], [0.00001234, "1.234E-05"],
    [0, "0"], [-0, "-0"], [1, "1"], [0.65, "0.65"], [1 / 6, "0.16666666666666666"], [1e-5, "1E-05"], [1e-4, "0.0001"],
    [0.00012345, "0.00012345"], [1e14, "100000000000000"], [1e15, "1000000000000000"], [1e16, "10000000000000000"],
    [123456789012345.6, "123456789012345.6"], [1234567890123456.7, "1234567890123456.8"],
    [1.2345678901234567e16, "12345678901234568"], [1e21, "1E+21"], [1e-7, "1E-07"], [5e-324, "5E-324"],
    [Number.MAX_VALUE, "1.7976931348623157E+308"], [100, "100"], [0.1 + 0.2, "0.30000000000000004"],
    [1.0 - 5.0 / 6, "0.16666666666666663"], [1e300, "1E+300"], [1.5e-300, "1.5E-300"], [-0.65, "-0.65"],
  ];
  for (const [value, expected] of cases) assert.equal(formatJsonNumber(value), expected, `${value}`);
});

test("JSON strings: what the relaxed encoder escapes and what it leaves", () => {
  const codes = [0x22, 0x5c, 0x0a, 0x0d, 0x09, 0x08, 0x0c, 0x00, 0x1b, 0x7f, 0x85, 0xa0, 0xad, 0x2014, 0x2026, 0x2028, 0x2029,
    0x200b, 0xfeff, 0xd83d, 0xde00, 0xd800, 0x78, 0x3c, 0x3e, 0x26, 0x27, 0x2b, 0x60, 0xe9, 0xf1, 0x4e2d, 0xe000, 0xfffe,
    0xffff, 0x2061, 0x20, 0x41, 0x7e];
  const text = String.fromCharCode(...codes);
  // .NET also escaped U+0378 (unassigned in its Unicode tables), which this port does not reproduce; it is left out.
  assert.equal(jsonString(text),
    '"\\"\\\\\\n\\r\\t\\b\\f\\u0000\\u001B\\u007F\\u0085\\u00A0­—…\\u2028\\u2029​\\uFEFF\\uD83D\\uDE00\\uFFFDx<>&\'+`éñ中\\uE000\\uFFFE\\uFFFF⁡ A~"');

  const separators = [0x1680, 0x2000, 0x2005, 0x200a, 0x202f, 0x205f, 0x3000, 0xfdd0, 0xfdef, 0xf8ff];
  for (const code of separators) assert.equal(jsonString(String.fromCharCode(code)), `"\\u${code.toString(16).toUpperCase().padStart(4, "0")}"`);
  const plain = [0xfdf0, 0xfffd, 0xfff9, 0xfffc, 0x0600, 0x180e, 0xf900, 0x00ff, 0x0100, 0x2027, 0x202a, 0x2060, 0x2064, 0x206f, 0xfe0f];
  for (const code of plain) assert.equal(jsonString(String.fromCharCode(code)), `"${String.fromCharCode(code)}"`);
});

test("Math.Round, Trim and ReplaceLineEndings as C# does them", () => {
  assert.deepEqual([0.5, 1.5, 2.5, -0.5, -1.5, 1.9500000000000002].map(roundHalfEven), [0, 2, 2, -0, -2, 2]);
  assert.equal(trimNet("﻿ a \u0085"), "﻿ a");
  assert.equal(replaceLineEndings("a\r\nb\rc\nd\fe\u0085f g h", " "), "a b c d e f g h");
});
