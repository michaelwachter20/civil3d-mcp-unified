import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

/**
 * The framework standards search behind `civil3d_standards action=lookup` (standardsDomain.ts) and the
 * `civil3d://standards/framework` resource (mcpResources.ts). The rules are general CAD-standards guidance
 * (templates, styles, layers, labels, plotting, ...) shipped with the server in
 * src/standards/data/civil3d_framework_rules.json, copied to build/standards/data by the build. Node only: no
 * drawing and no plugin call.
 *
 * Search is plain keyword scoring, no index: the file is small (a few dozen rules), read once and cached.
 */

/** One rule as stored in civil3d_framework_rules.json: a section of the framework document and its guidance. */
export interface FrameworkPromptRule {
  section_number: string;
  title: string;
  pages: number[];
  rule: string;
  /** The rule's own weight (how central it is), added to every match score up to 20. */
  score: number;
  tags: string[];
}

/** What to search for: free text, a topic (a key of TOPIC_ALIASES or any word), exact tags; 1-20 results (5). */
export interface StandardsLookupArgs {
  query?: string;
  topic?: string;
  tags?: string[];
  maxResults?: number;
}

export interface StandardsLookupMatch {
  sectionNumber: string;
  title: string;
  pages: number[];
  rule: string;
  tags: string[];
  sourceScore: number;
  matchScore: number;
}

/**
 * The lookup answer: the best matches (highest score first), how many rules were searched, and up to five distinct
 * rule texts as `recommendedPractices`. `matchedCount` is the number returned, after the `maxResults` cut.
 */
export interface StandardsLookupResult {
  query: string | null;
  topic: string | null;
  tags: string[];
  totalRulesLoaded: number;
  matchedCount: number;
  matches: StandardsLookupMatch[];
  recommendedPractices: string[];
}

/** Extra search words for each known topic, so `topic: "layers"` also matches "layer manager", "layer state", .... */
const TOPIC_ALIASES: Record<string, string[]> = {
  templates: ["template", "templates", "production drawing", "production drawings", "hierarchy"],
  styles: ["style", "styles", "feature style", "label style", "code set"],
  layers: ["layer", "layers", "layer naming", "layer manager", "layer state"],
  labels: ["label", "labels", "labeling", "annotation", "annotate"],
  plotting: ["plot", "plotting", "stb", "ctb", "publish", "lineweight"],
  textstyles: ["textstyle", "textstyles", "font", "proposed", "existing"],
  proposed_existing: ["proposed", "existing", "design", "survey", "conditions"],
  pipe_networks: ["pipe", "pipes", "structure", "pressure pipe", "network"],
  profile_section: ["profile", "profiles", "section", "sections", "profile view", "section view"],
};

/** The rules, read once per process; a read or parse failure stays cached too, so later lookups fail the same way. */
let cachedRulesPromise: Promise<FrameworkPromptRule[]> | null = null;

/** The rules file next to this module, in src/standards/data or (when built) build/standards/data. */
function getPromptRulesPath(): string {
  const currentDirectory = path.dirname(fileURLToPath(import.meta.url));
  return path.resolve(
    currentDirectory,
    "../standards/data/civil3d_framework_rules.json"
  );
}

async function loadPromptRules(): Promise<FrameworkPromptRule[]> {
  if (!cachedRulesPromise) {
    cachedRulesPromise = readFile(getPromptRulesPath(), "utf8").then((raw) => {
      const parsed = JSON.parse(raw) as FrameworkPromptRule[];
      return Array.isArray(parsed) ? parsed : [];
    });
  }

  return await cachedRulesPromise;
}

/** Lowercase, punctuation to spaces (underscores and hyphens kept), single spaces. */
function normalizeText(value: string): string {
  return value.toLowerCase().replace(/[^a-z0-9\s_\-]/g, " ").replace(/\s+/g, " ").trim();
}

function splitTerms(value: string): string[] {
  return normalizeText(value)
    .split(" ")
    .map((term) => term.trim())
    .filter((term) => term.length >= 2);
}

function getTopicTerms(topic?: string): string[] {
  if (!topic) {
    return [];
  }

  const normalizedTopic = normalizeText(topic);
  return Array.from(new Set([normalizedTopic, ...(TOPIC_ALIASES[normalizedTopic] ?? [])].flatMap(splitTerms)));
}

/**
 * How well a rule matches the request, before its own weight: 12 per requested tag it has, 6 per topic word in its
 * text, 5 per query word of six or more letters (3 for shorter ones), 8 when its title contains the topic. 0 means
 * it doesn't match at all. Matching is substring matching, so "lab" finds "label".
 */
function relevance(rule: FrameworkPromptRule, args: StandardsLookupArgs): number {
  const queryTerms = splitTerms(args.query ?? "");
  const topicTerms = getTopicTerms(args.topic);
  const requestedTags = (args.tags ?? []).map(normalizeText);
  const searchableText = normalizeText(`${rule.title} ${rule.rule} ${rule.tags.join(" ")}`);

  let matchScore = 0;

  for (const tag of requestedTags) {
    if (rule.tags.map(normalizeText).includes(tag)) {
      matchScore += 12;
    }
  }

  for (const term of topicTerms) {
    if (searchableText.includes(term)) {
      matchScore += 6;
    }
  }

  for (const term of queryTerms) {
    if (searchableText.includes(term)) {
      matchScore += term.length >= 6 ? 5 : 3;
    }
  }

  if (args.topic && normalizeText(rule.title).includes(normalizeText(args.topic))) {
    matchScore += 8;
  }

  return matchScore;
}

/** The first five distinct rule texts among the matches, in match order. */
function buildRecommendedPractices(matches: StandardsLookupMatch[]): string[] {
  const uniqueRules = new Set<string>();
  const recommendations: string[] = [];

  for (const match of matches) {
    const normalizedRule = match.rule.trim();
    if (!normalizedRule || uniqueRules.has(normalizedRule)) {
      continue;
    }

    uniqueRules.add(normalizedRule);
    recommendations.push(normalizedRule);

    if (recommendations.length >= 5) {
      break;
    }
  }

  return recommendations;
}

/**
 * Searches the framework rules and returns the best `maxResults` (default 5, 1-20). With no query, topic or tags it
 * returns the highest-weighted rules. With any of them, only rules that match are returned (a query matching
 * nothing returns none, rather than the top-weighted rules as if they matched); they are ranked by how well they
 * match plus their own `score` (at most 20).
 */
export async function lookupFrameworkStandards(args: StandardsLookupArgs): Promise<StandardsLookupResult> {
  const rules = await loadPromptRules();
  const maxResults = Math.max(1, Math.min(args.maxResults ?? 5, 20));

  const hasFilter = Boolean(args.query?.trim()) || Boolean(args.topic?.trim()) || (args.tags?.length ?? 0) > 0;
  const scoredMatches = rules
    .map((rule) => {
      const matched = relevance(rule, args);
      return { rule, matched, matchScore: matched + Math.min(rule.score, 20) };
    })
    .filter(({ matched }) => !hasFilter || matched > 0)
    .sort((left, right) => right.matchScore - left.matchScore)
    .slice(0, maxResults)
    .map(({ rule, matchScore }) => ({
      sectionNumber: rule.section_number,
      title: rule.title,
      pages: rule.pages,
      rule: rule.rule,
      tags: rule.tags,
      sourceScore: rule.score,
      matchScore,
    } satisfies StandardsLookupMatch));

  return {
    query: args.query?.trim() || null,
    topic: args.topic?.trim() || null,
    tags: args.tags ?? [],
    totalRulesLoaded: rules.length,
    matchedCount: scoredMatches.length,
    matches: scoredMatches,
    recommendedPractices: buildRecommendedPractices(scoredMatches),
  };
}
