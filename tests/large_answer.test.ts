// Guards src/tools/largeAnswer.ts (fitAnswer): an answer over MAX_INLINE_BYTES is cut to its shape and says what to do,
// so a huge drawing query cannot flood the AI's context. Pure unit test.

import { describe, expect, it } from "vitest";
import { fitAnswer, MAX_INLINE_BYTES } from "../src/tools/largeAnswer.js";

describe("fitAnswer", () => {
  it("leaves an answer that fits alone", () => {
    const result = { name: "Elm Street", points: [1, 2, 3] };
    const serialized = JSON.stringify(result);
    expect(fitAnswer(result, serialized)).toEqual({ result, text: serialized });
  });

  it("cuts a huge answer to its shape, far under the limit, and keeps the plain values", () => {
    const entities = Array.from({ length: 60000 }, (_, index) => ({ index, startStation: index * 10.5, type: "tangent", grade: 0.012345 }));
    const result = { name: "Elm Street EG", entityCount: entities.length, entities };
    const serialized = JSON.stringify(result, null, 2);
    expect(Buffer.byteLength(serialized)).toBeGreaterThan(MAX_INLINE_BYTES);

    const fitted = fitAnswer(result, serialized, "civil3d://reports/1");
    expect(Buffer.byteLength(fitted.text)).toBeLessThan(10000);
    expect(fitted.result).toMatchObject({
      truncated: true,
      resourceUri: "civil3d://reports/1",
      answer: { name: "Elm Street EG", entityCount: 60000, entities: { itemCount: 60000 } },
    });
    expect((fitted.result as { answer: { entities: { firstItems: unknown[] } } }).answer.entities.firstItems).toHaveLength(3);
    expect(fitted.text).toContain("civil3d://reports/1");
  });

  it("says what to do when the whole answer couldn't be kept", () => {
    const result = { rows: Array.from({ length: 80000 }, () => "x".repeat(100)) };
    const fitted = fitAnswer(result, JSON.stringify(result));
    expect(fitted.text).toContain("ask for less");
    expect(fitted.text).not.toContain("read the whole answer");
  });

  it("cuts a very long string", () => {
    const result = { report: "line\n".repeat(600000) };
    const fitted = fitAnswer(result, JSON.stringify(result));
    expect(Buffer.byteLength(fitted.text)).toBeLessThan(10000);
    expect(fitted.text).toContain("3000000 characters");
  });
});
