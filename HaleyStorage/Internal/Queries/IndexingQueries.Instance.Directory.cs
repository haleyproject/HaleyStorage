using static Haley.Internal.IndexingConstant;

namespace Haley.Internal {
    internal partial class IndexingQueries {
        public partial class INSTANCE {
            public class DIRECTORY {
                public const string EXISTS = $@"select dir.id, dir.cuid as uid from directory as dir where dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.name = {NAME} and dir.delete_state = 0;";
                public const string EXISTS_BY_CUID = $@"select dir.id, dir.cuid as uid from directory as dir where dir.cuid = {VALUE} and dir.delete_state = 0;";
                public const string EXISTS_BY_ID = $@"select dir.id, dir.cuid as uid from directory as dir where dir.id = {VALUE} and dir.delete_state = 0;";
                public const string INSERT = $@"insert ignore into directory (workspace,parent,name,display_name,actor) values ({WSPACE},{PARENT},{NAME},{DNAME},{ACTOR});";
                public const string GET = $@"select dir.id from directory as dir where dir.workspace = {WSPACE} and dir.parent={PARENT} and dir.name ={NAME} and dir.delete_state = 0;";
                public const string GET_BY_CUID = $@"select dir.id from directory as dir where dir.cuid = {CUID} and dir.delete_state = 0;";
                public const string GET_BY_CUID_ALL = $@"select dir.id from directory as dir where dir.cuid = {CUID} limit 1;";
                public const string GET_DETAILS =
                    $@"select dir.id, dir.cuid as uid, dir.name, dir.display_name, dir.actor, dir.parent, dir.workspace, dir.delete_state, dir.deleted, dir.created, dir.modified
                       from directory as dir
                       where dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.name = {NAME} and dir.delete_state = 0
                       limit 1;";
                public const string GET_DETAILS_BY_CUID =
                    $@"select dir.id, dir.cuid as uid, dir.name, dir.display_name, dir.actor, dir.parent, dir.workspace, dir.delete_state, dir.deleted, dir.created, dir.modified
                       from directory as dir
                       where dir.cuid = {VALUE} and dir.delete_state = 0
                       limit 1;";
                public const string GET_DETAILS_BY_ID =
                    $@"select dir.id, dir.cuid as uid, dir.name, dir.display_name, dir.actor, dir.parent, dir.workspace, dir.delete_state, dir.deleted, dir.created, dir.modified
                       from directory as dir
                       where dir.id = {VALUE} and dir.delete_state = 0
                       limit 1;";
                public const string GET_DETAILS_ALL =
                    $@"select dir.id, dir.cuid as uid, dir.name, dir.display_name, dir.actor, dir.parent, dir.workspace, dir.delete_state, dir.deleted, dir.created, dir.modified
                       from directory as dir
                       where dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.name = {NAME}
                       limit 1;";
                public const string GET_DETAILS_BY_CUID_ALL =
                    $@"select dir.id, dir.cuid as uid, dir.name, dir.display_name, dir.actor, dir.parent, dir.workspace, dir.delete_state, dir.deleted, dir.created, dir.modified
                       from directory as dir
                       where dir.cuid = {VALUE}
                       limit 1;";
                public const string GET_DETAILS_BY_ID_ALL =
                    $@"select dir.id, dir.cuid as uid, dir.name, dir.display_name, dir.actor, dir.parent, dir.workspace, dir.delete_state, dir.deleted, dir.created, dir.modified
                       from directory as dir
                       where dir.id = {VALUE}
                       limit 1;";
                public const string COUNT_CHILDREN = $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.delete_state = 0;";
                public const string COUNT_CHILDREN_ALL = $@"select count(*) from directory as dir where {VISIBILITY.FOLDER} and dir.workspace = {WSPACE} and dir.parent = {PARENT};";
                public const string BROWSE_ITEMS =
                    $@"select *
                       from (
                             select 0 as sort_group, 'folder' as item_type, dir.id, dir.cuid as uid, dir.display_name, dir.actor as actor_id, dir.parent as parent_id, exists(select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = dir.id and left(hd.name, 1) = '.') as is_hidden, '' as virtual_path, dir.delete_state, dir.deleted, null as document_delete_state, null as document_deleted, null as version_delete_state, null as version_deleted, dir.created, dir.modified, null as version_id, null as version_cuid, null as version_no, null as version_count, {DIRECTORY_INFO.HAS_THUMBNAIL} as has_thumbnail, null as version_created, null as size, null as storage_name, null as storage_ref, null as staging_ref, null as flags, null as hash, null as synced_at
                             from directory as dir
                             where {VISIBILITY.FOLDER} and dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.delete_state = 0

                             union all

                            select 1 as sort_group, 'file' as item_type, d.id, d.cuid as uid, coalesce(di.display_name, '') as display_name, dv.actor as actor_id, d.parent as parent_id, exists(select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = d.parent and left(hd.name, 1) = '.') as is_hidden, '' as virtual_path, d.delete_state, d.deleted, d.delete_state as document_delete_state, d.deleted as document_deleted, dv.delete_state as version_delete_state, dv.deleted as version_deleted, d.created, d.modified, dv.id as version_id, dv.cuid as version_cuid, dv.ver as version_no, latest.version_count, exists(select 1 from doc_version as thumb where thumb.parent = d.id and thumb.ver = dv.ver and thumb.sub_ver > 0 and thumb.delete_state = 0) as has_thumbnail, dv.created as version_created, vi.size, vi.storage_name, vi.storage_ref, vi.staging_ref, vi.flags, vi.hash, vi.synced_at
                            from document as d
                            left join doc_info as di on di.file = d.id
                            inner join (
                                select dvi.parent, max(dvi.ver) as max_ver, count(*) as version_count
                                from doc_version as dvi
                                inner join version_info as vii on vii.id = dvi.id and (vii.flags & 64) > 0
                                where dvi.sub_ver = 0 and dvi.delete_state = 0
                                group by dvi.parent
                             ) as latest on latest.parent = d.id
                             inner join doc_version as dv on dv.parent = d.id and dv.ver = latest.max_ver and dv.sub_ver = 0 and dv.delete_state = 0
                             inner join version_info as vi on vi.id = dv.id and (vi.flags & 64) > 0
                             where d.workspace = {WSPACE} and d.parent = {PARENT} and d.delete_state = 0
                       ) as browse_items
                       order by browse_items.sort_group asc, browse_items.display_name asc, browse_items.id asc
                       limit {LIMIT_ROWS} offset {OFFSET_ROWS};";
                public const string BROWSE_ITEMS_ALL =
                    $@"select *
                       from (
                             select 0 as sort_group, 'folder' as item_type, dir.id, dir.cuid as uid, dir.display_name, dir.actor as actor_id, dir.parent as parent_id, exists(select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = dir.id and left(hd.name, 1) = '.') as is_hidden, '' as virtual_path, dir.delete_state, dir.deleted, null as document_delete_state, null as document_deleted, null as version_delete_state, null as version_deleted, dir.created, dir.modified, null as version_id, null as version_cuid, null as version_no, null as version_count, {DIRECTORY_INFO.HAS_THUMBNAIL} as has_thumbnail, null as version_created, null as size, null as storage_name, null as storage_ref, null as staging_ref, null as flags, null as hash, null as synced_at
                             from directory as dir
                             where {VISIBILITY.FOLDER} and dir.workspace = {WSPACE} and dir.parent = {PARENT}

                             union all

                            select 1 as sort_group, 'file' as item_type, d.id, d.cuid as uid, coalesce(di.display_name, '') as display_name, dv.actor as actor_id, d.parent as parent_id, exists(select 1 from dir_path hp join directory hd on hd.id = hp.ancestor where hp.descendant = d.parent and left(hd.name, 1) = '.') as is_hidden, '' as virtual_path, case when d.delete_state > 0 then d.delete_state else dv.delete_state end as delete_state, case when d.delete_state > 0 then d.deleted else dv.deleted end as deleted, d.delete_state as document_delete_state, d.deleted as document_deleted, dv.delete_state as version_delete_state, dv.deleted as version_deleted, d.created, d.modified, dv.id as version_id, dv.cuid as version_cuid, dv.ver as version_no, latest.version_count, exists(select 1 from doc_version as thumb where thumb.parent = d.id and thumb.ver = dv.ver and thumb.sub_ver > 0 and thumb.delete_state = 0) as has_thumbnail, dv.created as version_created, vi.size, vi.storage_name, vi.storage_ref, vi.staging_ref, vi.flags, vi.hash, vi.synced_at
                            from document as d
                            left join doc_info as di on di.file = d.id
                            inner join (
                                select dvi.parent, coalesce(max(case when dvi.delete_state = 0 then dvi.ver end), max(dvi.ver)) as max_ver, count(*) as version_count
                                from doc_version as dvi
                                inner join version_info as vii on vii.id = dvi.id and (vii.flags & 64) > 0
                                where dvi.sub_ver = 0
                                group by dvi.parent
                             ) as latest on latest.parent = d.id
                             inner join doc_version as dv on dv.parent = d.id and dv.ver = latest.max_ver and dv.sub_ver = 0
                             inner join version_info as vi on vi.id = dv.id and (vi.flags & 64) > 0
                             where d.workspace = {WSPACE} and d.parent = {PARENT}
                       ) as browse_items
                       order by browse_items.sort_group asc, browse_items.display_name asc, browse_items.id asc
                       limit {LIMIT_ROWS} offset {OFFSET_ROWS};";
                public const string GET_CHILD_IDS_ALL = $@"select dir.id from directory as dir where dir.parent = {PARENT};";
                public const string SOFT_DELETE_BY_ID = $@"update directory set delete_state = 1, deleted = {DELETED} where id = {ID};";
                public const string RESTORE_BY_ID = $@"update directory set delete_state = 0, deleted = null where id = {ID} and delete_state in (1,2);";

                public const string GET_BY_DOC_VERSION_CUID =
                    $@"select dir.display_name, dir.cuid, dir.name
                       from doc_version as dv
                       join document as d on d.id = dv.parent and d.delete_state = 0
                       join directory as dir on dir.id = d.parent and dir.delete_state = 0
                       where dv.cuid = {CUID} and dv.delete_state = 0;";
            }
        }
    }
}
