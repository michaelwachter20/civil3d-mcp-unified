-- Project knowledge database, schema version 1.
--
-- One SQLite file per job: <shared>\projects\<slug>.db, where <shared> is the suite's folder, by default
-- Documents\Project Knowledge (docs/suite-knowledge.md, "Where"). Shared by Project Analyzer (fills it), Civil 3D MCP and TBC MCP (read it, add
-- notes). The rules that go with these tables are in docs/suite-knowledge.md; this file is the exact table layout.
--
-- Every tool that creates a database runs this whole file (it is safe to run on an existing one: IF NOT EXISTS
-- everywhere) and then sets meta.schema_version = '1'. A tool must refuse to write to a database whose
-- meta.schema_version is higher than the one it knows, and may still read it.
--
-- Connection settings every tool uses: PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 30000;
-- PRAGMA foreign_keys = OFF (no foreign keys are declared). Writes go in short BEGIN IMMEDIATE transactions.
--
-- Times are UTC ISO 8601 with milliseconds and Z, as JavaScript's toISOString(): 2026-10-25T21:42:47.123Z.
-- Dates about the project are 'YYYY', 'YYYY-MM' or 'YYYY-MM-DD'. JSON columns hold JSON text.
-- tool values: 'project-analyzer', 'civil3d', 'tbc'.

-- Settings of the project. Keys:
--   schema_version   '1'
--   project_name     "Elm Street Drainage"            project_id   12 hex characters, never changes (merges use it)
--   created          time                              source_root  the analysed folder on this PC (absolute)
--   copy_status      what the user said: "copy of P:\Jobs\1234 Elm St", "not a copy", "assumed a copy"
--   original_roots   JSON list of folders the user said the files were copied from
--   reference_roots  JSON list of {"original": "p:\\jobs\\1234 elm st", "in_copy": "Project"} (worked out from refs)
--   last_run_started / last_run_finished / source_unchanged ("yes" or "N files changed")   written by the analyzer
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);

-- Every file in the job folder (depth 0) and every file found inside one (depth 1+: archive members, email
-- attachments, mailbox messages). Nothing here is a copy of the file: only what describes it.
CREATE TABLE IF NOT EXISTS files (
    id           TEXT PRIMARY KEY,   -- 16 hex characters: sha1 of the logical path (below), first 16
    root_id      TEXT NOT NULL,      -- the depth-0 file it came from (itself at depth 0)
    parent_id    TEXT,               -- the file it was directly inside
    depth        INTEGER NOT NULL,
    path         TEXT NOT NULL,      -- relative to source_root, backslashes; 'outer.zip::inner/file.pdf' inside a file
    name         TEXT NOT NULL,
    ext          TEXT NOT NULL,      -- lower case with the dot, '' when none
    kind         TEXT NOT NULL,      -- word pdf excel powerpoint visio cad email mailbox archive image text landxml
                                     -- jobxml points pointcloud plotstyle gis sqlite rinex media noreader
                                     -- unknown ignored
    size         INTEGER,
    mtime        TEXT,
    ctime        TEXT,
    sha256       TEXT,
    status       TEXT NOT NULL,      -- pending running done partial failed password no_reader duplicate ignored
    duplicate_of TEXT,               -- status duplicate: the file with the same content whose text is stored
    reader       TEXT,               -- which reader produced the text: 'pandoc', 'markitdown', 'ezdxf', 'wordcraft'...
    chars        INTEGER,            -- characters of text stored
    summary      TEXT,               -- one line: pages, sheets, OCR, sender ...
    doc_date     TEXT,               -- when it was written / sent / taken, from inside the file
    author       TEXT,               -- author, sender or creator, from inside the file
    error        TEXT,               -- why it failed, or warnings for 'partial' ('; '-separated)
    started      TEXT,
    finished     TEXT
);
CREATE INDEX IF NOT EXISTS files_sha ON files(sha256);
CREATE INDEX IF NOT EXISTS files_status ON files(status);
CREATE INDEX IF NOT EXISTS files_root ON files(root_id);
CREATE INDEX IF NOT EXISTS files_path ON files(path);
CREATE INDEX IF NOT EXISTS files_size ON files(size);

-- Details of a file, one row per value: document properties, photo date / camera / GPS, title-block fields of a
-- drawing ('title_block.sheet' = 'C-104'), LandXML / JobXML summaries, sheet names. Keys are dotted lower case.
CREATE TABLE IF NOT EXISTS props (file_id TEXT NOT NULL, key TEXT NOT NULL, value TEXT);
CREATE INDEX IF NOT EXISTS props_file ON props(file_id);
CREATE INDEX IF NOT EXISTS props_key ON props(key, value);

-- The text of each file, as Markdown, compressed. A file's parts (pages, sheets, layouts, slides, messages) are
-- joined in order and stored in blocks of about 256 KB of text; each block is one compressed frame.
--   codec 'zstd': a zstd frame (with its content size); 'zlib': a zlib stream (RFC 1950); 'none': plain UTF-8.
-- A part is the UTF-8 bytes [offset, offset + length) of its block's decompressed data.
CREATE TABLE IF NOT EXISTS text_blocks (
    id       INTEGER PRIMARY KEY,
    file_id  TEXT NOT NULL,
    codec    TEXT NOT NULL,
    raw_size INTEGER NOT NULL,       -- bytes after decompression
    data     BLOB NOT NULL
);
CREATE INDEX IF NOT EXISTS text_blocks_file ON text_blocks(file_id);
CREATE TABLE IF NOT EXISTS text_parts (
    id       INTEGER PRIMARY KEY,    -- also the rowid of the part in text_search
    file_id  TEXT NOT NULL,
    seq      INTEGER NOT NULL,       -- order within the file, from 1
    label    TEXT,                   -- 'p.3', 'Sheet Budget', 'Layout C-104', 'Slide 2', 'Message 4'; NULL = whole file
    block_id INTEGER NOT NULL,
    offset   INTEGER NOT NULL,
    length   INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS text_parts_file ON text_parts(file_id, seq);
-- Full-text index of every part (rowid = text_parts.id). It keeps no copy of the text (content=''), only the words
-- and their positions, so phrase searches ("MH 3", "12 50") work. To show a hit, decompress the part.
CREATE VIRTUAL TABLE IF NOT EXISTS text_search USING fts5(
    body, content='', contentless_delete=1, tokenize='unicode61 remove_diacritics 2'
);

-- References from a file to another file: xrefs, linked images, hyperlinks, paths written in text.
CREATE TABLE IF NOT EXISTS refs (
    file_id       TEXT NOT NULL,
    root_id       TEXT NOT NULL,
    kind          TEXT NOT NULL,     -- xref, linked image, hyperlink, linked workbook, path in text, path inside file
    raw           TEXT NOT NULL,     -- as written in the file
    normalized    TEXT,              -- as a Windows path
    location      TEXT,              -- where in the file (xref name, page...)
    status        TEXT,              -- found, found by name, ambiguous, missing, support, ignored
    target_path   TEXT,              -- where it is in the job folder, relative to source_root
    target_id     TEXT,
    expected_path TEXT,              -- where it should be, when missing and the original location is known
    method        TEXT,
    candidates    TEXT               -- JSON list of paths, for ambiguous
);
CREATE INDEX IF NOT EXISTS refs_file ON refs(file_id);
CREATE INDEX IF NOT EXISTS refs_root ON refs(root_id);
CREATE INDEX IF NOT EXISTS refs_normalized ON refs(normalized);

-- Automatic connections (Project Analyzer rebuilds these on every `analyze.py intel`; others only read them).
-- JSON columns: aliases, related, participants, recipients, added, removed, evidence, refs, people, amounts, files.
CREATE TABLE IF NOT EXISTS intel_mentions (file_id TEXT, type TEXT, value TEXT, found TEXT, location TEXT, context TEXT);
CREATE TABLE IF NOT EXISTS intel_entities (id TEXT PRIMARY KEY, type TEXT, name TEXT, aliases TEXT, files INTEGER,
                                           mentions INTEGER, first_date TEXT, last_date TEXT, related TEXT);
CREATE TABLE IF NOT EXISTS intel_entity_files (entity_id TEXT, file_id TEXT, count INTEGER);
CREATE TABLE IF NOT EXISTS intel_events (date TEXT, precision TEXT, kind TEXT, confidence TEXT, title TEXT,
                                         file_id TEXT, location TEXT);
CREATE TABLE IF NOT EXISTS intel_threads (id TEXT PRIMARY KEY, title TEXT, first_date TEXT, last_date TEXT,
                                          messages INTEGER, participants TEXT);
CREATE TABLE IF NOT EXISTS intel_thread_messages (thread_id TEXT, position INTEGER, file_id TEXT, date TEXT,
                                                  sender TEXT, recipients TEXT, subject TEXT, snippet TEXT);
CREATE TABLE IF NOT EXISTS intel_versions (group_id TEXT, position INTEGER, file_id TEXT, label TEXT, similarity REAL,
                                           added TEXT, removed TEXT);
-- The latest version of each file (analyzer/series.py): files on the drive of one kind whose names are the same once
-- dates, "rev N", "current", "copy" and the like are taken out are one series (key), oldest modified first; latest = 1
-- on the one modified last. title_date and revision come from the title block when there is one. note says when the
-- highest revision in the names isn't the newest file. Every file on the drive is in a series (alone if need be).
CREATE TABLE IF NOT EXISTS intel_series (series_id TEXT, key TEXT, kind TEXT, position INTEGER, file_id TEXT, path TEXT,
                                         modified TEXT, title_date TEXT, revision TEXT, latest INTEGER, note TEXT);
CREATE TABLE IF NOT EXISTS intel_edges (src TEXT, dst TEXT, kind TEXT, weight INTEGER, first_date TEXT,
                                        last_date TEXT, evidence TEXT);
CREATE TABLE IF NOT EXISTS intel_stories (id TEXT PRIMARY KEY, title TEXT, subject TEXT, refs TEXT, first_date TEXT,
                                          last_date TEXT, files INTEGER, people TEXT, amounts TEXT);
CREATE TABLE IF NOT EXISTS intel_story_steps (story_id TEXT, position INTEGER, date TEXT, file_id TEXT, kind TEXT,
                                              who TEXT, what TEXT);
CREATE TABLE IF NOT EXISTS intel_checks (id TEXT PRIMARY KEY, severity TEXT, kind TEXT, title TEXT, detail TEXT,
                                         refs TEXT, files TEXT);

-- What was learned, by any tool. Each row records which tool wrote it; a tool changes only its own rows (except
-- answering a question, which any tool may do).
--
-- facts: one statement each. The analyzer's facts and the MCPs' notes are both facts.
--   sources: JSON list of {"file": file id or "drawing:<path>" / "tbc:<path>" / "user", "path": readable label,
--            "where"?: "p.3", "missing_here"?: true}
--   topics: JSON list ('person:John Smith', 'org:Acme Paving', 'place:Pond 2', 'doc:RFI 12', or a subject)
--   origin: JSON {"by", "knowledge_id", "id", "added"} when merged from someone else's database
CREATE TABLE IF NOT EXISTS facts (
    id             TEXT PRIMARY KEY, -- 'fact-N', N = highest + 1, never reused
    fact           TEXT NOT NULL,
    topics         TEXT NOT NULL DEFAULT '[]',
    date           TEXT,
    confidence     TEXT NOT NULL DEFAULT 'high',   -- high medium low
    key            INTEGER NOT NULL DEFAULT 0,     -- 1 = one of the facts that matter most
    supersedes     TEXT,             -- the fact this one corrects (that one stays, marked superseded)
    conflicts_with TEXT NOT NULL DEFAULT '[]',     -- JSON list of fact ids
    sources        TEXT NOT NULL DEFAULT '[]',
    tool           TEXT NOT NULL,
    author         TEXT,             -- the person, when known
    origin         TEXT,
    confirmed_by   TEXT,             -- JSON list of people who stated the same fact in a merge
    added          TEXT NOT NULL,
    updated        TEXT,
    retired        TEXT              -- set when its tool removes it: hidden, but kept and its id never reused
);
CREATE INDEX IF NOT EXISTS facts_tool ON facts(tool);

-- What each file says, written after reading it (latest row per file is the current one).
CREATE TABLE IF NOT EXISTS summaries (
    id       INTEGER PRIMARY KEY,
    file_id  TEXT NOT NULL,
    summary  TEXT NOT NULL,
    points   TEXT NOT NULL DEFAULT '[]',
    people   TEXT NOT NULL DEFAULT '[]',
    dates    TEXT NOT NULL DEFAULT '[]',
    tool     TEXT NOT NULL,
    origin   TEXT,
    added    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS summaries_file ON summaries(file_id);

CREATE TABLE IF NOT EXISTS questions (
    id          TEXT PRIMARY KEY,    -- 'q-N'
    question    TEXT NOT NULL,
    topics      TEXT NOT NULL DEFAULT '[]',
    sources     TEXT NOT NULL DEFAULT '[]',
    status      TEXT NOT NULL DEFAULT 'open',      -- open answered
    answer      TEXT,
    answered    TEXT,
    answered_by TEXT,                -- tool or person
    tool        TEXT NOT NULL,
    origin      TEXT,
    added       TEXT NOT NULL
);

-- Standing rules for this project ("The 2019 folder holds superseded drafts").
CREATE TABLE IF NOT EXISTS rules (
    id      TEXT PRIMARY KEY,        -- 'rule-N', never reused
    rule    TEXT NOT NULL,
    tool    TEXT NOT NULL,
    origin  TEXT,
    added   TEXT NOT NULL,
    updated TEXT,
    retired TEXT
);

-- Folders that belong to this job on this PC (drawings, TBC projects, documents). A drawing or TBC project belongs
-- to the job whose deepest folder contains it (case-insensitive). source_root counts as a folder too.
CREATE TABLE IF NOT EXISTS folders (path TEXT PRIMARY KEY, tool TEXT NOT NULL, added TEXT NOT NULL);

-- Merges of someone else's database into this one (kb.py merge).
CREATE TABLE IF NOT EXISTS merges (at TEXT NOT NULL, source TEXT, by TEXT, knowledge_id TEXT, counts TEXT);

-- What changed in the job folder from one run to the next (analyze.py update; analyzer/updates.py). One row per
-- file and change:
--   added      new since the last run              changed   its size or modified time changed (what changed: details)
--   removed    no longer in the folder (its knowledge is kept; props key 'gone' = when noticed)
--   moved      a removed file's content found under another path (old_path)      restored   back after being removed
-- details (JSON, changed files): {"text": {"added": lines, "removed": lines, "parts_changed": [...], "parts_added":
-- [...], "parts_removed": [...], "lines_added": ["p.2: ...", ...], "lines_removed": [...]}, "format": [sentences],
-- "title_block": ["revision B -> C"], "messages": new messages in a mailbox}. Nothing is recorded on a first run.
CREATE TABLE IF NOT EXISTS file_changes (
    id          INTEGER PRIMARY KEY,
    file_id     TEXT NOT NULL,
    path        TEXT NOT NULL,
    change      TEXT NOT NULL,
    seen        TEXT NOT NULL,       -- when the analyzer noticed it (time)
    mtime       TEXT,                -- the file's modified time (new version; removed: the last one known)
    old_path    TEXT,
    old_sha256  TEXT,
    new_sha256  TEXT,
    summary     TEXT,                -- one line: "title block revision B -> C; 12 lines added, 3 removed (p.2)"
    details     TEXT
);
CREATE INDEX IF NOT EXISTS file_changes_seen ON file_changes(seen);
CREATE INDEX IF NOT EXISTS file_changes_file ON file_changes(file_id);

-- The previous version of a changed file (its text, zlib-compressed JSON [[label, text], ...], and its format and
-- title-block details), kept only from the walk until the new version has been read and compared, then deleted.
CREATE TABLE IF NOT EXISTS file_versions (
    file_id TEXT PRIMARY KEY, path TEXT, kind TEXT, size INTEGER, mtime TEXT, sha256 TEXT, status TEXT, summary TEXT,
    inside INTEGER, text BLOB, props TEXT, saved TEXT
);

-- What happened during runs (replaces log.txt).
CREATE TABLE IF NOT EXISTS log (time TEXT NOT NULL, level TEXT NOT NULL, message TEXT NOT NULL, file_id TEXT);
