/**
 * Shared types of `civil3d_help`: installations found (helpDiscovery.ts), parsed topics and images (helpParser.ts),
 * the cached index and search results (helpIndex.ts), and tutorial videos (videoCatalog.ts). helpTool.ts returns
 * these shapes to the AI.
 */

/** The kind of help page, guessed by helpParser.inferTopicType. */
export type HelpTopicType =
  | "Concept"
  | "Procedure"
  | "Command Reference"
  | "Dialog Reference"
  | "Troubleshooting"
  | "Tutorial"
  | "Reference";

/** One installed copy of the offline help; `configured` when it came from CIVIL3D_HELP_ROOT. */
export interface HelpInstallation {
  root: string;
  version: string;
  language: string;
  displayName: string;
  configured: boolean;
}

/**
 * An image a topic shows: `uri` is the `civil3d://help/images/...` address civil3d_help serves it under, `path` the
 * file inside the help folder (helpTool.ts leaves it out of what clients see), `size` in bytes; width and
 * height are known for PNG and GIF only.
 */
export interface HelpImageRef {
  id: string;
  uri: string;
  path: string;
  alt: string;
  caption: string;
  mimeType: string;
  width?: number;
  height?: number;
  size: number;
}

/** An Autodesk tutorial video from autodesk_videos.json (videoCatalog.ts): its help page and direct video links. */
export interface HelpVideoRef {
  id: string;
  uri: string;
  title: string;
  sourceVersion: string;
  pageUrl: string;
  mp4Url: string;
  webmUrl?: string;
}

/**
 * A topic as kept in the cached index: everything but the Markdown body. `id` is this tool's stable id (hash of
 * version and Autodesk topic id), `topicId` Autodesk's own (usually a GUID), `searchText` what the search engine
 * indexes (at most 24,000 characters).
 */
export interface HelpTopicIndexEntry {
  id: string;
  topicId: string;
  uri: string;
  title: string;
  summary: string;
  version: string;
  product: string;
  featureArea: string;
  topicType: HelpTopicType;
  headings: string[];
  tags: string[];
  relatedTopicIds: string[];
  images: HelpImageRef[];
  sourcePath: string;
  canonicalUrl?: string;
  contentHash: string;
  searchText: string;
}

/** A full topic, with its body as Markdown (parsed from the page again on each read). */
export interface HelpTopic extends HelpTopicIndexEntry {
  markdown: string;
}

/** One search hit: the topic's summary fields, a 500-character excerpt around the first query word, and the score. */
export interface HelpSearchResult {
  id: string;
  uri: string;
  title: string;
  summary: string;
  excerpt: string;
  version: string;
  featureArea: string;
  topicType: HelpTopicType;
  tags: string[];
  canonicalUrl?: string;
  imageCount: number;
  score: number;
}

/** What `civil3d_help action=status` reports about the index; `loadedFromCache` is false right after a rebuild. */
export interface HelpIndexStatus {
  version: string;
  root: string;
  topicCount: number;
  imageCount: number;
  cachePath: string;
  sourceFingerprint: string;
  indexedAt: string;
  loadedFromCache: boolean;
}

/**
 * The cached index file (`<cache root>\<version>\index.json.gz`). It is reused only while schemaVersion, version,
 * root and sourceFingerprint all still match; otherwise the help is indexed again.
 */
export interface HelpIndexCache {
  schemaVersion: 1;
  version: string;
  root: string;
  sourceFingerprint: string;
  indexedAt: string;
  topics: HelpTopicIndexEntry[];
}
