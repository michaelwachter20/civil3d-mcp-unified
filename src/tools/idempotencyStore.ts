/**
 * In-memory results for calls that carry an `idempotencyKey`, so a client that retries (after a timeout, a dropped
 * connection) gets the first call's result instead of running it twice. domainRuntime.ts uses the one shared
 * `idempotencyStore`, only for `safeForRetry` actions, with a scope of tool, action and drawing fingerprint.
 *
 * A second call with the same scope and key and the same parameters shares the first one's promise, even while it
 * is still running; with different parameters it is refused (CIVIL3D.CONFLICT). A failed call is forgotten so it can
 * be retried. Results are kept two minutes after they finish; at most 512 entries, and when every slot holds a call
 * still running, new keyed calls are refused (CIVIL3D.HOST_BUSY).
 *
 * NOTE: the drawing fingerprint in the scope changes when the drawing changes (object counts, the unsaved flag), so a
 * retry after a first call that edited the drawing lands in a new scope and runs again. Only safeForRetry actions take
 * a key, which keeps this harmless, but the store doesn't really deduplicate edits.
 */

/** One remembered call: a stable serialization of its parameters and its (possibly unfinished) result. */
type StoredExecution<T> = {
  signature: string;
  promise: Promise<T>;
  expiresAt: number;
  settled: boolean;
};

/** The key was used before with other parameters: the client is reusing keys, not retrying. */
export class IdempotencyConflictError extends Error {
  readonly code = "CIVIL3D.CONFLICT";
  readonly rpcCode = -32009;

  constructor(scope: string, key: string) {
    super(`Idempotency key '${key}' was already used with different parameters for '${scope}'.`);
    this.name = "IdempotencyConflictError";
  }
}

/** Every slot holds a call still running, so nothing can be evicted to make room. */
export class IdempotencyCapacityError extends Error {
  readonly code = "CIVIL3D.HOST_BUSY";
  readonly rpcCode = -32029;

  constructor(capacity: number) {
    super(`Idempotency store has ${capacity} in-flight executions; retry after work completes.`);
    this.name = "IdempotencyCapacityError";
  }
}

/** The store; the size, lifetime and clock are constructor arguments so tests can shrink them. */
export class IdempotencyStore {
  private readonly entries = new Map<string, StoredExecution<unknown>>();

  constructor(
    private readonly maxEntries = 512,
    private readonly ttlMs = 2 * 60 * 1000,
    private readonly now: () => number = Date.now,
  ) {}

  /**
   * Runs `factory` once per (scope, key) and returns its result to every matching call. `parameters` is compared by
   * value with object keys sorted, so key order doesn't matter. Throws IdempotencyConflictError or
   * IdempotencyCapacityError as described above, or whatever `factory` throws.
   */
  async execute<T>(scope: string, key: string, parameters: unknown, factory: () => Promise<T>): Promise<T> {
    this.purgeExpired();
    const cacheKey = `${scope}\u0000${key}`;
    const signature = stableSerialize(parameters);
    const existing = this.entries.get(cacheKey) as StoredExecution<T> | undefined;

    if (existing) {
      if (existing.signature !== signature) {
        throw new IdempotencyConflictError(scope, key);
      }
      return existing.promise;
    }

    this.evictForCapacity();
    const promise = Promise.resolve().then(factory);
    const entry: StoredExecution<T> = {
      signature,
      promise,
      expiresAt: this.now() + this.ttlMs,
      settled: false,
    };
    this.entries.set(cacheKey, entry);

    try {
      const result = await promise;
      if (this.entries.get(cacheKey) === entry) {
        entry.settled = true;
        entry.expiresAt = this.now() + this.ttlMs;
      }
      return result;
    } catch (error) {
      if (this.entries.get(cacheKey)?.promise === promise) {
        this.entries.delete(cacheKey);
      }
      throw error;
    }
  }

  get size(): number {
    this.purgeExpired();
    return this.entries.size;
  }

  private purgeExpired(): void {
    const now = this.now();
    for (const [key, entry] of this.entries) {
      if (entry.settled && entry.expiresAt <= now) this.entries.delete(key);
    }
  }

  /** Makes room by dropping the oldest finished entries (a Map keeps insertion order); running ones are never dropped. */
  private evictForCapacity(): void {
    while (this.entries.size >= this.maxEntries) {
      const oldestSettled = [...this.entries].find(([, entry]) => entry.settled)?.[0];
      if (oldestSettled === undefined) throw new IdempotencyCapacityError(this.maxEntries);
      this.entries.delete(oldestSettled);
    }
  }
}

/** JSON with object keys sorted, so two parameter objects that differ only in key order serialize the same. */
function stableSerialize(value: unknown): string {
  if (value === null || typeof value !== "object") return JSON.stringify(value) ?? "undefined";
  if (Array.isArray(value)) return `[${value.map(stableSerialize).join(",")}]`;
  const object = value as Record<string, unknown>;
  return `{${Object.keys(object).sort().map((key) => `${JSON.stringify(key)}:${stableSerialize(object[key])}`).join(",")}}`;
}

/** The process-wide store used by domainRuntime.ts. */
export const idempotencyStore = new IdempotencyStore();
