import { z } from "zod";
import type { DomainToolDefinition } from "../domainRuntime.js";

/**
 * The docs domain: `civil3d_docs` (two actions), its alias `list_tool_capabilities`, and `civil3d_orchestrate`, which
 * toolManifest.ts lists to MCP clients even in compact mode. Neither action calls the plugin from here:
 * `list_tool_capabilities` filters the flat tool catalog (tool_catalog.ts), and `orchestrate` hands everything to
 * civil3d_orchestrate.ts, which routes a plain-language request (or an explicit tool and action) to a tool, plans
 * it, and with `execute: true` runs it through executeRegisteredTool, so the chosen tool's own validation and
 * approval apply.
 *
 * Both modules are imported lazily inside `execute` because they import the tool manifest, which imports this file;
 * a static import would be a cycle.
 */

/** The catalog answer: every domain name, and the catalog entries left after the domain and status filters. */
const ToolCapabilityListResponseSchema = z.object({
  domains: z.array(z.string()),
  tools: z.array(z.object({
    toolName: z.string(),
    displayName: z.string(),
    description: z.string(),
    domain: z.string(),
    capabilities: z.array(z.string()),
    operations: z.array(z.string()).optional(),
    pluginMethods: z.array(z.string()).optional(),
    requiresActiveDrawing: z.boolean(),
    safeForRetry: z.boolean(),
    status: z.enum(["implemented", "planned"]),
  })),
});

const ListToolCapabilitiesArgsSchema = z.object({
  action: z.literal("list_tool_capabilities"),
  domain: z.string().optional(),
  status: z.enum(["implemented", "planned"]).optional(),
});

/**
 * `request` (plain language) or `toolName` (+ `toolAction`, `toolParameters`) says what to do; `execute` runs the
 * plan instead of only returning it. The named fields after them (alignmentName, surfaceName, payItems, ...) are
 * hints the router uses to fill the chosen tool's arguments; names left out may be filled from the user's selection
 * in Civil 3D.
 */
const OrchestrateArgsSchema = z.object({
  action: z.literal("orchestrate"),
  request: z.string().min(1).optional(),
  execute: z.boolean().optional(),
  toolName: z.string().optional(),
  toolAction: z.string().optional(),
  toolParameters: z.record(z.string(), z.unknown()).optional(),
  name: z.string().optional(),
  alignmentName: z.string().optional(),
  corridorName: z.string().optional(),
  profileName: z.string().optional(),
  surfaceName: z.string().optional(),
  baseSurface: z.string().optional(),
  comparisonSurface: z.string().optional(),
  style: z.string().optional(),
  layer: z.string().optional(),
  labelSet: z.string().optional(),
  filePath: z.string().optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  gridSpacing: z.number().optional(),
  designSpeed: z.number().optional(),
  inflow: z.number().optional(),
  outflow: z.number().optional(),
  bottomElevation: z.number().optional(),
  topElevation: z.number().optional(),
  payItems: z.array(z.object({
    code: z.string(),
    description: z.string(),
    unit: z.string(),
    unitPrice: z.number().nonnegative(),
  })).optional(),
});

/** Every argument either `civil3d_docs` action takes, all optional except `action`: what MCP clients see. */
const canonicalDocsInputShape = {
  action: z.enum(["list_tool_capabilities", "orchestrate"]),
  domain: z.string().optional(),
  status: z.enum(["implemented", "planned"]).optional(),
  request: z.string().min(1).optional(),
  execute: z.boolean().optional(),
  toolName: z.string().optional(),
  toolAction: z.string().optional(),
  toolParameters: z.record(z.string(), z.unknown()).optional(),
  name: z.string().optional(),
  alignmentName: z.string().optional(),
  corridorName: z.string().optional(),
  profileName: z.string().optional(),
  surfaceName: z.string().optional(),
  baseSurface: z.string().optional(),
  comparisonSurface: z.string().optional(),
  style: z.string().optional(),
  layer: z.string().optional(),
  labelSet: z.string().optional(),
  filePath: z.string().optional(),
  outputPath: z.string().optional(),
  overwrite: z.boolean().optional(),
  gridSpacing: z.number().optional(),
  designSpeed: z.number().optional(),
  inflow: z.number().optional(),
  outflow: z.number().optional(),
  bottomElevation: z.number().optional(),
  topElevation: z.number().optional(),
  payItems: z.array(z.object({
    code: z.string(),
    description: z.string(),
    unit: z.string(),
    unitPrice: z.number().nonnegative(),
  })).optional(),
};

/**
 * The docs domain definition, listed in toolManifest.ts. `orchestrate` is declared safe to retry and needs no
 * approval of its own; when it executes, the approval check of the tool it runs applies (that tool's action, with
 * the parameters it is actually called with).
 */
export const DOCS_DOMAIN_DEFINITION: DomainToolDefinition = {
  domain: "docs",
  actions: {
    list_tool_capabilities: {
      action: "list_tool_capabilities",
      inputSchema: ListToolCapabilitiesArgsSchema,
      responseSchema: ToolCapabilityListResponseSchema,
      capabilities: ["query", "inspect"],
      requiresActiveDrawing: false,
      safeForRetry: true,
      execute: async (args) => {
        const parsedArgs = args as z.infer<typeof ListToolCapabilitiesArgsSchema>;
        const { listDomains, listToolCatalog } = await import("../tool_catalog.js");
        const domainFilter = parsedArgs.domain?.trim().toLowerCase();
        const statusFilter = parsedArgs.status;
        return ToolCapabilityListResponseSchema.parse({
          domains: listDomains(),
          tools: listToolCatalog().filter((entry) => {
            const domainMatches = !domainFilter || entry.domain === domainFilter;
            const statusMatches = !statusFilter || entry.status === statusFilter;
            return domainMatches && statusMatches;
          }),
        });
      },
    },
    orchestrate: {
      action: "orchestrate",
      inputSchema: OrchestrateArgsSchema,
      responseSchema: z.object({}).passthrough(),
      capabilities: ["query", "inspect", "generate"],
      requiresActiveDrawing: true,
      safeForRetry: true,
      execute: async (args) => {
        const parsedArgs = args as z.infer<typeof OrchestrateArgsSchema>;
        const { executeCivil3DOrchestrate } = await import("../civil3d_orchestrate.js");
        return await executeCivil3DOrchestrate({
          request: parsedArgs.request,
          execute: parsedArgs.execute,
          toolName: parsedArgs.toolName,
          toolAction: parsedArgs.toolAction,
          toolParameters: parsedArgs.toolParameters,
          name: parsedArgs.name,
          alignmentName: parsedArgs.alignmentName,
          corridorName: parsedArgs.corridorName,
          profileName: parsedArgs.profileName,
          surfaceName: parsedArgs.surfaceName,
          baseSurface: parsedArgs.baseSurface,
          comparisonSurface: parsedArgs.comparisonSurface,
          style: parsedArgs.style,
          layer: parsedArgs.layer,
          labelSet: parsedArgs.labelSet,
          filePath: parsedArgs.filePath,
          outputPath: parsedArgs.outputPath,
          overwrite: parsedArgs.overwrite,
          gridSpacing: parsedArgs.gridSpacing,
          designSpeed: parsedArgs.designSpeed,
          inflow: parsedArgs.inflow,
          outflow: parsedArgs.outflow,
          bottomElevation: parsedArgs.bottomElevation,
          topElevation: parsedArgs.topElevation,
          payItems: parsedArgs.payItems,
        });
      },
    },
  },
  exposures: [
    {
      toolName: "civil3d_docs",
      displayName: "Civil 3D Docs",
      description: "Lists tool capabilities and routes natural-language orchestration requests through a single docs/orchestration tool.",
      inputShape: canonicalDocsInputShape,
      supportedActions: ["list_tool_capabilities", "orchestrate"],
      resolveAction: (rawArgs) => ({ action: String(rawArgs.action ?? ""), args: rawArgs }),
    },
    {
      toolName: "list_tool_capabilities",
      displayName: "List Tool Capabilities",
      description: "Lists domain and capability metadata for the Civil 3D MCP tool catalog, including implemented and planned tools.",
      inputShape: {
        domain: z.string().optional(),
        status: z.enum(["implemented", "planned"]).optional(),
      },
      supportedActions: ["list_tool_capabilities"],
      resolveAction: (rawArgs) => ({ action: "list_tool_capabilities", args: { action: "list_tool_capabilities", ...rawArgs } }),
    },
    {
      toolName: "civil3d_orchestrate",
      displayName: "Civil 3D Orchestrate",
      description: "Routes a natural-language Civil 3D request to the best starting tool or action and executes routed work through the registered MCP tool surface.",
      inputShape: {
        request: z.string().min(1).optional(),
        execute: z.boolean().optional(),
        toolName: z.string().optional(),
        toolAction: z.string().optional(),
        toolParameters: z.record(z.string(), z.unknown()).optional(),
        name: z.string().optional(),
        alignmentName: z.string().optional(),
        corridorName: z.string().optional(),
        profileName: z.string().optional(),
        surfaceName: z.string().optional(),
        baseSurface: z.string().optional(),
        comparisonSurface: z.string().optional(),
        style: z.string().optional(),
        layer: z.string().optional(),
        labelSet: z.string().optional(),
        filePath: z.string().optional(),
        outputPath: z.string().optional(),
        overwrite: z.boolean().optional(),
        gridSpacing: z.number().optional(),
        designSpeed: z.number().optional(),
        inflow: z.number().optional(),
        outflow: z.number().optional(),
        bottomElevation: z.number().optional(),
        topElevation: z.number().optional(),
        payItems: z.array(z.object({
          code: z.string(),
          description: z.string(),
          unit: z.string(),
          unitPrice: z.number().nonnegative(),
        })).optional(),
      },
      supportedActions: ["orchestrate"],
      resolveAction: (rawArgs) => ({ action: "orchestrate", args: { action: "orchestrate", ...rawArgs } }),
    },
  ],
};
