using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml.Linq;
using static Haley.Internal.IndexingConstant;

namespace Haley.Internal {
    /// <summary>
    /// Named parameter placeholder constants used in all <see cref="IndexingQueries"/> SQL strings.
    /// Each constant expands to the <c>@FIELDNAME</c> form expected by the MariaDB adapter gateway.
    /// </summary>
    internal class IndexingConstant {
        public const string VAULT_DEFCLIENT = "admin";
        public const string NAME = $@"@{nameof(NAME)}";
        public const string DNAME = $@"@{nameof(DNAME)}";
        public const string SAVENAME = $@"@{nameof(SAVENAME)}";
        public const string GUID = $@"@{nameof(GUID)}";
        public const string CUID = $@"@{nameof(CUID)}";
        public const string PATH = $@"@{nameof(PATH)}";
        public const string SUFFIX_DIR = $@"@{nameof(SUFFIX_DIR)}";
        public const string SUFFIX_FILE = $@"@{nameof(SUFFIX_FILE)}";
        public const string ID = $@"@{nameof(ID)}";
        public const string FULLNAME = $@"@{nameof(FULLNAME)}";
        public const string SIGNKEY = $@"@{nameof(SIGNKEY)}";
        public const string ENCRYPTKEY = $@"@{nameof(ENCRYPTKEY)}";
        public const string VALUE = $@"@{nameof(VALUE)}";
        public const string ACTOR = $@"@{nameof(ACTOR)}";
        public const string ORIGINAL_NAME = $@"@{nameof(ORIGINAL_NAME)}";
        public const string PASSWORD = $@"@{nameof(PASSWORD)}";
        public const string DATETIME = $@"@{nameof(DATETIME)}";
        public const string DELETED = $@"@{nameof(DELETED)}";
        public const string DELETED_AT = $@"@{nameof(DELETED_AT)}";
        public const string PARENT = $@"@{nameof(PARENT)}";
        public const string DIRNAME = $@"@{nameof(DIRNAME)}";
        public const string CONTROLMODE = $@"@{nameof(CONTROLMODE)}";    // legacy — kept for any non-workspace callers
        public const string PARSEMODE = $@"@{nameof(PARSEMODE)}";       // legacy — kept for any non-workspace callers
        // Workspace column renames (schema v2)
        public const string STORAGE_REF = $@"@{nameof(STORAGE_REF)}";           // workspace.storage_ref
        public const string IS_VIRTUAL = $@"@{nameof(IS_VIRTUAL)}";             // workspace.is_virtual
        public const string CASE_SENSITIVE = $@"@{nameof(CASE_SENSITIVE)}";     // workspace.case_sensitive
        public const string STORAGENAME_MODE = $@"@{nameof(STORAGENAME_MODE)}"; // workspace.storagename_mode
        public const string STORAGENAME_PARSE = $@"@{nameof(STORAGENAME_PARSE)}"; // workspace.storagename_parse
        public const string WSPACE = $@"@{nameof(WSPACE)}";
        public const string EXT = $@"@{nameof(EXT)}";
        public const string VERSION = $@"@{nameof(VERSION)}";
        public const string SIZE = $@"@{nameof(SIZE)}";

        public const string STORAGE_PROFILE = $@"@{nameof(STORAGE_PROFILE)}";

        // CORE : PROFILE / PROVIDER
        public const string PROFILE_ID = $@"@{nameof(PROFILE_ID)}";
        public const string PROVIDER_ID = $@"@{nameof(PROVIDER_ID)}";
        public const string STORAGE_PROVIDER = $@"@{nameof(STORAGE_PROVIDER)}";
        public const string STAGING_PROVIDER = $@"@{nameof(STAGING_PROVIDER)}";
        public const string MODE = $@"@{nameof(MODE)}";
        public const string DESCRIPTION = $@"@{nameof(DESCRIPTION)}";
        public const string METADATA = $@"@{nameof(METADATA)}";

        // CLIENT : VERSION INFO
        public const string STAGINGPATH = $@"@{nameof(STAGINGPATH)}";
        public const string FLAGS = $@"@{nameof(FLAGS)}";

        // CLIENT : VERSION INFO (new columns)
        public const string HASH = $@"@{nameof(HASH)}";                         // version_info.hash / chunked_files.hash
        public const string SYNCED_AT = $@"@{nameof(SYNCED_AT)}";               // version_info.synced_at
        public const string PROFILE_INFO_ID = $@"@{nameof(PROFILE_INFO_ID)}";   // version_info.profile_info_id

        // CLIENT : CHUNKING
        public const string CHUNK_SIZE = $@"@{nameof(CHUNK_SIZE)}";     // chunk_info.size (MB)
        public const string CHUNK_PARTS = $@"@{nameof(CHUNK_PARTS)}";   // chunk_info.parts
        public const string CHUNK_NAME = $@"@{nameof(CHUNK_NAME)}";     // chunk_info.name
        public const string IS_COMPLETED = $@"@{nameof(IS_COMPLETED)}"; // chunk_info.is_completed
        public const string PART = $@"@{nameof(PART)}";                 // chunked_files.part
        public const string FILESIZE_MB = $@"@{nameof(FILESIZE_MB)}";   // chunked_files.size (MB)
        public const string LIMIT_ROWS = $@"@{nameof(LIMIT_ROWS)}";     // browse pagination LIMIT
        public const string OFFSET_ROWS = $@"@{nameof(OFFSET_ROWS)}";   // browse pagination OFFSET

        // THUMBNAIL
        public const string SUB_VER = $@"@{nameof(SUB_VER)}";           // doc_version.sub_ver (0=content, 1+=thumbnail)

        // STATS / MOVE
        public const string EVENT_KEY = $@"@{nameof(EVENT_KEY)}";
        public const string EVENT_TYPE = $@"@{nameof(EVENT_TYPE)}";
        public const string NODE_TYPE = $@"@{nameof(NODE_TYPE)}";
        public const string NODE_ID = $@"@{nameof(NODE_ID)}";
        public const string STAT_ID = $@"@{nameof(STAT_ID)}";
        public const string WORKSPACE_ID = $@"@{nameof(WORKSPACE_ID)}";
        public const string DOCUMENT_ID = $@"@{nameof(DOCUMENT_ID)}";
        public const string VERSION_ID = $@"@{nameof(VERSION_ID)}";
        public const string EXT_NAME = $@"@{nameof(EXT_NAME)}";
        public const string ACTIVE_FOLDERS_DELTA = $@"@{nameof(ACTIVE_FOLDERS_DELTA)}";
        public const string DELETED_FOLDERS_DELTA = $@"@{nameof(DELETED_FOLDERS_DELTA)}";
        public const string ACTIVE_DOCS_DELTA = $@"@{nameof(ACTIVE_DOCS_DELTA)}";
        public const string DELETED_DOCS_DELTA = $@"@{nameof(DELETED_DOCS_DELTA)}";
        public const string ACTIVE_VERSIONS_DELTA = $@"@{nameof(ACTIVE_VERSIONS_DELTA)}";
        public const string DELETED_VERSIONS_DELTA = $@"@{nameof(DELETED_VERSIONS_DELTA)}";
        public const string ACTIVE_THUMBS_DELTA = $@"@{nameof(ACTIVE_THUMBS_DELTA)}";
        public const string DELETED_THUMBS_DELTA = $@"@{nameof(DELETED_THUMBS_DELTA)}";
        public const string ACTIVE_BYTES_DELTA = $@"@{nameof(ACTIVE_BYTES_DELTA)}";
        public const string DELETED_BYTES_DELTA = $@"@{nameof(DELETED_BYTES_DELTA)}";
        public const string ARCHIVED_BYTES_DELTA = $@"@{nameof(ARCHIVED_BYTES_DELTA)}";
        public const string PURGED_BYTES_DELTA = $@"@{nameof(PURGED_BYTES_DELTA)}";
        public const string BATCH_SIZE = $@"@{nameof(BATCH_SIZE)}";
        public const string TARGET_PARENT = $@"@{nameof(TARGET_PARENT)}";
        public const string TARGET_WORKSPACE = $@"@{nameof(TARGET_WORKSPACE)}";
        public const string RUN_TYPE = $@"@{nameof(RUN_TYPE)}";
        public const string STATUS = $@"@{nameof(STATUS)}";
        public const string MESSAGE = $@"@{nameof(MESSAGE)}";
    }
}
