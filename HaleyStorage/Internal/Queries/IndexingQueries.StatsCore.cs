using static Haley.Internal.IndexingConstant;

namespace Haley.Internal {
    internal partial class IndexingQueries {
        public class STATS_CORE {
            public const string GET_MODULE_IDS =
                $@"select m.id as module_id, m.parent as client_id
                   from module as m
                   where m.cuid = {CUID}
                   limit 1;";

            public const string INSERT_STAT = "insert into stat () values ();";
            public const string GET_LAST_STAT_ID = "select last_insert_id();";

            public const string GET_WORKSPACE_STAT_ID =
                $@"select stat from ws_stat where workspace = {WORKSPACE_ID} limit 1 for update;";
            public const string INSERT_WORKSPACE_STAT =
                $@"insert into ws_stat (workspace, module, client, stat)
                   values ({WORKSPACE_ID}, {ID}, {PARENT}, {STAT_ID});";

            public const string GET_MODULE_STAT_ID =
                $@"select stat from mod_stat where module = {ID} limit 1 for update;";
            public const string INSERT_MODULE_STAT =
                $@"insert into mod_stat (module, client, stat) values ({ID}, {PARENT}, {STAT_ID});";

            public const string GET_CLIENT_STAT_ID =
                $@"select stat from cli_stat where client = {PARENT} limit 1 for update;";
            public const string INSERT_CLIENT_STAT =
                $@"insert into cli_stat (client, stat) values ({PARENT}, {STAT_ID});";

            public const string OVERWRITE_STAT =
                $@"update stat set
                        active_folders = {ACTIVE_FOLDERS_DELTA},
                        deleted_folders = {DELETED_FOLDERS_DELTA},
                        active_docs = {ACTIVE_DOCS_DELTA},
                        deleted_docs = {DELETED_DOCS_DELTA},
                        active_versions = {ACTIVE_VERSIONS_DELTA},
                        deleted_versions = {DELETED_VERSIONS_DELTA},
                        active_thumbs = {ACTIVE_THUMBS_DELTA},
                        deleted_thumbs = {DELETED_THUMBS_DELTA},
                        active_bytes = {ACTIVE_BYTES_DELTA},
                        deleted_bytes = {DELETED_BYTES_DELTA},
                        archived_bytes = {ARCHIVED_BYTES_DELTA},
                        purged_bytes = {PURGED_BYTES_DELTA}
                   where id = {STAT_ID};";

            public const string GET_MODULE_TOTALS =
                $@"select coalesce(sum(s.active_folders), 0) as active_folders,
                          coalesce(sum(s.deleted_folders), 0) as deleted_folders,
                          coalesce(sum(s.active_docs), 0) as active_docs,
                          coalesce(sum(s.deleted_docs), 0) as deleted_docs,
                          coalesce(sum(s.active_versions), 0) as active_versions,
                          coalesce(sum(s.deleted_versions), 0) as deleted_versions,
                          coalesce(sum(s.active_thumbs), 0) as active_thumbs,
                          coalesce(sum(s.deleted_thumbs), 0) as deleted_thumbs,
                          coalesce(sum(s.active_bytes), 0) as active_bytes,
                          coalesce(sum(s.deleted_bytes), 0) as deleted_bytes,
                          coalesce(sum(s.archived_bytes), 0) as archived_bytes,
                          coalesce(sum(s.purged_bytes), 0) as purged_bytes
                   from ws_stat as ws
                   inner join stat as s on s.id = ws.stat
                   where ws.module = {ID};";

            public const string GET_CLIENT_TOTALS =
                $@"select coalesce(sum(s.active_folders), 0) as active_folders,
                          coalesce(sum(s.deleted_folders), 0) as deleted_folders,
                          coalesce(sum(s.active_docs), 0) as active_docs,
                          coalesce(sum(s.deleted_docs), 0) as deleted_docs,
                          coalesce(sum(s.active_versions), 0) as active_versions,
                          coalesce(sum(s.deleted_versions), 0) as deleted_versions,
                          coalesce(sum(s.active_thumbs), 0) as active_thumbs,
                          coalesce(sum(s.deleted_thumbs), 0) as deleted_thumbs,
                          coalesce(sum(s.active_bytes), 0) as active_bytes,
                          coalesce(sum(s.deleted_bytes), 0) as deleted_bytes,
                          coalesce(sum(s.archived_bytes), 0) as archived_bytes,
                          coalesce(sum(s.purged_bytes), 0) as purged_bytes
                   from mod_stat as ms
                   inner join stat as s on s.id = ms.stat
                   where ms.client = {PARENT};";

            public const string DELETE_STAT_IF_UNUSED =
                $@"delete from stat
                   where id = {STAT_ID}
                     and not exists (select 1 from cli_stat where stat = {STAT_ID})
                     and not exists (select 1 from mod_stat where stat = {STAT_ID})
                     and not exists (select 1 from ws_stat where stat = {STAT_ID});";
        }
    }
}
