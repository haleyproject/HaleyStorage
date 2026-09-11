using static Haley.Internal.IndexingConstant;

namespace Haley.Internal {
    internal partial class IndexingQueries {
        public partial class INSTANCE {
            public class STATS {
                public const string INSERT_DIR_PATH_FOR_DIRECTORY =
                    $@"insert ignore into dir_path (ancestor, descendant, depth)
                       select src.id, src.id, 0
                       from directory as src
                       where src.id = {ID}
                       union all
                       select path.ancestor, src.id, path.depth + 1
                       from directory as src
                       inner join dir_path as path on path.descendant = src.parent
                       where src.id = {ID};";

                public const string QUEUE_EVENT =
                    $@"insert ignore into stat_evt (
                            event_key, event_type, node_type, node_id, workspace, `document`, `version`, ext,
                            active_folders_delta, deleted_folders_delta, active_docs_delta, deleted_docs_delta,
                            active_versions_delta, deleted_versions_delta, active_thumbs_delta, deleted_thumbs_delta,
                            active_bytes_delta, deleted_bytes_delta, archived_bytes_delta, purged_bytes_delta)
                       values (
                            {EVENT_KEY}, {EVENT_TYPE}, {NODE_TYPE}, {NODE_ID}, {WORKSPACE_ID}, {DOCUMENT_ID}, {VERSION_ID}, {EXT_NAME},
                            {ACTIVE_FOLDERS_DELTA}, {DELETED_FOLDERS_DELTA}, {ACTIVE_DOCS_DELTA}, {DELETED_DOCS_DELTA},
                            {ACTIVE_VERSIONS_DELTA}, {DELETED_VERSIONS_DELTA}, {ACTIVE_THUMBS_DELTA}, {DELETED_THUMBS_DELTA},
                            {ACTIVE_BYTES_DELTA}, {DELETED_BYTES_DELTA}, {ARCHIVED_BYTES_DELTA}, {PURGED_BYTES_DELTA});";

                public const string GET_PENDING =
                    $@"select id, event_key, event_type, node_type, node_id, workspace, `document`, `version`, ext,
                              active_folders_delta, deleted_folders_delta, active_docs_delta, deleted_docs_delta,
                              active_versions_delta, deleted_versions_delta, active_thumbs_delta, deleted_thumbs_delta,
                              active_bytes_delta, deleted_bytes_delta, archived_bytes_delta, purged_bytes_delta
                       from stat_evt
                       where processed is null
                       order by id
                       limit {BATCH_SIZE}
                       for update skip locked;";

                public const string MARK_PROCESSED =
                    $@"update stat_evt set processed = utc_timestamp(), message = {MESSAGE} where id = {ID};";

                public const string DELETE_PROCESSED =
                    $@"delete from stat_evt
                       where processed is not null
                         and processed < utc_timestamp() - interval 24 hour
                       order by id
                       limit {BATCH_SIZE};";

                public const string GET_TREE_TARGETS =
                    $@"select 1 as node_type, {WORKSPACE_ID} as node_id, {WORKSPACE_ID} as workspace
                       union all
                       select 2 as node_type, path.ancestor as node_id, dir.workspace as workspace
                       from dir_path as path
                       inner join directory as dir on dir.id = path.ancestor
                       where {NODE_TYPE} = 2 and path.descendant = {NODE_ID};";

                public const string INSERT_STAT = "insert into stat () values ();";
                public const string GET_LAST_STAT_ID = "select last_insert_id();";

                public const string GET_NODE_STAT_ID =
                    $@"select stat from node_stat where node_type = {NODE_TYPE} and node_id = {NODE_ID} limit 1 for update;";
                public const string INSERT_NODE_STAT =
                    $@"insert into node_stat (node_type, node_id, workspace, stat)
                       values ({NODE_TYPE}, {NODE_ID}, {WORKSPACE_ID}, {STAT_ID});";

                public const string GET_TREE_STAT_ID =
                    $@"select stat from tree_stat where node_type = {NODE_TYPE} and node_id = {NODE_ID} limit 1 for update;";
                public const string INSERT_TREE_STAT =
                    $@"insert into tree_stat (node_type, node_id, workspace, stat)
                       values ({NODE_TYPE}, {NODE_ID}, {WORKSPACE_ID}, {STAT_ID});";

                public const string GET_NODE_EXT_STAT_ID =
                    $@"select stat from node_ext_stat
                       where node_type = {NODE_TYPE} and node_id = {NODE_ID} and ext = {EXT_NAME}
                       limit 1 for update;";
                public const string INSERT_NODE_EXT_STAT =
                    $@"insert into node_ext_stat (node_type, node_id, workspace, ext, stat)
                       values ({NODE_TYPE}, {NODE_ID}, {WORKSPACE_ID}, {EXT_NAME}, {STAT_ID});";

                public const string GET_TREE_EXT_STAT_ID =
                    $@"select stat from tree_ext_stat
                       where node_type = {NODE_TYPE} and node_id = {NODE_ID} and ext = {EXT_NAME}
                       limit 1 for update;";
                public const string INSERT_TREE_EXT_STAT =
                    $@"insert into tree_ext_stat (node_type, node_id, workspace, ext, stat)
                       values ({NODE_TYPE}, {NODE_ID}, {WORKSPACE_ID}, {EXT_NAME}, {STAT_ID});";

                public const string APPLY_STAT_DELTA =
                    $@"update stat set
                            active_folders = greatest(0, active_folders + {ACTIVE_FOLDERS_DELTA}),
                            deleted_folders = greatest(0, deleted_folders + {DELETED_FOLDERS_DELTA}),
                            active_docs = greatest(0, active_docs + {ACTIVE_DOCS_DELTA}),
                            deleted_docs = greatest(0, deleted_docs + {DELETED_DOCS_DELTA}),
                            active_versions = greatest(0, active_versions + {ACTIVE_VERSIONS_DELTA}),
                            deleted_versions = greatest(0, deleted_versions + {DELETED_VERSIONS_DELTA}),
                            active_thumbs = greatest(0, active_thumbs + {ACTIVE_THUMBS_DELTA}),
                            deleted_thumbs = greatest(0, deleted_thumbs + {DELETED_THUMBS_DELTA}),
                            active_bytes = greatest(0, active_bytes + {ACTIVE_BYTES_DELTA}),
                            deleted_bytes = greatest(0, deleted_bytes + {DELETED_BYTES_DELTA}),
                            archived_bytes = greatest(0, archived_bytes + {ARCHIVED_BYTES_DELTA}),
                            purged_bytes = greatest(0, purged_bytes + {PURGED_BYTES_DELTA})
                       where id = {STAT_ID};";

                public const string DELETE_STAT_IF_UNUSED =
                    $@"delete from stat
                       where id = {STAT_ID}
                         and not exists (select 1 from node_stat where stat = {STAT_ID})
                         and not exists (select 1 from tree_stat where stat = {STAT_ID})
                         and not exists (select 1 from node_ext_stat where stat = {STAT_ID})
                         and not exists (select 1 from tree_ext_stat where stat = {STAT_ID});";

                public const string GET_VERSION_SOURCE =
                    $@"select dv.id as version_id, dv.parent as document_id, dv.ver as version_no, dv.sub_ver as sub_version_no,
                              dv.delete_state as version_delete_state, d.workspace, d.parent as directory_id,
                              d.delete_state as document_delete_state, coalesce(vi.size, 0) as size,
                              coalesce(vi.flags, 0) as flags,
                              case
                                  when dv.sub_ver = 0 then ext.name
                                  when instr(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.') > 0
                                      then lower(concat('.', substring_index(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.', -1)))
                                  else 'default'
                              end as ext
                       from doc_version as dv
                       inner join document as d on d.id = dv.parent
                       left join version_info as vi on vi.id = dv.id
                       left join name_store as ns on ns.id = d.name
                       left join extension as ext on ext.id = ns.extension
                       where dv.id = {ID}
                       limit 1;";

                public const string GET_VERSION_SOURCES_BY_PARENT =
                    $@"select dv.id as version_id, dv.parent as document_id, dv.ver as version_no, dv.sub_ver as sub_version_no,
                              dv.delete_state as version_delete_state, d.workspace, d.parent as directory_id,
                              d.delete_state as document_delete_state, coalesce(vi.size, 0) as size,
                              coalesce(vi.flags, 0) as flags,
                              case
                                  when dv.sub_ver = 0 then ext.name
                                  when instr(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.') > 0
                                      then lower(concat('.', substring_index(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.', -1)))
                                  else 'default'
                              end as ext
                       from doc_version as dv
                       inner join document as d on d.id = dv.parent
                       left join version_info as vi on vi.id = dv.id
                       left join name_store as ns on ns.id = d.name
                       left join extension as ext on ext.id = ns.extension
                       where dv.parent = {PARENT}
                       order by dv.ver, dv.sub_ver;";

                public const string GET_VERSION_SOURCES_BY_VERSION =
                    $@"select dv.id as version_id, dv.parent as document_id, dv.ver as version_no, dv.sub_ver as sub_version_no,
                              dv.delete_state as version_delete_state, d.workspace, d.parent as directory_id,
                              d.delete_state as document_delete_state, coalesce(vi.size, 0) as size,
                              coalesce(vi.flags, 0) as flags,
                              case
                                  when dv.sub_ver = 0 then ext.name
                                  when instr(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.') > 0
                                      then lower(concat('.', substring_index(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.', -1)))
                                  else 'default'
                              end as ext
                       from doc_version as dv
                       inner join document as d on d.id = dv.parent
                       left join version_info as vi on vi.id = dv.id
                       left join name_store as ns on ns.id = d.name
                       left join extension as ext on ext.id = ns.extension
                       where dv.parent = {PARENT} and dv.ver = {VERSION}
                       order by dv.sub_ver;";

                public const string COUNT_ACTIVE_COMPLETED_CONTENT_EXCLUDING =
                    $@"select count(*)
                       from doc_version as dv
                       inner join document as d on d.id = dv.parent and d.delete_state = 0
                       inner join version_info as vi on vi.id = dv.id
                       where dv.parent = {PARENT}
                         and dv.id <> {ID}
                         and dv.sub_ver = 0
                         and dv.delete_state = 0
                         and (coalesce(vi.flags, 0) & 64) <> 0;";

                public const string COUNT_ACTIVE_COMPLETED_CONTENT =
                    $@"select count(*)
                       from doc_version as dv
                       inner join document as d on d.id = dv.parent and d.delete_state = 0
                       inner join version_info as vi on vi.id = dv.id
                       where dv.parent = {PARENT}
                         and dv.sub_ver = 0
                         and dv.delete_state = 0
                         and (coalesce(vi.flags, 0) & 64) <> 0;";

                public const string CLEAR_STATS = "delete from stat;";
                public const string CLEAR_DIR_PATH = "delete from dir_path;";
                public const string CLEAR_EVENTS = "delete from stat_evt;";
                public const string DROP_REBUILD_TEMP = "drop temporary table if exists tmp_stat_rebuild;";

                public const string INSERT_REBUILD_STATS =
                    @"insert into stat (
                          id, active_folders, deleted_folders, active_docs, deleted_docs,
                          active_versions, deleted_versions, active_thumbs, deleted_thumbs,
                          active_bytes, deleted_bytes, archived_bytes, purged_bytes)
                      select stat_id, active_folders, deleted_folders, active_docs, deleted_docs,
                             active_versions, deleted_versions, active_thumbs, deleted_thumbs,
                             active_bytes, deleted_bytes, archived_bytes, purged_bytes
                      from tmp_stat_rebuild
                      order by stat_id;";

                public const string REBUILD_DIR_PATH =
                    @"insert ignore into dir_path (ancestor, descendant, depth)
                      with recursive path_tree as (
                          select dir.id as ancestor, dir.id as descendant, 0 as depth
                          from directory as dir
                          union all
                          select path_tree.ancestor, child.id as descendant, path_tree.depth + 1 as depth
                          from path_tree
                          inner join directory as child on child.parent = path_tree.descendant
                          where child.parent > 0
                      )
                      select ancestor, descendant, depth from path_tree;";

                public const string REBUILD_NODE_STATS =
                    @"create temporary table tmp_stat_rebuild engine=InnoDB as
                      select (select coalesce(max(id), 1987) from stat)
                                 + row_number() over (order by source.node_type, source.node_id) as stat_id,
                             source.*
                      from (
                          select 1 as node_type, ws.id as node_id, ws.id as workspace, cast(null as char(100)) as ext,
                                 (select count(*) from directory as dir where dir.workspace = ws.id and dir.parent = 0 and dir.delete_state = 0) as active_folders,
                                 (select count(*) from directory as dir where dir.workspace = ws.id and dir.parent = 0 and dir.delete_state > 0) as deleted_folders,
                                 0 as active_docs, 0 as deleted_docs, 0 as active_versions, 0 as deleted_versions,
                                 0 as active_thumbs, 0 as deleted_thumbs, 0 as active_bytes, 0 as deleted_bytes,
                                 0 as archived_bytes, 0 as purged_bytes
                          from workspace as ws
                          union all
                          select 2, dir.id, dir.workspace, cast(null as char(100)),
                                 (select count(*) from directory as child where child.workspace = dir.workspace and child.parent = dir.id and child.delete_state = 0),
                                 (select count(*) from directory as child where child.workspace = dir.workspace and child.parent = dir.id and child.delete_state > 0),
                                 (select count(distinct doc.id) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.sub_ver = 0 and dv.delete_state = 0 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and doc.delete_state = 0 and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select count(*) from document as doc where doc.parent = dir.id and doc.delete_state > 0),
                                 (select count(*) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.sub_ver = 0 and dv.delete_state = 0 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and doc.delete_state = 0 and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select count(*) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.sub_ver = 0 and dv.delete_state > 0 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select count(*) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.sub_ver > 0 and dv.delete_state = 0 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and doc.delete_state = 0 and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select count(*) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.sub_ver > 0 and dv.delete_state > 0 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select coalesce(sum(vi.size), 0) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.delete_state = 0 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and doc.delete_state = 0 and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select coalesce(sum(vi.size), 0) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.delete_state > 0 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select coalesce(sum(vi.size), 0) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.delete_state = 2 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and (coalesce(vi.flags, 0) & 64) <> 0),
                                 (select coalesce(sum(vi.size), 0) from document as doc inner join doc_version as dv on dv.parent = doc.id and dv.delete_state = 3 inner join version_info as vi on vi.id = dv.id where doc.parent = dir.id and (coalesce(vi.flags, 0) & 64) <> 0)
                          from directory as dir
                      ) as source;";

                public const string INSERT_REBUILD_NODE_STATS =
                    @"insert into node_stat (node_type, node_id, workspace, stat)
                      select node_type, node_id, workspace, stat_id from tmp_stat_rebuild;";

                public const string REBUILD_NODE_EXT_STATS =
                    @"create temporary table tmp_stat_rebuild engine=InnoDB as
                      select (select coalesce(max(id), 1987) from stat)
                                 + row_number() over (order by source.node_type, source.node_id, source.ext) as stat_id,
                             source.*
                      from (
                          select 2 as node_type, d.parent as node_id, d.workspace,
                                 coalesce(case
                                     when dv.sub_ver = 0 then file_ext.name
                                     when instr(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.') > 0
                                         then lower(concat('.', substring_index(coalesce(nullif(vi.storage_ref, ''), vi.storage_name), '.', -1)))
                                     else 'default'
                                 end, 'default') as ext,
                                 0 as active_folders, 0 as deleted_folders,
                                 count(distinct case when dv.sub_ver = 0 and d.delete_state = 0 and dv.delete_state = 0 then d.id end) as active_docs,
                                 count(distinct case when dv.sub_ver = 0 and d.delete_state > 0 then d.id end) as deleted_docs,
                                 sum(case when dv.sub_ver = 0 and d.delete_state = 0 and dv.delete_state = 0 then 1 else 0 end) as active_versions,
                                 sum(case when dv.sub_ver = 0 and dv.delete_state > 0 then 1 else 0 end) as deleted_versions,
                                 sum(case when dv.sub_ver > 0 and d.delete_state = 0 and dv.delete_state = 0 then 1 else 0 end) as active_thumbs,
                                 sum(case when dv.sub_ver > 0 and dv.delete_state > 0 then 1 else 0 end) as deleted_thumbs,
                                 coalesce(sum(case when d.delete_state = 0 and dv.delete_state = 0 then vi.size else 0 end), 0) as active_bytes,
                                 coalesce(sum(case when dv.delete_state > 0 then vi.size else 0 end), 0) as deleted_bytes,
                                 coalesce(sum(case when dv.delete_state = 2 then vi.size else 0 end), 0) as archived_bytes,
                                 coalesce(sum(case when dv.delete_state = 3 then vi.size else 0 end), 0) as purged_bytes
                          from document as d
                          inner join doc_version as dv on dv.parent = d.id
                          inner join version_info as vi on vi.id = dv.id
                          left join name_store as ns on ns.id = d.name
                          left join extension as file_ext on file_ext.id = ns.extension
                          where (coalesce(vi.flags, 0) & 64) <> 0
                          group by d.parent, d.workspace, ext
                      ) as source;";

                public const string INSERT_REBUILD_NODE_EXT_STATS =
                    @"insert into node_ext_stat (node_type, node_id, workspace, ext, stat)
                      select node_type, node_id, workspace, ext, stat_id from tmp_stat_rebuild;";

                public const string REBUILD_TREE_STATS =
                    @"create temporary table tmp_stat_rebuild engine=InnoDB as
                      select (select coalesce(max(id), 1987) from stat)
                                 + row_number() over (order by source.node_type, source.node_id) as stat_id,
                             source.*
                      from (
                          select 1 as node_type, ws.id as node_id, ws.id as workspace, cast(null as char(100)) as ext,
                                 coalesce(sum(s.active_folders), 0) as active_folders,
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
                          from workspace as ws
                          left join node_stat as ns on ns.workspace = ws.id
                          left join stat as s on s.id = ns.stat
                          group by ws.id
                          union all
                          select 2, path.ancestor, dir.workspace, cast(null as char(100)),
                                 coalesce(sum(s.active_folders), 0),
                                 coalesce(sum(s.deleted_folders), 0),
                                 coalesce(sum(s.active_docs), 0),
                                 coalesce(sum(s.deleted_docs), 0),
                                 coalesce(sum(s.active_versions), 0),
                                 coalesce(sum(s.deleted_versions), 0),
                                 coalesce(sum(s.active_thumbs), 0),
                                 coalesce(sum(s.deleted_thumbs), 0),
                                 coalesce(sum(s.active_bytes), 0),
                                 coalesce(sum(s.deleted_bytes), 0),
                                 coalesce(sum(s.archived_bytes), 0),
                                 coalesce(sum(s.purged_bytes), 0)
                          from dir_path as path
                          inner join directory as dir on dir.id = path.ancestor
                          left join node_stat as ns on ns.node_type = 2 and ns.node_id = path.descendant
                          left join stat as s on s.id = ns.stat
                          group by path.ancestor, dir.workspace
                      ) as source;";

                public const string INSERT_REBUILD_TREE_STATS =
                    @"insert into tree_stat (node_type, node_id, workspace, stat)
                      select node_type, node_id, workspace, stat_id from tmp_stat_rebuild;";

                public const string REBUILD_TREE_EXT_STATS =
                    @"create temporary table tmp_stat_rebuild engine=InnoDB as
                      select (select coalesce(max(id), 1987) from stat)
                                 + row_number() over (order by source.node_type, source.node_id, source.ext) as stat_id,
                             source.*
                      from (
                          select 1 as node_type, ws.id as node_id, ws.id as workspace, nes.ext,
                                 0 as active_folders, 0 as deleted_folders,
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
                          from workspace as ws
                          inner join node_ext_stat as nes on nes.workspace = ws.id
                          inner join stat as s on s.id = nes.stat
                          group by ws.id, nes.ext
                          union all
                          select 2, path.ancestor, dir.workspace, nes.ext,
                                 0, 0,
                                 coalesce(sum(s.active_docs), 0),
                                 coalesce(sum(s.deleted_docs), 0),
                                 coalesce(sum(s.active_versions), 0),
                                 coalesce(sum(s.deleted_versions), 0),
                                 coalesce(sum(s.active_thumbs), 0),
                                 coalesce(sum(s.deleted_thumbs), 0),
                                 coalesce(sum(s.active_bytes), 0),
                                 coalesce(sum(s.deleted_bytes), 0),
                                 coalesce(sum(s.archived_bytes), 0),
                                 coalesce(sum(s.purged_bytes), 0)
                          from dir_path as path
                          inner join directory as dir on dir.id = path.ancestor
                          inner join node_ext_stat as nes on nes.node_type = 2 and nes.node_id = path.descendant
                          inner join stat as s on s.id = nes.stat
                          group by path.ancestor, dir.workspace, nes.ext
                      ) as source;";

                public const string INSERT_REBUILD_TREE_EXT_STATS =
                    @"insert into tree_ext_stat (node_type, node_id, workspace, ext, stat)
                      select node_type, node_id, workspace, ext, stat_id from tmp_stat_rebuild;";

                public const string GET_NODE_STAT =
                    $@"select ns.node_type, ns.node_id, ns.workspace, s.*
                       from node_stat as ns
                       inner join stat as s on s.id = ns.stat
                       where ns.node_type = {NODE_TYPE} and ns.node_id = {NODE_ID}
                       limit 1;";

                public const string GET_TREE_STAT =
                    $@"select ts.node_type, ts.node_id, ts.workspace, s.*
                       from tree_stat as ts
                       inner join stat as s on s.id = ts.stat
                       where ts.node_type = {NODE_TYPE} and ts.node_id = {NODE_ID}
                       limit 1;";

                public const string GET_NODE_EXT_STATS =
                    $@"select nes.node_type, nes.node_id, nes.workspace, nes.ext, s.*
                       from node_ext_stat as nes
                       inner join stat as s on s.id = nes.stat
                       where nes.node_type = {NODE_TYPE}
                         and nes.node_id = {NODE_ID}
                         and ({EXT_NAME} is null or nes.ext = {EXT_NAME})
                       order by nes.ext;";

                public const string GET_TREE_EXT_STATS =
                    $@"select tes.node_type, tes.node_id, tes.workspace, tes.ext, s.*
                       from tree_ext_stat as tes
                       inner join stat as s on s.id = tes.stat
                       where tes.node_type = {NODE_TYPE}
                         and tes.node_id = {NODE_ID}
                         and ({EXT_NAME} is null or tes.ext = {EXT_NAME})
                       order by tes.ext;";

                public const string GET_WORKSPACE_TREE_STATS =
                    @"select ts.node_type, ts.node_id, ts.workspace, s.*
                      from tree_stat as ts
                      inner join stat as s on s.id = ts.stat
                      where ts.node_type = 1
                      order by ts.node_id;";

                public const string INSERT_RUN =
                    $@"insert into stat_run (run_type, status, message) values ({RUN_TYPE}, {STATUS}, {MESSAGE});";
            }
        }
    }
}
