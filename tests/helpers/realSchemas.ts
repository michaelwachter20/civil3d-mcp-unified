// Test helper: the REAL input and response schemas of the manifest tools (src/tools/domains/*), so schema tests catch a
// change in src instead of checking a copy declared in the test file. Not a test file itself (vitest runs *.test.ts).
// Pure: nothing here calls Civil 3D or the plugin.

import { z } from "zod";
import type { DomainToolDefinition, DomainToolExposure } from "../../src/tools/domainRuntime.js";
import { MIGRATED_DOMAIN_DEFINITIONS } from "../../src/tools/toolManifest.js";

type ParseResult = z.SafeParseReturnType<unknown, Record<string, unknown>>;
type SafeParser = { safeParse: (args: unknown) => ParseResult; parse: (args: unknown) => Record<string, unknown> };

/** safeParse plus a parse that throws the zod error, like a zod schema. */
function parser(safeParse: (args: unknown) => ParseResult): SafeParser {
  return {
    safeParse,
    parse: (args: unknown) => {
      const result = safeParse(args);
      if (!result.success) throw result.error;
      return result.data;
    },
  };
}

/** The domain definition and exposure that register `toolName` (canonical tool or alias). */
function findExposure(toolName: string): { definition: DomainToolDefinition; exposure: DomainToolExposure } {
  for (const definition of MIGRATED_DOMAIN_DEFINITIONS as DomainToolDefinition[]) {
    const exposure = definition.exposures.find((candidate) => candidate.toolName === toolName);
    if (exposure) return { definition, exposure };
  }
  throw new Error(`No manifest tool named '${toolName}'.`);
}

/**
 * What a call to `toolName` with these arguments is validated against: the exposure's resolveAction (an alias renames
 * fields, e.g. alignmentName to name), then the resolved action's real input schema. A call that resolves to an action
 * the tool doesn't offer fails, as it would in domainRuntime.ts.
 */
export function toolInput(toolName: string): SafeParser {
  const { definition, exposure } = findExposure(toolName);
  return parser((args: unknown) => {
    const resolved = exposure.resolveAction((args ?? {}) as Record<string, unknown>);
    const action = definition.actions[resolved.action];
    if (!action || !exposure.supportedActions.includes(resolved.action)) {
      return z.never().safeParse(args) as ParseResult;
    }
    return action.inputSchema.safeParse(resolved.args) as ParseResult;
  });
}

/** toolInput for one action of a multi-action tool: `action` is added to every call's arguments. */
export function toolActionInput(toolName: string, action: string): SafeParser {
  const tool = toolInput(toolName);
  return parser((args: unknown) => tool.safeParse({ ...(args as object), action }));
}

/** An action's real input schema with `action` filled in, for actions no tool offers (unsupported, orchestrator-only). */
export function actionInput(definition: DomainToolDefinition, action: string): SafeParser {
  const schema = definition.actions[action]?.inputSchema;
  if (!schema) throw new Error(`Domain '${definition.domain}' has no action '${action}'.`);
  return parser((args: unknown) => schema.safeParse({ action, ...(args as object) }) as ParseResult);
}

/** An action's real response schema (the one domainRuntime.ts checks every answer against). */
export function actionResponse(definition: DomainToolDefinition, action: string) {
  const schema = definition.actions[action]?.responseSchema;
  if (!schema) throw new Error(`Domain '${definition.domain}' action '${action}' has no response schema.`);
  return schema;
}

/** The input shape a tool shows MCP clients (z.object of its inputShape), e.g. to check its action enum. */
export function toolShape(toolName: string) {
  return z.object(findExposure(toolName).exposure.inputShape);
}
