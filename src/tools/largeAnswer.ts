/**
 * Keeps one huge answer from ending the session. An MCP client drops the whole connection when a single message passes
 * its limit (10 MiB in the SDK), and a message carries the answer twice, as text and as structured content, so an 8 MiB
 * answer from Civil 3D was enough. Past MAX_INLINE_BYTES the caller gets the answer's shape instead: its plain values,
 * how long its lists are and their first items, and where the whole answer is kept when it could be.
 * Called by domainRuntime.ts for every tool result, after the result was offered to reportResourceStore.ts.
 */

/** Well under the client's limit, with the answer sent twice. */
export const MAX_INLINE_BYTES = 2 * 1024 * 1024;
const LONGEST_STRING = 2000;
const ITEMS_SHOWN = 3;
const DEEPEST_LEVEL = 3;

/** An answer with its lists cut to their first items and its long strings cut short. */
function shape(value: unknown, depth = 0): unknown {
  if (typeof value === "string") {
    return value.length > LONGEST_STRING ? `${value.slice(0, LONGEST_STRING)}... (${value.length} characters)` : value;
  }
  if (Array.isArray(value)) {
    return { itemCount: value.length, firstItems: value.slice(0, ITEMS_SHOWN).map((item) => shape(item, DEEPEST_LEVEL)) };
  }
  if (value && typeof value === "object") {
    if (depth >= DEEPEST_LEVEL) return "(left out)";
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, shape(item, depth + 1)]));
  }
  return value;
}

/**
 * What to send for an answer: itself when it fits, otherwise its shape and a note saying how to get the rest.
 * `result` is what structured content carries, `text` the same as text.
 */
export function fitAnswer(result: unknown, serialized: string, resourceUri?: string): { result: unknown; text: string } {
  const bytes = Buffer.byteLength(serialized, "utf8");
  if (bytes <= MAX_INLINE_BYTES) {
    return { result, text: serialized };
  }

  const megabytes = (bytes / (1024 * 1024)).toFixed(1);
  const shortened = {
    truncated: true,
    answerBytes: bytes,
    note: `This answer is ${megabytes} MB, too big to send whole. Its lists are cut to their first items: ask for less `
      + `(a limit, a wider interval, one object)${resourceUri ? `, or read the whole answer from ${resourceUri}` : ""}.`,
    ...(resourceUri ? { resourceUri } : {}),
    answer: shape(result),
  };
  return { result: shortened, text: JSON.stringify(shortened, null, 2) };
}
