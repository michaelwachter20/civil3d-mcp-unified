import {
  closeSync, copyFileSync, existsSync, mkdirSync, openSync, readdirSync, readFileSync, renameSync, rmSync, statSync, unlinkSync, writeFileSync, writeSync,
} from "node:fs";
import path from "node:path";

/**
 * Writing the knowledge files safely when several programs share them (two AI apps each running this server, Project
 * Analyzer and TBC MCP next to it), with Node built-ins only:
 *
 *   writeAtomically      temp file in the same folder, then rename (retried while Windows holds the target open)
 *   withFileLock         <target>.lock around a read-modify-write; a dead or stale holder's lock is taken over
 *   keepBackup           a copy in <folder>\<stem>.backups\<stem> YYYY-MM-DD HHMMSS<ext>, the newest 3 kept
 *   sweepTemporaryFiles  temp files a crashed write left behind (older than an hour)
 */

const RENAME_RETRY_CODES = new Set(["EPERM", "EBUSY", "EACCES"]);

const errorCode = (error: unknown) => (error as NodeJS.ErrnoException | undefined)?.code;

/** Waits without the event loop; only for the few milliseconds a rename retry needs. */
function pause(ms: number) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

/** Rename, retried a few times (about 0.8 s in all) while a virus scanner, the search indexer or a reader holds the target. */
export function renameWithRetry(from: string, to: string) {
  for (let attempt = 0; ; attempt += 1) {
    try {
      renameSync(from, to);
      return;
    } catch (error) {
      if (attempt >= 5 || !RENAME_RETRY_CODES.has(errorCode(error) ?? "")) throw error;
      pause(25 * 2 ** attempt);
    }
  }
}

/** The temp file name writeAtomically uses: .<name>.<pid>.<time>.tmp next to the target. */
const TEMPORARY = /^\..+\.\d+\.\d+\.tmp$/;

/** Writes the whole file through a temp file in the same folder and a rename; the temp file never stays behind. */
export function writeAtomically(file: string, content: string | Buffer) {
  mkdirSync(path.dirname(file), { recursive: true });
  const temporary = path.join(path.dirname(file), `.${path.basename(file)}.${process.pid}.${Date.now()}.tmp`);
  try {
    if (typeof content === "string") writeFileSync(temporary, content, "utf8");
    else writeFileSync(temporary, content);
    renameWithRetry(temporary, file);
  } finally {
    try { rmSync(temporary, { force: true }); } catch { /* swept at the next start */ }
  }
}

/** Deletes writeAtomically's temp files older than `maxAgeMs` under `directory` (subfolders to `depth`). Never throws. */
export function sweepTemporaryFiles(directory: string, options: { maxAgeMs?: number; depth?: number } = {}): string[] {
  const maxAge = options.maxAgeMs ?? 60 * 60 * 1000;
  const removed: string[] = [];
  const walk = (current: string, depth: number) => {
    let entries;
    try { entries = readdirSync(current, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (depth > 0) walk(full, depth - 1);
      } else if (entry.isFile() && TEMPORARY.test(entry.name)) {
        try {
          if (Date.now() - statSync(full).mtimeMs > maxAge) {
            unlinkSync(full);
            removed.push(full);
          }
        } catch { /* in use or already gone */ }
      }
    }
  };
  walk(directory, options.depth ?? 4);
  return removed;
}

// ---------------------------------------------------------------- locking

const DEFAULT_LOCK_TIMING = { waitMs: 5_000, pollMs: 50, staleMs: 60_000 };
let lockTiming = { ...DEFAULT_LOCK_TIMING };

/** For tests: how long to wait for a lock, how often to look, when a lock counts as left behind. */
export function setFileLockTimingForTesting(value?: Partial<typeof DEFAULT_LOCK_TIMING>) {
  lockTiming = { ...DEFAULT_LOCK_TIMING, ...value };
}

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

function alive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return errorCode(error) !== "ESRCH";   // EPERM: it exists, it just isn't ours
  }
}

/** The lock's content when it was left behind (its process is gone, or it is older than a minute), else undefined. */
function leftBehind(lock: string): string | undefined {
  try {
    const content = readFileSync(lock, "utf8");
    const pid = Number.parseInt(content, 10);
    const age = Date.now() - statSync(lock).mtimeMs;
    // Empty or garbled: its writer (this tool or TBC MCP, which share practices.json) may not have written the pid
    // yet, so it is left behind only after 2 s, as TBC MCP does.
    if (!Number.isInteger(pid) || pid <= 0) return age > 2000 ? content : undefined;
    return age > lockTiming.staleMs || !alive(pid) ? content : undefined;
  } catch {
    return undefined;   // gone already, or being written: look again
  }
}

/** Removes a left-behind lock, unless another process took it over since it was read. */
function takeOver(lock: string, content: string) {
  const aside = `${lock}.${process.pid}.${Date.now()}.stale`;
  try {
    renameSync(lock, aside);
  } catch {
    return;   // someone else took it over first
  }
  try {
    if (readFileSync(aside, "utf8") !== content && !existsSync(lock)) renameSync(aside, lock);   // a fresh lock: put it back
  } catch { /* the lock is free either way */ }
  try { rmSync(aside, { force: true }); } catch { /* removed later */ }
}

function lockError(target: string, lock: string) {
  const error = new Error(
    `${path.basename(target)} is being changed by another program (it holds ${lock}); try again in a moment. ` +
    "If no Civil 3D MCP, Project Analyzer or TBC MCP is running, delete that lock file.",
  ) as Error & { code: string };
  error.code = "CIVIL3D.CONFLICT";
  return error;
}

/**
 * Runs `work` while holding <target>.lock (created exclusively, holding this process id), so a read-modify-write
 * of the file by another process (or another call in this one) waits instead of losing an update. A lock whose
 * process is gone, or older than a minute, is taken over. Waits up to about 5 s, then fails with a clear error.
 */
export async function withFileLock<T>(target: string, work: () => T | Promise<T>): Promise<T> {
  const lock = `${target}.lock`;
  mkdirSync(path.dirname(lock), { recursive: true });
  const started = Date.now();
  for (;;) {
    try {
      const fd = openSync(lock, "wx");
      try { writeSync(fd, `${process.pid}\n`); } finally { closeSync(fd); }
      break;
    } catch (error) {
      // EPERM/EACCES: Windows still deleting the previous lock; it is held until it is gone.
      if (!["EEXIST", "EPERM", "EACCES", "EBUSY"].includes(errorCode(error) ?? "")) throw error;
      const stale = errorCode(error) === "EEXIST" ? leftBehind(lock) : undefined;
      if (stale !== undefined) {
        takeOver(lock, stale);
        continue;
      }
      if (Date.now() - started >= lockTiming.waitMs) throw lockError(target, lock);
      await delay(lockTiming.pollMs);
    }
  }
  try {
    return await work();
  } finally {
    try { unlinkSync(lock); } catch { /* taken over meanwhile, or removed by hand */ }
  }
}

// ---------------------------------------------------------------- backups

const two = (value: number) => String(value).padStart(2, "0");

/** Local "YYYY-MM-DD" and "HHMMSS" of a time, as Project Analyzer stamps its backups. */
export function localStamp(at = new Date()): { date: string; time: string } {
  return {
    date: `${at.getFullYear()}-${two(at.getMonth() + 1)}-${two(at.getDate())}`,
    time: `${two(at.getHours())}${two(at.getMinutes())}${two(at.getSeconds())}`,
  };
}

/** <folder of file>\<stem>.backups, the suite's place for checked copies of a file. */
export const backupFolderOf = (file: string) => path.join(path.dirname(file), `${path.parse(file).name}.backups`);

/** The copies of `file` in its backups folder (named "<stem> <date> <time><ext>"), oldest first. */
export function backupsOf(file: string): string[] {
  const { name, ext } = path.parse(file);
  const folder = backupFolderOf(file);
  try {
    return readdirSync(folder).filter((entry) => entry.startsWith(`${name} `) && entry.toLowerCase().endsWith(ext.toLowerCase()))
      .sort().map((entry) => path.join(folder, entry));
  } catch {
    return [];
  }
}

/** Deletes the oldest copies beyond `keep`. */
export function rotateBackups(file: string, keep = 3) {
  for (const old of backupsOf(file).slice(0, -keep)) {
    try { unlinkSync(old); } catch { /* in use: removed next time */ }
  }
}

/**
 * Copies `file` (as it is now) to its backups folder before it is rewritten and keeps the newest `keep` copies.
 * Returns the copy, or undefined when there was nothing to copy or the copy failed (a backup never blocks a write).
 */
export function keepBackup(file: string, keep = 3): string | undefined {
  try {
    if (!existsSync(file)) return undefined;
    const { name, ext } = path.parse(file);
    const { date, time } = localStamp();
    const target = path.join(backupFolderOf(file), `${name} ${date} ${time}${ext}`);
    mkdirSync(path.dirname(target), { recursive: true });
    copyFileSync(file, target);
    rotateBackups(file, keep);
    return target;
  } catch {
    return undefined;
  }
}
