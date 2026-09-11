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


-- Dumping structure for table dss_core.client
CREATE TABLE IF NOT EXISTS `client` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Auto-increment surrogate key.',
  `name` varchar(100) NOT NULL COMMENT 'Machine-usable slug; lowercase-normalized (e.g. "myapp", "hr_portal"). Unique across all clients. Used as one input to derive the deterministic CUID and the on-disk directory name. Never changes after registration — renaming requires a full migration.',
  `display_name` varchar(100) NOT NULL COMMENT 'Human-readable label shown in UIs and logs (e.g. "HR Portal", "Customer Docs Service"). Has no effect on path computation or CUID derivation.',
  `guid` varchar(42) NOT NULL COMMENT 'CUID generated from the name. Own name hash (not hierarchy aware)',
  `created` timestamp NOT NULL DEFAULT current_timestamp() COMMENT 'Row creation timestamp (UTC).',
  `modified` timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'Automatically updated whenever any column changes.',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_client` (`name`),
  UNIQUE KEY `unq_client_1` (`guid`)
) ENGINE=InnoDB AUTO_INCREMENT=1975 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Top-level tenant / application that owns the entire storage hierarchy.\nExample: an application called "hr_portal" is registered as a client. Under it live modules such as "employee_docs" and "avatars".\nThe client name is hashed (SHA-256, compact-N GUID) at runtime to produce a CUID that drives all internal routing — no database round-trip is needed once the client is cached at startup.\nEvery file ultimately belongs to a client → module → workspace chain.';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.client_keys
CREATE TABLE IF NOT EXISTS `client_keys` (
  `client` int(11) NOT NULL COMMENT 'FK to client.id. One key bundle per client — the primary key doubles as the foreign key.',
  `signing` varchar(300) NOT NULL COMMENT 'Server-generated random 512-character string used to sign JWTs and access tokens issued to this client. Rotated by re-registering the client with a new password.',
  `encrypt` varchar(300) NOT NULL COMMENT 'Server-generated random 512-character string used for symmetric encryption of payload data scoped to this client (e.g. encrypted cookie values, sealed metadata blobs).',
  `password` varchar(120) NOT NULL COMMENT 'SHA-256 hash of the client plaintext password. The plaintext is never stored. Verification hashes the candidate and compares it to this value.',
  PRIMARY KEY (`client`),
  CONSTRAINT `fk_client_keys_client` FOREIGN KEY (`client`) REFERENCES `client` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Security credentials for each client — hashed password plus server-generated signing and encryption keys.\nSeparated from the client table so that credential rotation (key refresh, password change) does not touch the client identity row and does not affect any downstream FKs.\nExample: when "hr_portal" is registered, a random 512-char signing key and encrypt key are generated and stored here alongside the SHA-256 of the provided password.';

-- Data exporting was unselected.

-- Dumping structure for procedure dss_core.DropDatabasesWithPrefix
DROP PROCEDURE IF EXISTS `DropDatabasesWithPrefix`;
DELIMITER //
CREATE PROCEDURE `DropDatabasesWithPrefix`(IN prefix VARCHAR(100))
BEGIN
  DECLARE done INT DEFAULT FALSE;
  DECLARE db_name VARCHAR(255);
  DECLARE cur CURSOR FOR
    SELECT SCHEMA_NAME
    FROM INFORMATION_SCHEMA.SCHEMATA
    WHERE SCHEMA_NAME LIKE CONCAT(prefix, '%')
      AND SCHEMA_NAME NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys');
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET done = TRUE;

  IF prefix IS NULL OR CHAR_LENGTH(TRIM(prefix)) < 3 THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'prefix must be at least 3 characters — refusing to run without a specific prefix.';
  END IF;

  OPEN cur;

  read_loop: LOOP
    FETCH cur INTO db_name;
    IF done THEN
      LEAVE read_loop;
    END IF;
    SET @drop_stmt = CONCAT('DROP DATABASE `', db_name, '`');
    PREPARE stmt FROM @drop_stmt;
    EXECUTE stmt;
    DEALLOCATE PREPARE stmt;
  END LOOP;

  CLOSE cur;
END//
DELIMITER ;

-- Dumping structure for procedure dss_core.FixCollationsWithPrefix
DROP PROCEDURE IF EXISTS `FixCollationsWithPrefix`;
DELIMITER //
CREATE PROCEDURE `FixCollationsWithPrefix`(IN prefix VARCHAR(100))
BEGIN
  DECLARE done INT DEFAULT FALSE;
  DECLARE v_schema VARCHAR(255);
  DECLARE v_table  VARCHAR(255);
  DECLARE cur CURSOR FOR
    SELECT TABLE_SCHEMA, TABLE_NAME
    FROM   INFORMATION_SCHEMA.TABLES
    WHERE  TABLE_SCHEMA LIKE CONCAT(prefix, '%')
      AND  TABLE_SCHEMA NOT IN ('mysql', 'information_schema', 'performance_schema', 'sys')
      AND  TABLE_COLLATION LIKE 'latin1%'
      AND  TABLE_TYPE = 'BASE TABLE';
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET done = TRUE;

  IF prefix IS NULL OR CHAR_LENGTH(TRIM(prefix)) < 3 THEN
    SIGNAL SQLSTATE '45000'
      SET MESSAGE_TEXT = 'prefix must be at least 3 characters — refusing to run without a specific prefix.';
  END IF;

  OPEN cur;

  fix_loop: LOOP
    FETCH cur INTO v_schema, v_table;
    IF done THEN LEAVE fix_loop; END IF;
    SET @fix_stmt = CONCAT(
      'ALTER TABLE `', v_schema, '`.`', v_table,
      '` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci'
    );
    PREPARE stmt FROM @fix_stmt;
    EXECUTE stmt;
    DEALLOCATE PREPARE stmt;
  END LOOP;

  CLOSE cur;
END//
DELIMITER ;

-- Dumping structure for table dss_core.module
CREATE TABLE IF NOT EXISTS `module` (
  `parent` int(11) NOT NULL COMMENT 'FK to client.id. Every module belongs to exactly one client.',
  `guid` varchar(42) NOT NULL COMMENT 'CUID generated from the name. Own name hash (not hierarchy aware)',
  `cuid` varchar(48) NOT NULL COMMENT 'Deterministic compact-N GUID derived from SHA-256(client_name##module_name). Used as the per-module MariaDB schema name prefix (e.g. "dss_module_<cuid>"). Stable for as long as the client and module names do not change.',
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Auto-increment surrogate key.',
  `name` varchar(120) NOT NULL COMMENT 'Machine-usable slug (e.g. "documents", "invoices", "avatars"). Combined with the parent client name to derive the CUID. Unique per client.',
  `display_name` varchar(120) NOT NULL COMMENT 'Human-readable label (e.g. "Document Archive", "Invoice Store"). No effect on routing or path computation.',
  `active` bit(1) NOT NULL DEFAULT b'1' COMMENT '1 = module is active and accepts new uploads. 0 = soft-disabled; existing files remain readable but new uploads are rejected.',
  `created` datetime NOT NULL DEFAULT current_timestamp() COMMENT 'Row creation timestamp (UTC).',
  `modified` timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'Automatically updated whenever any column changes.',
  `storage_profile` int(11) DEFAULT NULL COMMENT 'Optional FK to profile_info.id. When set, all workspaces under this module default to the specified provider and upload mode unless they override it with their own storage_profile. NULL means use the StorageCoordinator default (typically the local filesystem provider).',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_directory_01` (`parent`,`name`),
  UNIQUE KEY `unq_module` (`parent`,`guid`),
  UNIQUE KEY `unq_module_0` (`cuid`),
  KEY `fk_module_profile` (`storage_profile`),
  CONSTRAINT `fk_direcory_client` FOREIGN KEY (`parent`) REFERENCES `client` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_module_profile_info` FOREIGN KEY (`storage_profile`) REFERENCES `profile_info` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1966 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='A logical feature area or application section inside a client (e.g. "documents", "invoices", "avatars").\nEach module gets its own isolated MariaDB schema named dss_module_<cuid> so file indexes for different feature areas are fully separated — a query for "invoices" never touches the "avatars" database.\nExample: client "hr_portal" → module "employee_docs". All files under this module are indexed in the "dss_module_<employee_docs_cuid>" database.\nThe storage_profile column points to the cloud/FS provider configuration to use for uploads at the module level. Individual workspaces may override this with their own storage_profile.';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.profile
CREATE TABLE IF NOT EXISTS `profile` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Auto-increment surrogate key.',
  `name` varchar(120) NOT NULL COMMENT 'Human-readable name for this profile group (e.g. "prod-b2", "staging-fs", "azure-east-tier1"). Groups one or more versioned profile_info rows under a single logical label.',
  `display_name` varchar(120) NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_profile` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=2655 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='A named storage configuration group. Each profile can have multiple versioned profile_info rows to support side-by-side configurations during migrations.\nA profile defines WHAT to call a configuration; the actual settings (provider, mode, metadata) live in profile_info.\nExample: a profile named "prod-b2" might have version 1 pointing to Backblaze B2 us-west and version 2 pointing to B2 eu-central — useful when migrating buckets without downtime.';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.profile_info
CREATE TABLE IF NOT EXISTS `profile_info` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Auto-increment surrogate key. This id is stored in workspace.storage_profile and module.storage_profile to activate this configuration for a workspace or module.',
  `profile` int(11) NOT NULL COMMENT 'FK to profile.id. Groups this versioned configuration under a named profile.',
  `version` int(11) NOT NULL DEFAULT 1 COMMENT 'Integer version of this configuration starting at 1, incremented on each change. The (profile, version) pair is unique — allows creating a new version before cutting over, supporting zero-downtime migrations.',
  `mode` int(11) NOT NULL DEFAULT 0 COMMENT 'Upload routing mode — determines what happens immediately after a file is received:\n0 - DirectSave: write directly to the primary storage provider and mark the upload complete right away. Simplest and fastest; suitable when the primary provider is always available and reachable.\n1 - StageAndMove: write to the staging provider first (flag=4, InStaging), then a background worker promotes the file to primary storage and removes the staging copy. Use this when the primary provider is slow, remote, or should receive files in bulk.\n2 - StageAndRetainCopy: write to both staging and primary simultaneously; the staging copy is kept for redundancy or CDN acceleration.',
  `storage_provider` int(10) unsigned DEFAULT NULL COMMENT 'FK to provider.id. The primary permanent storage destination for uploads (e.g. Backblaze B2, AWS S3, local filesystem). NULL means use the StorageCoordinator default registered provider.',
  `staging_provider` int(10) unsigned DEFAULT NULL COMMENT 'FK to provider.id. Temporary fast-landing zone used before promotion to primary (mode 1 or 2). Typically a local filesystem or a cheap/fast cloud bucket. NULL means no staging.',
  `created` timestamp NOT NULL DEFAULT current_timestamp() COMMENT 'Row creation timestamp (UTC).',
  `metadata` text NOT NULL COMMENT 'JSON blob carrying provider-specific connection configuration required to initialise the provider at runtime.\nFor cloud providers this typically includes: bucket name, region, endpoint URL, and a reference to the credential (e.g. an env-var name or secrets-manager key).\nExample for S3: {"bucket":"my-vault","region":"us-east-1","endpoint":"https://s3.amazonaws.com","credential_ref":"env:AWS_SECRET_KEY"}\nExample for B2: {"bucketId":"abc123","bucketName":"my-vault","applicationKeyId":"key001","applicationKey":"env:B2_APP_KEY"}\nThe StorageCoordinator reads this JSON at startup when initialising the provider connection.',
  `hash` varchar(48) NOT NULL COMMENT 'Deterministric guid created from the sha256 hash of the metadata, storage key, staging key, mode which uniquely identifies if this is alreday created or not..',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_profile_config` (`profile`,`version`),
  UNIQUE KEY `unq_profile_info_hash` (`hash`),
  KEY `fk_profile_info_provider` (`storage_provider`),
  KEY `fk_profile_info_provider_0` (`staging_provider`),
  CONSTRAINT `fk_profile_config_profile` FOREIGN KEY (`profile`) REFERENCES `profile` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_profile_info_provider` FOREIGN KEY (`storage_provider`) REFERENCES `provider` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_profile_info_provider_0` FOREIGN KEY (`staging_provider`) REFERENCES `provider` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=3425 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='One versioned configuration for a storage profile — links a profile to a primary provider, an optional staging provider, an upload mode, and JSON connection metadata.\nAssigning a profile_info.id to a workspace.storage_profile (or module.storage_profile) activates that configuration for all uploads into that scope.\nExample: profile "prod-b2" version 1 — mode=StageAndMove, storage_provider=B2-us-west, staging_provider=local-fs, metadata={"bucket":"vault-prod",...}. When a file is uploaded to any workspace pointing at this profile_info, it first lands on local-fs, then a background job moves it to the B2 bucket.';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.provider
CREATE TABLE IF NOT EXISTS `provider` (
  `id` int(10) unsigned NOT NULL AUTO_INCREMENT COMMENT 'Auto-increment surrogate key.',
  `name` varchar(120) DEFAULT NULL COMMENT 'Registration key used by the StorageCoordinator to look up this provider at runtime (e.g. "hfs" for the default Haley FileSystem provider, "b2-prod", "s3-east-1", "azure-blob-tier1"). Must match the key passed to AddProvider() at application startup.',
  `created` timestamp NOT NULL DEFAULT current_timestamp() COMMENT 'Row creation timestamp (UTC).',
  `description` text DEFAULT NULL COMMENT 'Free-text description for operators (e.g. "Backblaze B2 production bucket — us-west-004, 1 TB allocated, auto-delete after 7 years").',
  `display_name` varchar(120) NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_provider` (`name`)
) ENGINE=InnoDB AUTO_INCREMENT=2245 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='A named storage backend — the HOW of file storage, not the WHERE.\nExamples: "hfs" = Haley FileSystem (default local disk provider), "b2" = Backblaze B2, "s3-east" = AWS S3 us-east-1, "azure-blob" = Azure Blob Storage.\nThe application registers provider implementations at startup: AddProvider("b2", new BackblazeProvider()). This table is the authoritative record of which backend names are known to the system.\nOne provider name can serve many workspaces and modules via different profile_info rows — the provider says HOW to talk to the storage service; the profile_info.metadata says WHERE (which bucket / region / path prefix).';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.workspace
CREATE TABLE IF NOT EXISTS `workspace` (
  `id` int(11) NOT NULL AUTO_INCREMENT COMMENT 'Auto-increment surrogate key.',
  `cuid` varchar(48) NOT NULL COMMENT 'Deterministic compact-N GUID derived from SHA-256(client_name##module_name##workspace_name). The StorageCoordinator uses this as the cache key to resolve the workspace base path and provider entirely in memory, with no DB query needed per request.',
  `guid` varchar(42) NOT NULL COMMENT 'CUID generated from the name. Own name hash (not hierarchy aware)',
  `parent` int(11) NOT NULL COMMENT 'FK to module.id. Every workspace belongs to exactly one module.',
  `name` varchar(120) NOT NULL COMMENT 'Machine-usable slug (e.g. "user-files", "invoices-2024", "global-templates"). Combined with client and module names to derive the CUID. Unique per module.',
  `display_name` varchar(120) NOT NULL COMMENT 'Human-readable label shown in UIs (e.g. "User File Archive", "2024 Invoices"). No effect on path computation or CUID derivation.',
  `active` bit(1) NOT NULL DEFAULT b'1' COMMENT '1 = accepts uploads. 0 = soft-disabled; reads still work.',
  `storagename_mode` int(11) NOT NULL DEFAULT 0 COMMENT 'Controls how the physical storage file name is chosen for each file uploaded into this workspace:\n0 - Number mode: the auto-increment database id is used (e.g. doc_version.id = 1234 → file saved as "1234f.pdf"). Compact and fast to look up.\n1 - Guid mode: a compact-N UUID is used (e.g. "a3b2c1d4e5f67890...f.pdf"). Globally unique without a DB round-trip; preferred for distributed or multi-node deployments.',
  `storagename_parse` int(11) NOT NULL DEFAULT 0 COMMENT 'Controls whether the system generates the storage name or parses it from the caller-provided filename:\n0 - Generate (recommended): the system generates a new id or UUID for each file. The caller filename is stored as a display label in doc_info.display_name only.\n1 - Parse: the caller-provided filename IS the storage name. The filename must be a pure integer (Number mode) or a valid GUID (Guid mode). Used for pre-assigned IDs or migrated content.',
  `storage_ref` varchar(300) DEFAULT NULL COMMENT 'Physical workspace segment or object-key prefix. NULL for virtual workspaces.',
  `is_virtual` bit(1) NOT NULL DEFAULT b'0' COMMENT '1 = workspace has no physical workspace segment; files use the client/module base directly.',
  `case_sensitive` bit(1) NOT NULL DEFAULT b'0' COMMENT '1 = preserve client/module display-name casing when constructing the storage base path.',
  `created` timestamp NOT NULL DEFAULT current_timestamp() COMMENT 'Row creation timestamp (UTC).',
  `modified` timestamp NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp() COMMENT 'Automatically updated whenever any column changes.',
  `storage_profile` int(11) DEFAULT NULL COMMENT 'Optional FK to profile_info.id. When set, this workspace uses its own provider and upload mode, overriding the parent module-level profile. Populated by SetWorkspaceStorageProfile() and rehydrated into the in-memory StorageWorkspace cache entry at startup via RehydrateWorkspaceProfilesAsync().',
  PRIMARY KEY (`id`),
  UNIQUE KEY `unq_workspace` (`parent`,`name`),
  UNIQUE KEY `unq_workspace_0` (`parent`,`guid`),
  UNIQUE KEY `unq_workspace_1` (`cuid`),
  KEY `fk_workspace_profile_info` (`storage_profile`),
  CONSTRAINT `fk_workspace_module` FOREIGN KEY (`parent`) REFERENCES `module` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_workspace_profile_info` FOREIGN KEY (`storage_profile`) REFERENCES `profile_info` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION
) ENGINE=InnoDB AUTO_INCREMENT=1923 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='A named storage container inside a module — like a dedicated folder for a specific purpose, tenant, or data set.\nExample: module "documents" → workspace "user-123-uploads" (all files for a specific user) or "global-templates" (shared assets).\nEach workspace has its own base path/prefix, file-naming mode, and can optionally point to a different cloud provider than its parent module.\nThe CUID (derived from the full client+module+workspace name chain) is the key the StorageCoordinator uses to locate the workspace in the in-memory cache and resolve the storage path with zero DB round-trips after startup.\nThe storagename_mode and storagename_parse settings are fixed at registration time and must not be changed after files exist in the workspace, as they determine how storage paths are reconstructed for reads.';

ALTER TABLE `workspace`
  ADD COLUMN IF NOT EXISTS `storage_ref` varchar(300) DEFAULT NULL COMMENT 'Physical workspace segment or object-key prefix. NULL for virtual workspaces.',
  ADD COLUMN IF NOT EXISTS `is_virtual` bit(1) NOT NULL DEFAULT b'0' COMMENT '1 = workspace has no physical workspace segment.',
  ADD COLUMN IF NOT EXISTS `case_sensitive` bit(1) NOT NULL DEFAULT b'0' COMMENT '1 = preserve client/module display-name casing in storage paths.';

UPDATE `workspace`
SET `is_virtual` = b'1', `storage_ref` = NULL
WHERE `name` = 'default';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.stat
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

-- Dumping structure for table dss_core.cli_stat
CREATE TABLE IF NOT EXISTS `cli_stat` (
  `client` int(11) NOT NULL COMMENT 'FK to client.id.',
  `stat` bigint(20) NOT NULL COMMENT 'FK to stat.id containing the cached counters.',
  PRIMARY KEY (`client`),
  UNIQUE KEY `unq_cli_stat_stat` (`stat`),
  CONSTRAINT `fk_cli_stat_client` FOREIGN KEY (`client`) REFERENCES `client` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_cli_stat_stat` FOREIGN KEY (`stat`) REFERENCES `stat` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Connects one client to its reusable statistics payload.';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.mod_stat
CREATE TABLE IF NOT EXISTS `mod_stat` (
  `module` int(11) NOT NULL COMMENT 'FK to module.id.',
  `client` int(11) NOT NULL COMMENT 'Parent client id for quick filtering.',
  `stat` bigint(20) NOT NULL COMMENT 'FK to stat.id containing the cached counters.',
  PRIMARY KEY (`module`),
  UNIQUE KEY `unq_mod_stat_stat` (`stat`),
  KEY `idx_mod_stat_client` (`client`),
  CONSTRAINT `fk_mod_stat_module` FOREIGN KEY (`module`) REFERENCES `module` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_mod_stat_client` FOREIGN KEY (`client`) REFERENCES `client` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_mod_stat_stat` FOREIGN KEY (`stat`) REFERENCES `stat` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Connects one module to its reusable statistics payload.';

-- Data exporting was unselected.

-- Dumping structure for table dss_core.ws_stat
CREATE TABLE IF NOT EXISTS `ws_stat` (
  `workspace` int(11) NOT NULL COMMENT 'FK to workspace.id.',
  `module` int(11) NOT NULL COMMENT 'Parent module id for quick filtering.',
  `client` int(11) NOT NULL COMMENT 'Parent client id for quick filtering.',
  `stat` bigint(20) NOT NULL COMMENT 'FK to stat.id containing the cached counters.',
  PRIMARY KEY (`workspace`),
  UNIQUE KEY `unq_ws_stat_stat` (`stat`),
  KEY `idx_ws_stat_module` (`module`),
  KEY `idx_ws_stat_client` (`client`),
  CONSTRAINT `fk_ws_stat_workspace` FOREIGN KEY (`workspace`) REFERENCES `workspace` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_ws_stat_module` FOREIGN KEY (`module`) REFERENCES `module` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_ws_stat_client` FOREIGN KEY (`client`) REFERENCES `client` (`id`) ON DELETE NO ACTION ON UPDATE NO ACTION,
  CONSTRAINT `fk_ws_stat_stat` FOREIGN KEY (`stat`) REFERENCES `stat` (`id`) ON DELETE CASCADE ON UPDATE NO ACTION
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci COMMENT='Connects one workspace to its reusable statistics payload.';

-- Data exporting was unselected.

/*!40103 SET TIME_ZONE=IFNULL(@OLD_TIME_ZONE, 'system') */;
/*!40101 SET SQL_MODE=IFNULL(@OLD_SQL_MODE, '') */;
/*!40014 SET FOREIGN_KEY_CHECKS=IFNULL(@OLD_FOREIGN_KEY_CHECKS, 1) */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40111 SET SQL_NOTES=IFNULL(@OLD_SQL_NOTES, 1) */;
