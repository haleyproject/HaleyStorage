using static Haley.Internal.IndexingConstant;

namespace Haley.Internal {
    internal partial class IndexingQueries {
        public partial class INSTANCE {
            public class SEARCH {
                // Shared file-join columns used by every ITEMS query (same shape as BROWSE_ITEMS).
                const string _FILE_COLS = $@"1 as sort_group, 'file' as item_type, d.id, d.cuid as uid, coalesce(di.display_name, '') as display_name, dv.actor as actor_id, d.parent as parent_id, exists(select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = d.parent and left(hd.name, 1) = '.') as is_hidden, '' as virtual_path, d.delete_state, d.deleted, d.delete_state as document_delete_state, d.deleted as document_deleted, dv.delete_state as version_delete_state, dv.deleted as version_deleted, d.created, d.modified, dv.id as version_id, dv.cuid as version_cuid, dv.ver as version_no, latest.version_count, exists(select 1 from doc_version as thumb where thumb.parent = d.id and thumb.ver = dv.ver and thumb.sub_ver > 0 and thumb.delete_state = 0) as has_thumbnail, dv.created as version_created, vi.size, vi.storage_name, vi.storage_ref, vi.staging_ref, vi.flags, vi.hash, vi.synced_at";
                const string _FILE_COLS_ALL = $@"1 as sort_group, 'file' as item_type, d.id, d.cuid as uid, coalesce(di.display_name, '') as display_name, dv.actor as actor_id, d.parent as parent_id, exists(select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = d.parent and left(hd.name, 1) = '.') as is_hidden, '' as virtual_path, case when d.delete_state > 0 then d.delete_state else dv.delete_state end as delete_state, case when d.delete_state > 0 then d.deleted else dv.deleted end as deleted, d.delete_state as document_delete_state, d.deleted as document_deleted, dv.delete_state as version_delete_state, dv.deleted as version_deleted, d.created, d.modified, dv.id as version_id, dv.cuid as version_cuid, dv.ver as version_no, latest.version_count, exists(select 1 from doc_version as thumb where thumb.parent = d.id and thumb.ver = dv.ver and thumb.sub_ver > 0 and thumb.delete_state = 0) as has_thumbnail, dv.created as version_created, vi.size, vi.storage_name, vi.storage_ref, vi.staging_ref, vi.flags, vi.hash, vi.synced_at";
                const string _FILE_JOINS =
                    $@"left join doc_info as di on di.file = d.id
                       inner join (
                           select dvi.parent, max(dvi.ver) as max_ver, count(*) as version_count
                           from doc_version as dvi
                           inner join version_info as vii on vii.id = dvi.id and (vii.flags & 64) > 0
                           where dvi.sub_ver = 0 and dvi.delete_state = 0
                           group by dvi.parent
                       ) as latest on latest.parent = d.id
                       inner join doc_version as dv on dv.parent = d.id and dv.ver = latest.max_ver and dv.sub_ver = 0 and dv.delete_state = 0
                       inner join version_info as vi on vi.id = dv.id and (vi.flags & 64) > 0
                       inner join name_store as ns on ns.id = d.name
                       inner join vault as v on v.id = ns.name
                       left join extension as ext on ext.id = ns.extension";
                const string _FILE_JOINS_ALL =
                    $@"left join doc_info as di on di.file = d.id
                       inner join (
                           select dvi.parent, coalesce(max(case when dvi.delete_state = 0 then dvi.ver end), max(dvi.ver)) as max_ver, count(*) as version_count
                           from doc_version as dvi
                           inner join version_info as vii on vii.id = dvi.id and (vii.flags & 64) > 0
                           where dvi.sub_ver = 0
                           group by dvi.parent
                       ) as latest on latest.parent = d.id
                       inner join doc_version as dv on dv.parent = d.id and dv.ver = latest.max_ver and dv.sub_ver = 0
                       inner join version_info as vi on vi.id = dv.id and (vi.flags & 64) > 0
                       inner join name_store as ns on ns.id = d.name
                       inner join vault as v on v.id = ns.name
                       left join extension as ext on ext.id = ns.extension";
                const string _FILE_NAME_FILTER = $@"and v.name like {VALUE} and ({EXT} is null or ext.name = {EXT})";
                const string _DIR_COLS  = $@"0 as sort_group, 'folder' as item_type, dir.id, dir.cuid as uid, dir.display_name, dir.actor as actor_id, dir.parent as parent_id, exists(select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = dir.id and left(hd.name, 1) = '.') as is_hidden, '' as virtual_path, dir.delete_state, dir.deleted, null as document_delete_state, null as document_deleted, null as version_delete_state, null as version_deleted, dir.created, dir.modified, null as version_id, null as version_cuid, null as version_no, null as version_count, {DIRECTORY_INFO.HAS_THUMBNAIL} as has_thumbnail, null as version_created, null as size, null as storage_name, null as storage_ref, null as staging_ref, null as flags, null as hash, null as synced_at";
                const string _ORDER_PAGE =
                    $@"order by sr.sort_group asc, sr.display_name asc, sr.id asc
                       limit {LIMIT_ROWS} offset {OFFSET_ROWS};";

                // Recursive CTE prefix — reused by all three RECURSIVE queries.
                const string _CTE =
                    $@"with recursive dir_tree as (
                           select id
                           from directory
                           where id = {PARENT} and workspace = {WSPACE} and delete_state = 0
                           union all
                           select dch.id
                           from directory dch
                           inner join dir_tree dt on dch.parent = dt.id
                           where dch.workspace = {WSPACE} and dch.delete_state = 0
                       ) ";
                const string _CTE_ALL =
                    $@"with recursive dir_tree as (
                           select id
                           from directory
                           where id = {PARENT} and workspace = {WSPACE}
                           union all
                           select dch.id
                           from directory dch
                           inner join dir_tree dt on dch.parent = dt.id
                           where dch.workspace = {WSPACE}
                       ) ";

                // ── Workspace-wide (no directory scope) ───────────────────────────────
                public const string ITEMS_ALL =
                    $@"select *
                       from (
                            select {_DIR_COLS}
                            from directory as dir
                            where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.delete_state = 0 and dir.name like {VALUE}
                            union all
                            select {_FILE_COLS}
                            from document as d
                            {_FILE_JOINS}
                            where {VISIBILITY.FILE} and d.workspace = {WSPACE} and d.delete_state = 0 {_FILE_NAME_FILTER}
                        ) as sr
                        {_ORDER_PAGE}";
                public const string ITEMS_ALL_INCLUDE_DELETED =
                    $@"select *
                       from (
                            select {_DIR_COLS}
                            from directory as dir
                            where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.name like {VALUE}
                            union all
                            select {_FILE_COLS_ALL}
                            from document as d
                            {_FILE_JOINS_ALL}
                            where {VISIBILITY.FILE} and d.workspace = {WSPACE} {_FILE_NAME_FILTER}
                        ) as sr
                        {_ORDER_PAGE}";

                public const string COUNT_DIRS_ALL =
                    $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.delete_state = 0 and dir.name like {VALUE};";
                public const string COUNT_DIRS_ALL_INCLUDE_DELETED =
                    $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.name like {VALUE};";

                public const string COUNT_FILES_ALL =
                    $@"select count(*) from document as d {_FILE_JOINS} where {VISIBILITY.FILE} and d.workspace = {WSPACE} and d.delete_state = 0 {_FILE_NAME_FILTER};";
                public const string COUNT_FILES_ALL_INCLUDE_DELETED =
                    $@"select count(*) from document as d {_FILE_JOINS_ALL} where {VISIBILITY.FILE} and d.workspace = {WSPACE} {_FILE_NAME_FILTER};";

                // ── Single directory — direct children only ────────────────────────────
                public const string ITEMS_IN_DIR =
                    $@"select *
                       from (
                            select {_DIR_COLS}
                            from directory as dir
                            where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.delete_state = 0 and dir.name like {VALUE}
                            union all
                            select {_FILE_COLS}
                            from document as d
                            {_FILE_JOINS}
                            where {VISIBILITY.FILE} and d.workspace = {WSPACE} and d.parent = {PARENT} and d.delete_state = 0 {_FILE_NAME_FILTER}
                        ) as sr
                        {_ORDER_PAGE}";
                public const string ITEMS_IN_DIR_INCLUDE_DELETED =
                    $@"select *
                       from (
                            select {_DIR_COLS}
                            from directory as dir
                            where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.name like {VALUE}
                            union all
                            select {_FILE_COLS_ALL}
                            from document as d
                            {_FILE_JOINS_ALL}
                            where {VISIBILITY.FILE} and d.workspace = {WSPACE} and d.parent = {PARENT} {_FILE_NAME_FILTER}
                        ) as sr
                        {_ORDER_PAGE}";

                public const string COUNT_DIRS_IN_DIR =
                    $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.delete_state = 0 and dir.name like {VALUE};";
                public const string COUNT_DIRS_IN_DIR_INCLUDE_DELETED =
                    $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and {EXT} is null and dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.name like {VALUE};";

                public const string COUNT_FILES_IN_DIR =
                    $@"select count(*) from document as d {_FILE_JOINS} where {VISIBILITY.FILE} and d.workspace = {WSPACE} and d.parent = {PARENT} and d.delete_state = 0 {_FILE_NAME_FILTER};";
                public const string COUNT_FILES_IN_DIR_INCLUDE_DELETED =
                    $@"select count(*) from document as d {_FILE_JOINS_ALL} where {VISIBILITY.FILE} and d.workspace = {WSPACE} and d.parent = {PARENT} {_FILE_NAME_FILTER};";

                // ── Recursive subtree from a directory ────────────────────────────────
                public const string ITEMS_RECURSIVE = _CTE +
                    $@"select *
                       from (
                            select {_DIR_COLS}
                            from directory as dir
                            where {VISIBILITY.FOLDER} and {EXT} is null and dir.id in (select id from dir_tree) and dir.id != {PARENT} and dir.delete_state = 0 and dir.name like {VALUE}
                            union all
                            select {_FILE_COLS}
                            from document as d
                            {_FILE_JOINS}
                            where {VISIBILITY.FILE} and d.parent in (select id from dir_tree) and d.delete_state = 0 {_FILE_NAME_FILTER}
                        ) as sr
                        {_ORDER_PAGE}";
                public const string ITEMS_RECURSIVE_INCLUDE_DELETED = _CTE_ALL +
                    $@"select *
                       from (
                            select {_DIR_COLS}
                            from directory as dir
                            where {VISIBILITY.FOLDER} and {EXT} is null and dir.id in (select id from dir_tree) and dir.id != {PARENT} and dir.name like {VALUE}
                            union all
                            select {_FILE_COLS_ALL}
                            from document as d
                            {_FILE_JOINS_ALL}
                            where {VISIBILITY.FILE} and d.parent in (select id from dir_tree) {_FILE_NAME_FILTER}
                        ) as sr
                        {_ORDER_PAGE}";

                public const string COUNT_DIRS_RECURSIVE = _CTE +
                    $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and {EXT} is null and dir.id in (select id from dir_tree) and dir.id != {PARENT} and dir.delete_state = 0 and dir.name like {VALUE};";
                public const string COUNT_DIRS_RECURSIVE_INCLUDE_DELETED = _CTE_ALL +
                    $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and {EXT} is null and dir.id in (select id from dir_tree) and dir.id != {PARENT} and dir.name like {VALUE};";

                public const string COUNT_FILES_RECURSIVE = _CTE +
                    $@"select count(*) from document as d {_FILE_JOINS} where {VISIBILITY.FILE} and d.parent in (select id from dir_tree) and d.delete_state = 0 {_FILE_NAME_FILTER};";
                public const string COUNT_FILES_RECURSIVE_INCLUDE_DELETED = _CTE_ALL +
                    $@"select count(*) from document as d {_FILE_JOINS_ALL} where {VISIBILITY.FILE} and d.parent in (select id from dir_tree) {_FILE_NAME_FILTER};";
            }
        }
    }
}
