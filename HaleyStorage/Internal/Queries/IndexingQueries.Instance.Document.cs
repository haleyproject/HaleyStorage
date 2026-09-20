using static Haley.Internal.IndexingConstant;

namespace Haley.Internal {
    internal partial class IndexingQueries {
        public partial class INSTANCE {
            public class DOCUMENT {
                public const string LOCK_DELETE_STATE = "select delete_state from document where id = @ID for update;";
                public const string GET_WORKSPACE_BY_CUID =
                    $@"select d.workspace from document as d where d.cuid = {VALUE} limit 1;";
                public const string EXISTS = $@"select doc.id , doc.cuid as uid from document as doc where doc.parent = {PARENT} and doc.name = {NAME} and doc.delete_state = 0;";
                public const string EXISTS_BY_CUID = $@"select doc.id from document as doc where doc.cuid = {CUID} and doc.delete_state = 0;";
                public const string EXISTS_DELETED = $@"select doc.id, doc.cuid as uid from document as doc where doc.parent = {PARENT} and doc.name = {NAME} and doc.delete_state > 0;";
                public const string EXISTS_BY_CUID_ALL = $@"select doc.id from document as doc where doc.cuid = {CUID} limit 1;";
                public const string INSERT = $@"insert ignore into document (workspace,parent,name) values ({WSPACE},{PARENT},{NAME});";
                public const string INSERT_INFO = $@"insert into doc_info (file,display_name,actor) values ({PARENT}, {DNAME}, {ACTOR}) ON DUPLICATE KEY UPDATE display_name = VALUES(display_name);";
                public const string GET_BY_PARENT = $@"select doc.id from document as doc where doc.parent= {PARENT} and doc.name = {NAME} and doc.delete_state = 0;";
                public const string GET_BY_CUID = $@"select doc.id from document as doc where doc.cuid = {CUID} and doc.delete_state = 0;";
                public const string COUNT_BY_DIRECTORY =
                    $@"select count(*)
                         from document as doc
                        where doc.workspace = {WSPACE}
                          and doc.parent = {PARENT}
                          and doc.delete_state = 0
                          and exists (
                              select 1
                                from doc_version dv
                                inner join version_info vi on vi.id = dv.id
                               where dv.parent = doc.id
                                 and dv.sub_ver = 0
                                 and dv.delete_state = 0
                                 and (vi.flags & 64) > 0
                          );";
                public const string COUNT_BY_DIRECTORY_ALL =
                    $@"select count(*)
                         from document as doc
                        where doc.workspace = {WSPACE}
                          and doc.parent = {PARENT}
                          and exists (
                              select 1
                                from doc_version dv
                                inner join version_info vi on vi.id = dv.id
                               where dv.parent = doc.id
                                 and dv.sub_ver = 0
                                 and (vi.flags & 64) > 0
                          );";
                public const string GET_DETAILS_BY_ID =
                    $@"select d.id as document_id, d.cuid as document_cuid, d.workspace as workspace_id, d.delete_state, d.deleted, dir.id as directory_id, dir.cuid as directory_cuid, dir.display_name as directory_name, dir.actor as directory_actor_id, dir.parent as directory_parent_id, coalesce(di.display_name, '') as display_name, di.metadata as doc_metadata, di.actor as document_actor_id
                       from document as d
                       left join doc_info as di on di.file = d.id
                       inner join directory as dir on dir.id = d.parent and dir.delete_state = 0
                       where d.id = {ID} and d.delete_state = 0
                       limit 1;";
                public const string GET_DETAILS_BY_ID_ALL =
                    $@"select d.id as document_id, d.cuid as document_cuid, d.workspace as workspace_id, d.delete_state, d.deleted, dir.id as directory_id, dir.cuid as directory_cuid, dir.display_name as directory_name, dir.actor as directory_actor_id, dir.parent as directory_parent_id, coalesce(di.display_name, '') as display_name, di.metadata as doc_metadata, di.actor as document_actor_id
                       from document as d
                       left join doc_info as di on di.file = d.id
                       left join directory as dir on dir.id = d.parent
                       where d.id = {ID}
                       limit 1;";
                public const string GET_LIFECYCLE_BY_ID =
                    $@"select d.id as document_id, d.cuid as document_cuid, d.workspace as workspace_id, d.parent as directory_id, d.name as current_name_id, d.original_name as original_name_id, d.delete_state, d.deleted, concat(cv.name, case when cext.name = 'default' then '' else concat('.', cext.name) end) as current_file_name, concat(coalesce(ov.name, cv.name), case when coalesce(oext.name, cext.name) = 'default' then '' else concat('.', coalesce(oext.name, cext.name)) end) as restore_file_name
                       from document as d
                       inner join name_store as cns on cns.id = d.name
                       inner join vault as cv on cv.id = cns.name
                       inner join extension as cext on cext.id = cns.extension
                       left join name_store as ons on ons.id = d.original_name
                       left join vault as ov on ov.id = ons.name
                       left join extension as oext on oext.id = ons.extension
                       where d.id = {ID}
                       limit 1;";
                public const string GET_LIFECYCLE_BY_CUID =
                    $@"select d.id as document_id, d.cuid as document_cuid, d.workspace as workspace_id, d.parent as directory_id, d.name as current_name_id, d.original_name as original_name_id, d.delete_state, d.deleted, concat(cv.name, case when cext.name = 'default' then '' else concat('.', cext.name) end) as current_file_name, concat(coalesce(ov.name, cv.name), case when coalesce(oext.name, cext.name) = 'default' then '' else concat('.', coalesce(oext.name, cext.name)) end) as restore_file_name
                       from document as d
                       inner join name_store as cns on cns.id = d.name
                       inner join vault as cv on cv.id = cns.name
                       inner join extension as cext on cext.id = cns.extension
                       left join name_store as ons on ons.id = d.original_name
                       left join vault as ov on ov.id = ons.name
                       left join extension as oext on oext.id = ons.extension
                       where d.cuid = {CUID}
                       limit 1;";
                public const string GET_META_BY_CUID =
                    $@"select di.metadata from doc_info as di inner join document as d on d.id = di.file where d.cuid = {CUID} and d.delete_state = 0 limit 1;";
                public const string UPSERT_META =
                    $@"insert into doc_info (file, display_name, metadata) select d.id, coalesce(di.display_name, ''), {METADATA} from document as d left join doc_info as di on di.file = d.id where d.cuid = {CUID} and d.delete_state = 0 limit 1 on duplicate key update metadata = VALUES(metadata);";
                public const string GET_BY_NAME =
                    $@"select dv.id
                       from document as dv
                       inner join (
                           select ns.id
                           from name_store as ns
                           inner join (select vin.id from vault as vin where vin.name = {NAME}) as v on v.id = ns.name
                           inner join extension as ext on ext.id = ns.extension
                           where ext.name = {EXT}
                       ) as ons on ons.id = dv.name
                        inner join (
                            select dir.id
                            from directory as dir
                            where dir.workspace = {WSPACE} and dir.parent = {PARENT} and dir.name = {DIRNAME} and dir.delete_state = 0
                         ) as odir on odir.id = dv.parent
                         where dv.delete_state = 0;";
                public const string GET_IDS_BY_PARENT_ALL = $@"select doc.id from document as doc where doc.parent = {PARENT};";
                public const string SOFT_DELETE_BY_ID = $@"update document set delete_state = 1, deleted = {DELETED} where id = {ID};";
                public const string RESTORE_BY_ID = $@"update document set delete_state = 0, deleted = null where id = {ID} and delete_state in (1,2);";
                public const string ARCHIVE_RENAME = $@"update document set name = {NAME}, original_name = case when original_name is null then {ORIGINAL_NAME} else original_name end, delete_state = 2 where id = {ID} and delete_state > 0;";
                public const string RESTORE_NAME = $@"update document set name = coalesce(original_name, name), original_name = null where id = {ID} and delete_state in (1,2);";
            }
        }
    }
}
