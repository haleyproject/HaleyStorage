namespace Haley.Internal;

internal partial class IndexingQueries {
    public partial class INSTANCE {
        public class DIRECTORY_INFO {
            public const string ENSURE = "insert ignore into dir_info (dir_id) values (@ID);";
            public const string GET = "select metadata, uri from dir_info where dir_id = @ID;";
            public const string LOCK = "select uri from dir_info where dir_id = @ID for update;";
            public const string SET_METADATA = "insert into dir_info (dir_id, metadata) values (@ID, @VALUE) on duplicate key update metadata = values(metadata);";
            public const string SET_THUMBNAIL = "update dir_info set uri = @VALUE where dir_id = @ID;";
            public const string CLEAR_THUMBNAIL = "update dir_info set uri = null where dir_id = @ID and uri = @VALUE;";
            public const string MANAGED_DOCUMENT = @"select d.id from document d join directory child on child.id = d.parent
                join doc_info di on di.file = d.id where d.cuid = @VALUE and child.parent = @ID and child.name = '.thumb'
                and di.display_name like 'dir-thumb-%';";
            public const string PURGE_DOCUMENT = "update document set delete_state = 3, deleted = coalesce(deleted, UTC_TIMESTAMP()) where id = @ID;";
            public const string PURGE_VERSIONS = "update doc_version set delete_state = 3, deleted = coalesce(deleted, UTC_TIMESTAMP()) where parent = @ID;";
            public const string DELETE_ACTIVE_VERSIONS = "update doc_version set delete_state = 1, deleted = UTC_TIMESTAMP() where parent = @ID and delete_state = 0;";
            public const string LATEST_VERSION = @"select dv.id, dv.ver from doc_version dv join version_info vi on vi.id = dv.id
                where dv.parent = @ID and dv.sub_ver = 0 and dv.delete_state = 0 and (vi.flags & 64) > 0 order by dv.ver desc limit 1 for update;";
            public const string RENAME = "update directory set name = @NAME, display_name = @DNAME where id = @ID;";
            public const string THUMBNAIL_DOCUMENT = @"select d.cuid, d.parent, d.delete_state, concat(v.name, case when e.name = 'default' then '' else e.name end) as file_name,
                exists(select 1 from doc_version dv where dv.parent = d.id) as has_version,
                exists(select 1 from doc_version dv join version_info vi on vi.id = dv.id where dv.parent = d.id and dv.sub_ver = 0 and dv.delete_state = 0 and (vi.flags & 64) > 0) as completed
                from document d join name_store ns on ns.id = d.name join vault v on v.id = ns.name join extension e on e.id = ns.extension where d.cuid = @VALUE;";
            public const string HAS_THUMBNAIL = @"exists(select 1 from dir_info fi join document td on td.cuid = fi.uri and td.delete_state = 0
                join doc_version tv on tv.parent = td.id and tv.sub_ver = 0 and tv.delete_state = 0
                join version_info ti on ti.id = tv.id and (ti.flags & 64) > 0 where fi.dir_id = dir.id)";
        }
    }
}
