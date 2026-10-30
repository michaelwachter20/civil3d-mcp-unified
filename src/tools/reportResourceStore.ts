import { randomUUID } from "node:crypto";

/**
 * Keeps large or report-like tool results for an hour as MCP resources (`civil3d://reports/{id}`), so a client that
 * truncates long tool output can still read the whole answer, and an AI can come back to a report later in the
 * conversation. domainRuntime.ts offers every successful result here and adds a resource link to the answer when one
 * is kept; mcpResources.ts serves and lists them.
 *
 * Held in memory only (gone when the server restarts): at most 64 results, 8 MiB each, 64 MiB in all; the oldest are
 * dropped first to make room.
 */
const MAX_REPORT_RESOURCES = 64;
const MAX_REPORT_RESOURCE_BYTES = 8 * 1024 * 1024;
const MAX_TOTAL_REPORT_BYTES = 64 * 1024 * 1024;
const REPORT_TTL_MS = 60 * 60 * 1000;
/** Results at least this big are kept whatever the action. */
const LARGE_RESULT_BYTES = 32 * 1024;
/** Actions whose results are kept whatever their size ("report", "export_landxml", "quantity_summary", ...). */
const REPORT_ACTION = /(?:^|_)(?:report|export|summary|capabilities)(?:_|$)/i;

/** One kept result: `text` is the serialized JSON exactly as the tool returned it, `size` its UTF-8 bytes, times in ms. */
export interface StoredReportResource {
  id: string;
  uri: string;
  name: string;
  text: string;
  size: number;
  createdAt: number;
  expiresAt: number;
}

const reports = new Map<string, StoredReportResource>();

function removeExpired(now = Date.now()): void {
  for (const [id, report] of reports) {
    if (report.expiresAt <= now) {
      reports.delete(id);
    }
  }
}

function retainedBytes(): number {
  let total = 0;
  for (const report of reports.values()) total += report.size;
  return total;
}

/** Drops the oldest results (Map insertion order) until one more of `incomingBytes` fits both limits. */
function enforceCapacity(incomingBytes: number): void {
  while (
    reports.size >= MAX_REPORT_RESOURCES ||
    retainedBytes() + incomingBytes > MAX_TOTAL_REPORT_BYTES
  ) {
    const oldestId = reports.keys().next().value as string | undefined;
    if (!oldestId) return;
    reports.delete(oldestId);
  }
}

/**
 * Keeps the result when it is at least 32 KB or the action name is report-like, and returns the stored entry (its
 * `uri` goes in the answer); undefined when it isn't worth keeping or is over 8 MiB.
 */
export function maybeStoreReportResource(
  action: string,
  serializedResult: string,
): StoredReportResource | undefined {
  const size = Buffer.byteLength(serializedResult, "utf8");
  if (size < LARGE_RESULT_BYTES && !REPORT_ACTION.test(action)) {
    return undefined;
  }
  if (size > MAX_REPORT_RESOURCE_BYTES || size > MAX_TOTAL_REPORT_BYTES) {
    return undefined;
  }

  removeExpired();
  enforceCapacity(size);
  const id = randomUUID();
  const now = Date.now();
  const report: StoredReportResource = {
    id,
    uri: `civil3d://reports/${id}`,
    name: `Civil 3D ${action} result`,
    text: serializedResult,
    size,
    createdAt: now,
    expiresAt: now + REPORT_TTL_MS,
  };
  reports.set(id, report);
  return report;
}

/** A kept result by id; undefined once it has expired or been pushed out. */
export function getReportResource(id: string): StoredReportResource | undefined {
  removeExpired();
  return reports.get(id);
}

/** Every result still kept, oldest first (for the resource list and id completion). */
export function listReportResources(): StoredReportResource[] {
  removeExpired();
  return [...reports.values()];
}

export function clearReportResourcesForTesting(): void {
  reports.clear();
}
