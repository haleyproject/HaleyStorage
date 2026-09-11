-- --------------------------------------------------------
-- Host:                         127.0.0.1
-- Server version:               11.8.2-MariaDB - mariadb.org binary distribution
-- Server OS:                    Win64
-- HeidiSQL Version:             12.10.0.7000
-- --------------------------------------------------------

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET NAMES utf8 */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;


-- Dumping structure for table dss_client.chunked_files
CREATE TABLE IF NOT EXISTS `chunked_files` (
  `id` bigint(20) NOT NULL COMMENT 'FK → doc_version.id. Identifies which upload session this chunk belongs to. Part of the composite PK.',
  `part` bigint(20) NOT NULL COMMENT '1-based sequential chunk number within this upload session. Part 1 is the first chunk, part N is the last. Combined with id to form the composite PK, ensuring each part number is recorded exactly once per session.',
  `size` int(11) NOT NULL DEFAULT 0 COMMENT 'Size of this individual chunk in MEGABYTES. All parts except the last are expected to match chunk_info.size. The final part may be smaller. Used for integrity checking during merge.',
  `uploaded` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'Timestamp when this specific chunk was received and written to the staging directory. Useful for diagnosing stalled uploads (e.g. if part 2 arrived but part 3 has not for 24 hours).',
  `hash` varchar(128) DEFAULT NULL COMMENT 'SHA-256 hex digest of this individual chunk, supplied by the caller at upload time. Allows per-chunk integrity verification before merge. NULL if the caller did not supply a hash for this part. Example: "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08".',
  PRIMARY KEY (`id`,`part`),
  CONSTRAINT `fk_chunked_files_doc_version` FOREIGN KEY (`id`) REFERENCES `doc_version` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Per-chunk tracking table for multi-part uploads. One row per received chunk part. Used to track upload progress, support resume (identify which parts are missing), verify per-chunk hashes, and confirm completion (row count = chunk_info.parts). Rows are cleaned up after the merge is complete and version_info.flags bit 16 (Chunked Files Deleted) is set.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.chunk_info
CREATE TABLE IF NOT EXISTS `chunk_info` (
  `id` bigint(20) NOT NULL COMMENT 'PK and FK → doc_version.id. One-to-one: only chunked doc_versions have a chunk_info row. Created at the start of a chunked upload session.',
  `size` bigint(20) NOT NULL DEFAULT 1 COMMENT 'Expected size of each individual chunk in MEGABYTES. All chunks except possibly the last one will be exactly this size. Example: size=10 means each chunk is ~10 MB. Minimum 1 (enforced by CHECK constraint).',
  `parts` int(11) NOT NULL DEFAULT 2 COMMENT 'Total number of chunk parts expected for this upload. E.g. a 95 MB file with size=10 MB → parts=10 (9 full chunks + 1 partial). Minimum 1 (enforced by CHECK constraint). When len(chunked_files rows) = parts, the upload is considered complete.',
  `name` varchar(64) NOT NULL COMMENT 'Name of the temporary staging directory that holds the chunk files. Named after the upload session, e.g. "chunks_abc123def456". Inside this directory files are named sequentially: file.mp4.001, file.mp4.002, etc.',
  `is_completed` bit(1) NOT NULL DEFAULT b'0' COMMENT 'Completion flag. 0=upload in progress (not all parts received or not yet merged). 1=all parts received AND the application has successfully merged them into the final file. Set by MarkChunkCompleted() in MariaDBIndexing.',
  `path` varchar(200) NOT NULL COMMENT 'Full path to the temporary chunk staging directory on the staging storage provider. E.g. "/staging/acme/hr/chunks_abc123/". The application streams individual chunks into files under this directory, then reads them back in order during merge.',
  PRIMARY KEY (`id`),
  KEY `idx_chunk_info_completion` (`is_completed`,`id`),
  CONSTRAINT `fk_chunk_info_doc_version` FOREIGN KEY (`id`) REFERENCES `doc_version` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `cns_chunk_info` CHECK (`parts` >= 1),
  CONSTRAINT `cns_chunk_info_0` CHECK (`size` >= 1)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Header record for a chunked-upload session. One row per doc_version that uses multi-part uploading. Tracks the expected chunk size, total part count, the staging directory name/path, and whether all parts have been merged. Paired with chunked_files which holds one row per received chunk part.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.directory
CREATE TABLE IF NOT EXISTS `directory` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Surrogate PK. Referenced by document.parent and by child directories via the parent column.',
  `display_name` varchar(120) NOT NULL COMMENT 'Human-readable folder label displayed in the UI. May contain spaces and mixed case, e.g. "Project Documents" or "Year 2024 Invoices".',
  `created` timestamp NOT NULL DEFAULT current_timestamp() COMMENT 'Timestamp when the directory record was first created.',
  `modified` timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'Automatically updated whenever any column in this row changes.',
  `cuid` varchar(48) NOT NULL DEFAULT uuid() COMMENT 'Collision-resistant unique identifier for this directory. Stable across renames. Used by the application layer as an external-safe reference (avoids exposing numeric auto-increment IDs in APIs).',
  `delete_state` tinyint(4) NOT NULL DEFAULT 0 COMMENT 'Lifecycle state. 0=active, 1=soft deleted, 2=archived, 3=purged.',
  `deleted` datetime DEFAULT NULL COMMENT 'UTC timestamp when this row entered a non-active delete_state. NULL while active.',
  `name` varchar(120) NOT NULL COMMENT 'Normalised folder slug used in uniqueness checks and path construction, e.g. "project_docs" or "invoices". Should be lowercase and URL-safe. Different from display_name which may have spaces.',
  `parent` bigint(20) NOT NULL DEFAULT 0 COMMENT 'FK → directory.id of the parent folder. Set to 0 for top-level (root) directories in the workspace — the application treats 0 as the sentinel for "no parent". Checked against (workspace, parent, name) unique constraint.',
  `workspace` bigint(20) NOT NULL COMMENT 'FK → workspace.id. Every directory belongs to exactly one workspace. A directory created under workspace=12 cannot contain documents from workspace=15.',
  `actor` bigint(20) NOT NULL DEFAULT 0,
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_directory` (`workspace`,`parent`,`name`),
  UNIQUE KEY `unq_directory_0` (`cuid`),
  KEY `idx_directory_parent` (`parent`),
  KEY `idx_directory_workspace_parent_delete_state` (`workspace`,`parent`,`delete_state`),
  CONSTRAINT `fk_directory_workspace` FOREIGN KEY (`workspace`) REFERENCES `workspace` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1000 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Virtual folder hierarchy within a workspace. Directories are logical — they exist in the DB only and do not map to physical filesystem folders. Supports soft-delete, parent-child nesting (parent=0 for root), and CUID-based external referencing.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.document
CREATE TABLE IF NOT EXISTS `document` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Surrogate PK. Referenced by doc_version.parent, doc_info.file, and other version-level tables.',
  `cuid` varchar(48) NOT NULL DEFAULT uuid() COMMENT 'Collision-resistant unique identifier for this document. Stable across version uploads and renames. Safe to expose in external APIs. Example: "2b3a8f1c9e4d7a0b6c5e2f3d8a1b4c7e".',
  `created` timestamp NOT NULL DEFAULT current_timestamp() COMMENT 'Timestamp when the document record was first created (i.e. when the first version was uploaded).',
  `modified` timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'Automatically updated whenever any column in this row changes (e.g. when the document is moved or soft-deleted).',
  `parent` bigint(20) NOT NULL COMMENT 'FK → directory.id. The virtual folder this document lives in. Changing this value moves the document to a different folder.',
  `name` bigint(20) NOT NULL COMMENT 'FK → name_store.id. Represents the logical filename (stem + extension). E.g. name_store id=7 maps to "invoice_jan_2024.pdf". Do not confuse with a varchar name — this is a numeric FK.',
  `original_name` bigint(20) DEFAULT NULL COMMENT 'FK → name_store.id. Preserves the original logical filename when a deleted document is tombstoned to free the active (parent,name) slot for a re-upload. NULL while the document still owns its active name.',
  `delete_state` tinyint(4) NOT NULL DEFAULT 0 COMMENT 'Lifecycle state. 0=active, 1=soft deleted, 2=archived, 3=purged.',
  `deleted` datetime DEFAULT NULL COMMENT 'UTC timestamp when this row entered a non-active delete_state. NULL while active.',
  `workspace` bigint(20) NOT NULL COMMENT 'FK → workspace.id. Redundantly stored here (parent directory already implies a workspace) for fast workspace-scoped queries without joining through directory.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_file_index` (`cuid`),
  UNIQUE KEY `unq_document` (`parent`,`name`),
  KEY `fk_file_index_parent` (`workspace`),
  KEY `fk_document_directory` (`parent`),
  KEY `fk_document_name_store` (`name`),
  KEY `fk_document_original_name_store` (`original_name`),
  KEY `idx_document_workspace_parent_delete_state` (`workspace`,`parent`,`delete_state`),
  CONSTRAINT `fk_document_directory` FOREIGN KEY (`parent`) REFERENCES `directory` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_document_name_store` FOREIGN KEY (`name`) REFERENCES `name_store` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_document_original_name_store` FOREIGN KEY (`original_name`) REFERENCES `name_store` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_document_workspace` FOREIGN KEY (`workspace`) REFERENCES `workspace` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1988 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Logical file identity. One row per unique filename within a directory/workspace combination. Does not store the filename string directly — uses name_store FK for normalised filename lookup. Multiple uploads of the same file create new doc_version rows, not new document rows.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.doc_info
CREATE TABLE IF NOT EXISTS `doc_info` (
  `file` bigint(20) NOT NULL COMMENT 'PK and FK → document.id. One-to-one optional relationship — insert a row here to attach a custom display name to the document. Omit to use the raw filename derived from name_store.',
  `display_name` varchar(200) NOT NULL COMMENT 'Human-readable label for the document. Free-form text, may include spaces, punctuation, and Unicode. E.g. "Q1 2024 Invoice – ACME Corp" or "Employee Handbook v3 (Draft)". Written by the application; not auto-derived.',
  `metadata` text DEFAULT NULL COMMENT 'to store document level metadata',
  `actor` bigint(20) NOT NULL DEFAULT 0,
  PRIMARY KEY (`file`),
  KEY `idx_doc_info` (`display_name`),
  CONSTRAINT `fk_file_info_file_index_0` FOREIGN KEY (`file`) REFERENCES `document` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Optional display name override for a document. When present, the application shows display_name instead of the raw filename derived from name_store. Inserted/updated by UpdateDocDisplayName(). Not every document has a row here.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.doc_version
CREATE TABLE IF NOT EXISTS `doc_version` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Surrogate PK. Used as the FK anchor for version_info, chunk_info, and chunked_files. Also returned to callers as the "versionId" in upload responses.',
  `cuid` varchar(48) NOT NULL DEFAULT uuid() COMMENT 'Collision-resistant unique identifier for this specific version. Stable and safe for external API exposure. Distinct from the parent document CUID. Example: "f3a8b2c1d9e4a7b0c6d5e3f2a8b1c4d7".',
  `created` timestamp NOT NULL DEFAULT current_timestamp() COMMENT 'Timestamp when this version record was created, i.e. when the upload began. For placeholder uploads this marks when the DB record was reserved, not when the file bytes arrived.',
  `ver` int(11) NOT NULL DEFAULT 1 COMMENT 'Monotonically increasing version number within the parent document. Starts at 1 for the first upload. The (parent, ver) unique constraint prevents gaps or duplicates. The latest version is MAX(ver) for a given parent.',
  `parent` bigint(20) NOT NULL COMMENT 'FK → document.id. Links this version to its parent logical document.',
  `sub_ver` int(11) NOT NULL DEFAULT 0 COMMENT 'sub version gives the files'' own content chain.. 0 is alwasy the main file.. rest are all supporting files.. and thes upporting files never get same extension as the main file.. for example.. if the main content is pdf.. we can still have the sub_version as png or jpeg.. main idea is these sub version files are used sa thumbnails.. \ninteresting idea is one file can have multiple thumbnails and it can be used sa ''rotating thumbnails'' so gives user an animated experience..',
  `delete_state` tinyint(4) NOT NULL DEFAULT 0 COMMENT 'Lifecycle state for this version row. 0=active, 1=soft deleted, 2=archived, 3=purged.',
  `deleted` datetime DEFAULT NULL COMMENT 'UTC timestamp when this version row entered a non-active delete_state. NULL while active.',
  `actor` bigint(20) NOT NULL DEFAULT 0,
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_doc_version` (`cuid`),
  UNIQUE KEY `unq_file_version` (`parent`,`ver`,`sub_ver`),
  KEY `idx_file_version_0` (`created`),
  KEY `idx_doc_version_parent_delete_state_subver_ver` (`parent`,`delete_state`,`sub_ver`,`ver`),
  KEY `idx_doc_version_parent_ver_delete_state_subver` (`parent`,`ver`,`delete_state`,`sub_ver`),
  CONSTRAINT `fk_doc_version_document` FOREIGN KEY (`parent`) REFERENCES `document` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1996 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='One row per file upload event. A document accumulates version rows over time (ver=1, 2, 3...). The version row is the FK anchor for all physical storage metadata (version_info) and chunking state (chunk_info, chunked_files).';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.extension
CREATE TABLE IF NOT EXISTS `extension` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Surrogate PK. Referenced by name_store.extension.',
  `name` varchar(100) NOT NULL COMMENT 'File extension without the leading dot, always lowercase. E.g. "pdf", "mp4", "docx", "jpg". Normalised by the application before insert.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_extension` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=1000 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Deduplicated registry of file extensions (lowercase, no dot). Paired with vault via name_store to form a full logical filename. Allows efficient type-based queries without scanning varchar filename columns.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.name_store
CREATE TABLE IF NOT EXISTS `name_store` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Surrogate PK. Referenced by document.name.',
  `extension` int(11) NOT NULL COMMENT 'FK → extension.id. Identifies the file type half of the logical filename (e.g. "pdf").',
  `name` bigint(20) NOT NULL COMMENT 'FK → vault.id. Identifies the stem half of the logical filename (e.g. "invoice_jan_2024").',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_name_store` (`name`,`extension`),
  KEY `idx_name_store_0` (`extension`,`name`),
  CONSTRAINT `fk_name_store_extension` FOREIGN KEY (`extension`) REFERENCES `extension` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_name_store_name_vault` FOREIGN KEY (`name`) REFERENCES `vault` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=900 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Normalised filename registry. Each row represents one unique (stem, extension) pair, e.g. ("invoice_jan_2024", "pdf"). document.name is a FK to this table, keeping the full filename string out of the document row.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.vault
CREATE TABLE IF NOT EXISTS `vault` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Surrogate PK. Referenced by name_store.name.',
  `name` varchar(200) NOT NULL COMMENT 'Bare filename stem without extension, e.g. "invoice_jan_2024" or "profile_photo". Normalised by the application before insert (trimmed, lowercased where appropriate).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_vault_name` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=500 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Deduplicated table of bare filename stems (no extension). Combined with the extension table via name_store to form a complete logical filename. Avoids repeating long filename strings across millions of document rows.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.version_info
CREATE TABLE IF NOT EXISTS `version_info` (
  `id` bigint(20) NOT NULL COMMENT 'PK and FK → doc_version.id. One-to-one mandatory relationship: every doc_version must have a version_info row.',
  `storage_name` varchar(200) NOT NULL COMMENT 'Provider-level identifier for the file, used to build the shard path or object key. For FileSystem: a GUID or numeric ID, e.g. "2b4f8a1c3d9e7b0a". For cloud providers: may be a service-assigned reference. Combined with client/module/workspace path segments to locate the actual file bytes.',
  `storage_ref` text NOT NULL COMMENT 'Full physical location of the file. FileSystem: absolute path, e.g. "/vault/acme/hr/general/f/2b4/2b4f8a1c.pdf". Cloud: object key, e.g. "acme/hr/general/2b4f8a1c.pdf". Empty string for Placeholder records (flags=256) until FinalizePlaceholder() is called.',
  `staging_ref` varchar(300) DEFAULT NULL COMMENT 'Full path or object key of the file in the staging provider (fast/temporary store), if staging is configured. Same format as storage_ref but points to the staging location. NULL when no staging provider is in use. Cleared after the file is promoted to primary storage and the staging copy is deleted.',
  `size` bigint(20) NOT NULL DEFAULT 0 COMMENT 'File size in BYTES. 0 for Placeholder records until finalization. For chunked uploads, set to the total assembled file size after all chunks are merged. Example: a 250 MB file → size=262144000.',
  `metadata` text DEFAULT NULL COMMENT 'Arbitrary caller-supplied JSON metadata attached to this version. The DB imposes no schema. Example: {"source":"scanner","department":"finance","approved_by":"jdoe@acme.com"}. NULL if the caller did not supply metadata.',
  `flags` int(11) NOT NULL DEFAULT 0 COMMENT 'Bitmask tracking the lifecycle state of this version.\n0 - None\n1 - Chunked-Upload Mode (Marked)\n2 - Uploaded to Chunking Area (Chunks are always deleted upon moving out)\n4 - Uploaded to Staging Area (Optional)\n8 - Uploaded to Storage Area (Final)\n16 - Deleted Chunked Files (Mandatory)\n32 - Deleted Staging Copy (Optional)\n64 - Upload Process Completed\n128 - Synced to Internal Storage (was ExternalTemp, now pulled to local disk)\n256 - PlaceHolder (DB Reserved, no content copied yet)',
  `hash` varchar(128) DEFAULT NULL COMMENT 'SHA-256 hex digest of the fully assembled final file, used for integrity verification and sync deduplication. Example: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855". NULL until the file is fully assembled and hashed (chunked uploads hash after merge; direct uploads hash during write).',
  `synced_at` timestamp NULL DEFAULT NULL COMMENT 'Timestamp set by the background sync worker when a file that was initially in external/cloud-only storage is successfully pulled to local internal disk (flag 128 set). NULL means either the file was always stored internally and never needed syncing, or the sync has not completed yet.',
  `profile_info_id` bigint(20) DEFAULT NULL COMMENT 'if null, then we fall back to using the module''s default current profile.. However, current profile could not be the correct one, may be the current profile got modified or changed .. May be we didn''t use the module''s profile, we used the workspace''s profile.. or a workspace profile was addedin between.. so, having the profile_info_id  here is the best option to properly resolve the correct storage path.',
  PRIMARY KEY (`id`),
  KEY `idx_version_info` (`storage_name`),
  KEY `idx_version_info_profile_info` (`profile_info_id`),
  CONSTRAINT `fk_version_info_doc_version` FOREIGN KEY (`id`) REFERENCES `doc_version` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Physical storage metadata for one document version. Stores WHERE the file is (storage_ref, staging_ref), HOW BIG it is (size), WHAT IT IS (hash), and WHAT STATE it is in (flags bitmask). The flags column is the authoritative lifecycle tracker — read it to determine whether a file is a placeholder, in staging, in primary storage, or fully completed.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.dir_path
CREATE TABLE IF NOT EXISTS `dir_path` (
  `ancestor` bigint(20) NOT NULL COMMENT 'Ancestor directory id. Self row has ancestor=descendant.',
  `descendant` bigint(20) NOT NULL COMMENT 'Descendant directory id. Self row has depth=0.',
  `depth` int(11) NOT NULL DEFAULT 0 COMMENT '0=self, 1=direct child, N=deeper descendant.',
  PRIMARY KEY (`ancestor`,`descendant`),
  KEY `idx_dir_path_descendant_depth` (`descendant`,`depth`),
  CONSTRAINT `fk_dir_path_ancestor` FOREIGN KEY (`ancestor`) REFERENCES `directory` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_dir_path_descendant` FOREIGN KEY (`descendant`) REFERENCES `directory` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Closure table for logical folders. Used for recursive stats and future directory move operations.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.stat
CREATE TABLE IF NOT EXISTS `stat` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT COMMENT 'Surrogate key for one reusable statistics payload.',
  `active_folders` bigint(20) NOT NULL DEFAULT 0,
  `deleted_folders` bigint(20) NOT NULL DEFAULT 0,
  `active_docs` bigint(20) NOT NULL DEFAULT 0,
  `deleted_docs` bigint(20) NOT NULL DEFAULT 0,
  `active_versions` bigint(20) NOT NULL DEFAULT 0,
  `deleted_versions` bigint(20) NOT NULL DEFAULT 0,
  `active_thumbs` bigint(20) NOT NULL DEFAULT 0,
  `deleted_thumbs` bigint(20) NOT NULL DEFAULT 0,
  `active_bytes` bigint(20) NOT NULL DEFAULT 0,
  `deleted_bytes` bigint(20) NOT NULL DEFAULT 0,
  `archived_bytes` bigint(20) NOT NULL DEFAULT 0,
  `purged_bytes` bigint(20) NOT NULL DEFAULT 0,
  `modified` timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB AUTO_INCREMENT=1988 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Reusable cached statistics payload. Ownership is held by one stat connection row.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.node_stat
CREATE TABLE IF NOT EXISTS `node_stat` (
  `node_type` tinyint(4) NOT NULL COMMENT '1=workspace, 2=directory.',
  `node_id` bigint(20) NOT NULL COMMENT 'workspace.id when node_type=1; directory.id when node_type=2.',
  `workspace` bigint(20) NOT NULL COMMENT 'Owning workspace id for quick filtering.',
  `stat` bigint(20) NOT NULL COMMENT 'FK to stat.id containing the cached counters.',
  PRIMARY KEY (`node_type`,`node_id`),
  UNIQUE KEY `unq_node_stat_stat` (`stat`),
  KEY `idx_node_stat_workspace` (`workspace`),
  CONSTRAINT `fk_node_stat_stat` FOREIGN KEY (`stat`) REFERENCES `stat` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Connects a direct workspace or directory node to its statistics payload.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.tree_stat
CREATE TABLE IF NOT EXISTS `tree_stat` (
  `node_type` tinyint(4) NOT NULL COMMENT '1=workspace, 2=directory.',
  `node_id` bigint(20) NOT NULL COMMENT 'workspace.id when node_type=1; directory.id when node_type=2.',
  `workspace` bigint(20) NOT NULL COMMENT 'Owning workspace id for quick filtering.',
  `stat` bigint(20) NOT NULL COMMENT 'FK to stat.id containing the cached counters.',
  PRIMARY KEY (`node_type`,`node_id`),
  UNIQUE KEY `unq_tree_stat_stat` (`stat`),
  KEY `idx_tree_stat_workspace` (`workspace`),
  CONSTRAINT `fk_tree_stat_stat` FOREIGN KEY (`stat`) REFERENCES `stat` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Connects a recursive workspace or directory tree to its statistics payload.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.node_ext_stat
CREATE TABLE IF NOT EXISTS `node_ext_stat` (
  `node_type` tinyint(4) NOT NULL COMMENT '1=workspace, 2=directory.',
  `node_id` bigint(20) NOT NULL,
  `workspace` bigint(20) NOT NULL,
  `ext` varchar(100) NOT NULL COMMENT 'Normalized extension from storage/name metadata, including dot when present.',
  `stat` bigint(20) NOT NULL COMMENT 'FK to stat.id containing the cached counters.',
  PRIMARY KEY (`node_type`,`node_id`,`ext`),
  UNIQUE KEY `unq_node_ext_stat_stat` (`stat`),
  KEY `idx_node_ext_stat_workspace_ext` (`workspace`,`ext`),
  CONSTRAINT `fk_node_ext_stat_stat` FOREIGN KEY (`stat`) REFERENCES `stat` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Connects a direct node extension to its statistics payload.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.tree_ext_stat
CREATE TABLE IF NOT EXISTS `tree_ext_stat` (
  `node_type` tinyint(4) NOT NULL COMMENT '1=workspace, 2=directory.',
  `node_id` bigint(20) NOT NULL,
  `workspace` bigint(20) NOT NULL,
  `ext` varchar(100) NOT NULL COMMENT 'Normalized extension from storage/name metadata, including dot when present.',
  `stat` bigint(20) NOT NULL COMMENT 'FK to stat.id containing the cached counters.',
  PRIMARY KEY (`node_type`,`node_id`,`ext`),
  UNIQUE KEY `unq_tree_ext_stat_stat` (`stat`),
  KEY `idx_tree_ext_stat_workspace_ext` (`workspace`,`ext`),
  CONSTRAINT `fk_tree_ext_stat_stat` FOREIGN KEY (`stat`) REFERENCES `stat` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Connects a recursive tree extension to its statistics payload.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.stat_evt
CREATE TABLE IF NOT EXISTS `stat_evt` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT,
  `event_key` varchar(180) NOT NULL COMMENT 'Idempotency key for this stats mutation.',
  `event_type` tinyint(4) NOT NULL COMMENT '1=upload, 2=delete, 3=restore, 4=archive, 5=purge, 6=folder-create, 7=file-move, 8=folder-move.',
  `node_type` tinyint(4) NOT NULL COMMENT '1=workspace, 2=directory.',
  `node_id` bigint(20) NOT NULL COMMENT 'Direct node receiving the delta.',
  `workspace` bigint(20) NOT NULL COMMENT 'Owning workspace id.',
  `document` bigint(20) DEFAULT NULL,
  `version` bigint(20) DEFAULT NULL,
  `ext` varchar(100) DEFAULT NULL,
  `active_folders_delta` bigint(20) NOT NULL DEFAULT 0,
  `deleted_folders_delta` bigint(20) NOT NULL DEFAULT 0,
  `active_docs_delta` bigint(20) NOT NULL DEFAULT 0,
  `deleted_docs_delta` bigint(20) NOT NULL DEFAULT 0,
  `active_versions_delta` bigint(20) NOT NULL DEFAULT 0,
  `deleted_versions_delta` bigint(20) NOT NULL DEFAULT 0,
  `active_thumbs_delta` bigint(20) NOT NULL DEFAULT 0,
  `deleted_thumbs_delta` bigint(20) NOT NULL DEFAULT 0,
  `active_bytes_delta` bigint(20) NOT NULL DEFAULT 0,
  `deleted_bytes_delta` bigint(20) NOT NULL DEFAULT 0,
  `archived_bytes_delta` bigint(20) NOT NULL DEFAULT 0,
  `purged_bytes_delta` bigint(20) NOT NULL DEFAULT 0,
  `created` timestamp NOT NULL DEFAULT current_timestamp(),
  `processed` datetime DEFAULT NULL,
  `message` varchar(300) DEFAULT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_stat_evt_key` (`event_key`),
  KEY `idx_stat_evt_processed_id` (`processed`,`id`),
  KEY `idx_stat_evt_workspace_node` (`workspace`,`node_type`,`node_id`),
  KEY `idx_stat_evt_document` (`document`),
  KEY `idx_stat_evt_version` (`version`),
  KEY `idx_stat_evt_ext` (`ext`)
) ENGINE=InnoDB AUTO_INCREMENT=1988 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Durable stats event queue. Mutations enqueue deltas in the same transaction; a worker folds them into stat tables.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.stat_run
CREATE TABLE IF NOT EXISTS `stat_run` (
  `id` bigint(20) NOT NULL AUTO_INCREMENT,
  `run_type` varchar(40) NOT NULL COMMENT 'process, rebuild, backfill.',
  `status` varchar(30) NOT NULL COMMENT 'started, completed, failed.',
  `started` datetime NOT NULL DEFAULT current_timestamp(),
  `completed` datetime DEFAULT NULL,
  `processed` bigint(20) NOT NULL DEFAULT 0,
  `message` varchar(500) DEFAULT NULL,
  PRIMARY KEY (`id`),
  KEY `idx_stat_run_type_status_started` (`run_type`,`status`,`started`)
) ENGINE=InnoDB AUTO_INCREMENT=2006 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Operational history for stats processing and exact rebuilds.';

-- Data exporting was unselected.

-- Dumping structure for table dss_client.workspace
CREATE TABLE IF NOT EXISTS `workspace` (
  `id` bigint(20) NOT NULL COMMENT 'Primary key — mirrors dsscore.workspace.id exactly. Set by the application at registration time; never AUTO_INCREMENT.',
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Anchor table: one row per registered workspace in this module. Mirrors dsscore.workspace.id so directories and documents can reference a workspace via FK without cross-database joins.';

-- Data exporting was unselected.

/*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;
/*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;
/*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;
