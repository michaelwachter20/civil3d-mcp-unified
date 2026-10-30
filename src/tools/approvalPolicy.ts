import { createHash, randomUUID } from "node:crypto";
import { withApplicationConnection } from "../utils/ConnectionManager.js";
import type { ToolCapability } from "./toolMetadata.js";

type JsonObject = Record<string, unknown>;

/**
 * The approval policy for drawing changes: decides which actions need the user's approval and checks the tokens
 * that grant it. The flow is civil3d_preview_action (does this need approval?), civil3d_request_approval (issue a
 * token, approvalTool.ts), then the real call with `approvalToken`; domainRuntime.ts calls `enforce` before every
 * action. A token is kept in memory only, used once, expires (5 minutes by default) and is bound to the tool, the
 * action, a hash of the exact parameters and a fingerprint of the active drawing, so it can't be reused for a
 * different change or another drawing. CIVIL3D_APPROVAL_MODE=disabled switches the policy off (test setups only).
 */

/** What the policy needs to know about an action: its tool, name, capabilities and retry safety. */
export interface ApprovalTarget {
  toolName: string;
  action: string;
  capabilities: ToolCapability[];
  safeForRetry: boolean;
  requiresActiveDrawing?: boolean;
}

interface ApprovalGrant {
  toolName: string;
  action: string;
  parametersHash: string;
  drawingFingerprint: string;
  expiresAt: number;
}

export interface ApprovalReceipt {
  approvalToken: string;
  expiresAt: string;
  drawingFingerprint: string;
}

export class ApprovalRequiredError extends Error {}

export class ApprovalValidationError extends Error {}

export type DrawingFingerprintProvider = () => Promise<string>;

const APPROVAL_TOKEN_TTL_MS = 5 * 60 * 1000;
const NO_ACTIVE_DRAWING_FINGERPRINT = "no-active-drawing";
const MUTATING_CAPABILITIES = new Set<ToolCapability>([
  "create",
  "edit",
  "delete",
  "manage",
  "import",
  "export",
]);
/** Action names that always need approval, whatever their capabilities say (matched as whole words between underscores). */
const EXPLICIT_APPROVAL_ACTION = /(?:^|_)(?:delete|remove|import|export|save|new|undo|redo|overwrite|replace|publish|promote|sync|fix|remediate)(?:_|$)/i;

/** JSON with object keys sorted, so the same parameters hash the same whatever order the client sent them in. */
function stableJson(value: unknown): string {
  if (Array.isArray(value)) {
    return `[${value.map((item) => stableJson(item)).join(",")}]`;
  }

  if (value && typeof value === "object") {
    const object = value as Record<string, unknown>;
    return `{${Object.keys(object)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${stableJson(object[key])}`)
      .join(",")}}`;
  }

  return JSON.stringify(value);
}

/**
 * SHA-256 of the parameters without `approvalToken`. Everything else counts, `action` and `idempotencyKey` included,
 * so the parameters given to civil3d_request_approval must be exactly the ones the call sends.
 */
function hashParameters(parameters: JsonObject): string {
  const normalized = { ...parameters };
  delete normalized.approvalToken;
  return createHash("sha256").update(stableJson(normalized)).digest("hex");
}

/** Whether `enforce` will demand a token: the action is risky and approval mode isn't disabled. */
export function isApprovalRequired(target: ApprovalTarget): boolean {
  if (process.env.CIVIL3D_APPROVAL_MODE === "disabled") {
    return false;
  }

  return hasApprovalRisk(target);
}

/**
 * Whether an action is risky: a delete, import or export capability; a name matching EXPLICIT_APPROVAL_ACTION; or a
 * mutating capability on an action that isn't safe to retry. Also drives the MCP destructiveHint.
 */
export function hasApprovalRisk(target: ApprovalTarget): boolean {
  const mutatesState = target.capabilities.some((capability) => MUTATING_CAPABILITIES.has(capability));
  return (
    target.capabilities.some((capability) => ["delete", "import", "export"].includes(capability)) ||
    EXPLICIT_APPROVAL_ACTION.test(target.action) ||
    (!target.safeForRetry && mutatesState)
  );
}

/**
 * A hash of the plugin's getDrawingInfo answer (file, units, coordinate system, the unsaved-changes flag, object
 * counts). Any change in it changes the fingerprint: another drawing, but also an edit in the same drawing between
 * approval and call, after which the token is refused and has to be requested again.
 */
export async function getActiveDrawingFingerprint(): Promise<string> {
  const drawingInfo = await withApplicationConnection((client) => client.sendCommand("getDrawingInfo", {}));
  return createHash("sha256").update(stableJson(drawingInfo)).digest("hex");
}

/** Whether the fingerprint failed only because no drawing is open (allowed for actions that don't need one). */
function isNoActiveDrawingError(error: unknown): boolean {
  const code = error && typeof error === "object" && "code" in error
    ? String(error.code)
    : "";
  const message = error instanceof Error ? error.message : String(error);
  return code === "CIVIL3D.NO_DRAWING" || code === "CIVIL3D.NO_ACTIVE_DRAWING" ||
    /no active (?:civil 3d )?drawing|no active (?:civil 3d )?document/i.test(message);
}

/** Issues and checks approval tokens. The clock and fingerprint source can be replaced in tests. */
export class ApprovalPolicyService {
  private readonly grants = new Map<string, ApprovalGrant>();

  public constructor(
    private readonly drawingFingerprint: DrawingFingerprintProvider = getActiveDrawingFingerprint,
    private readonly now: () => number = () => Date.now(),
  ) {}

  private async fingerprintFor(target: ApprovalTarget): Promise<string> {
    try {
      return await this.drawingFingerprint();
    } catch (error) {
      if (target.requiresActiveDrawing === false && isNoActiveDrawingError(error)) {
        return NO_ACTIVE_DRAWING_FINGERPRINT;
      }
      throw error;
    }
  }

  /** Issues a token for an action that needs approval; refuses (ApprovalValidationError) for one that doesn't. */
  public async requestApproval(
    target: ApprovalTarget,
    parameters: JsonObject,
    ttlMs = APPROVAL_TOKEN_TTL_MS,
  ): Promise<ApprovalReceipt> {
    if (!isApprovalRequired(target)) {
      throw new ApprovalValidationError(
        `'${target.toolName}' action '${target.action}' does not require approval. Execute it directly.`,
      );
    }

    const drawingFingerprint = await this.fingerprintFor(target);
    const expiresAt = this.now() + ttlMs;
    const approvalToken = randomUUID();
    this.grants.set(approvalToken, {
      toolName: target.toolName,
      action: target.action,
      parametersHash: hashParameters(parameters),
      drawingFingerprint,
      expiresAt,
    });

    return {
      approvalToken,
      expiresAt: new Date(expiresAt).toISOString(),
      drawingFingerprint,
    };
  }

  /**
   * Lets the call through, or throws: ApprovalRequiredError when a token is needed and missing (its message tells the
   * AI what to call), ApprovalValidationError when the token is unknown, used, expired, for another action or
   * parameters, or for a drawing that has changed. The token is consumed before the checks, so a failed attempt
   * still uses it up.
   */
  public async enforce(target: ApprovalTarget, parameters: JsonObject): Promise<void> {
    if (!isApprovalRequired(target)) {
      return;
    }

    const approvalToken = typeof parameters.approvalToken === "string" ? parameters.approvalToken : undefined;
    if (!approvalToken) {
      throw new ApprovalRequiredError(
        `Approval required for '${target.toolName}' action '${target.action}'. ` +
        "Call civil3d_request_approval with the same toolName, action, and parameters, then retry with approvalToken.",
      );
    }

    const grant = this.grants.get(approvalToken);
    if (!grant) {
      throw new ApprovalValidationError("Approval token is missing, expired, or has already been used.");
    }

    this.grants.delete(approvalToken);

    if (grant.expiresAt <= this.now()) {
      throw new ApprovalValidationError("Approval token has expired. Request a new approval.");
    }

    if (
      grant.toolName !== target.toolName ||
      grant.action !== target.action ||
      grant.parametersHash !== hashParameters(parameters)
    ) {
      throw new ApprovalValidationError("Approval token does not match this tool action and its parameters.");
    }

    const activeDrawingFingerprint = await this.fingerprintFor(target);
    if (grant.drawingFingerprint !== activeDrawingFingerprint) {
      throw new ApprovalValidationError("The active drawing changed after approval. Request approval for the current drawing.");
    }
  }
}

/** The server-wide policy instance (tokens live as long as the server process). */
export const approvalPolicy = new ApprovalPolicyService();
