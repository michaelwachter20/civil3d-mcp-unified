// Minimal leveled logger for the MCP server. Every line goes to stderr (console.error) because stdout carries the
// MCP stdio protocol and any stray output there would corrupt it. Used by index.ts, httpBridge.ts, domainRuntime.ts,
// the connection code and the knowledge modules. The level comes from CIVIL3D_LOG_LEVEL (debug, info, warn, error, any
// case; default info), read once at startup; any other value falls back to info with one warning line. Don't log
// drawing content or job data at info level.
type LogLevel = "debug" | "info" | "warn" | "error";

const LOG_LEVELS: Record<LogLevel, number> = {
  debug: 0,
  info: 1,
  warn: 2,
  error: 3,
};

/**
 * The level for a CIVIL3D_LOG_LEVEL value: one of the four names (trimmed, any case), else "info" with a warning to
 * print, so a typo never switches logging off.
 */
export function resolveLogLevel(value: string | undefined): { level: LogLevel; warning?: string } {
  const normalized = value?.trim().toLowerCase();
  if (!normalized) return { level: "info" };
  if (normalized in LOG_LEVELS) return { level: normalized as LogLevel };
  return {
    level: "info",
    warning: `CIVIL3D_LOG_LEVEL='${value}' is not one of debug, info, warn, error; logging at info.`,
  };
}

const resolvedLevel = resolveLogLevel(process.env.CIVIL3D_LOG_LEVEL);
const currentLevel: LogLevel = resolvedLevel.level;
if (resolvedLevel.warning) {
  console.error(formatMessage("warn", "Logger", resolvedLevel.warning));
}

/** True when `level` is at or above the configured level. */
function shouldLog(level: LogLevel): boolean {
  return LOG_LEVELS[level] >= LOG_LEVELS[currentLevel];
}

/** One log line: `[ISO time] [LEVEL] [component] message {json data}`. */
function formatMessage(level: LogLevel, component: string, message: string, data?: Record<string, unknown>): string {
  const timestamp = new Date().toISOString();
  const base = `[${timestamp}] [${level.toUpperCase()}] [${component}] ${message}`;
  if (data && Object.keys(data).length > 0) {
    return `${base} ${JSON.stringify(data)}`;
  }
  return base;
}

/**
 * A logger tagged with `component` (for example "MCP", "HttpBridge"). Each method writes one line to stderr when the
 * level is enabled; `data` is appended as JSON.
 */
export function createLogger(component: string) {
  return {
    debug(message: string, data?: Record<string, unknown>) {
      if (shouldLog("debug")) {
        console.error(formatMessage("debug", component, message, data));
      }
    },
    info(message: string, data?: Record<string, unknown>) {
      if (shouldLog("info")) {
        console.error(formatMessage("info", component, message, data));
      }
    },
    warn(message: string, data?: Record<string, unknown>) {
      if (shouldLog("warn")) {
        console.error(formatMessage("warn", component, message, data));
      }
    },
    error(message: string, data?: Record<string, unknown>) {
      if (shouldLog("error")) {
        console.error(formatMessage("error", component, message, data));
      }
    },
  };
}
