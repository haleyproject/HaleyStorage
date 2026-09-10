using static Haley.Internal.IndexingConstant;

namespace Haley.Internal {
    internal partial class IndexingQueries {
        public class WORKSPACE {
            public const string EXISTS = $@"select ws.id from workspace as ws where ws.name = {NAME} and ws.parent = {PARENT} LIMIT 1;";
            public const string EXISTS_BY_CUID = $@"select ws.id from workspace as ws where ws.cuid = {CUID} LIMIT 1;";
            public const string GET_CUID_BY_ID = $@"select ws.cuid from workspace as ws where ws.id = {ID} LIMIT 1;";
            public const string GET_CUIDS_BY_MODULE_CUID = $@"select ws.cuid
                                                              from workspace as ws
                                                              inner join module as m on m.id = ws.parent
                                                              where m.cuid = {CUID}
                                                              order by ws.id;";
            public const string GET_BY_CUID = $@"select ws.id, ws.cuid, ws.display_name, ws.storagename_mode, ws.storagename_parse,
                                                       ws.storage_ref, ws.is_virtual, ws.case_sensitive, ws.storage_profile as workspace_profile_id,
                                                       m.id as module_id, m.cuid as module_cuid, m.name as module_name,
                                                       m.display_name as module_display_name, m.storage_profile as module_profile_id,
                                                       c.id as client_id, c.name as client_name, c.display_name as client_display_name
                                                from workspace as ws
                                                inner join module as m on m.id = ws.parent
                                                inner join client as c on c.id = m.parent
                                                where ws.cuid = {CUID}
                                                limit 1;";
            /// <summary>Plain insert — called only after EXISTS_BY_CUID confirms the workspace does not yet exist. INSERT IGNORE handles the rare concurrent-race edge case without consuming an AUTO_INCREMENT id.</summary>
            public const string INSERT = $@"insert ignore into workspace (parent,name,display_name,guid,cuid,storagename_mode,storagename_parse,storage_ref,is_virtual,case_sensitive) values ({PARENT},{NAME},{DNAME},{GUID},{CUID},{STORAGENAME_MODE},{STORAGENAME_PARSE},{STORAGE_REF},{IS_VIRTUAL},{CASE_SENSITIVE});";
            public const string UPDATE = $@"update workspace set display_name={DNAME},storagename_mode={STORAGENAME_MODE},storagename_parse={STORAGENAME_PARSE},storage_ref={STORAGE_REF},is_virtual={IS_VIRTUAL},case_sensitive={CASE_SENSITIVE} where id={ID};";
            public const string UPDATE_STORAGE_PROFILE_BY_CUID = $@"update workspace set storage_profile = {STORAGE_PROFILE} where cuid = {CUID};";
            public const string UPDATE_STORAGE_PROFILE_BY_ID = $@"update workspace set storage_profile = {STORAGE_PROFILE} where id = {ID};";
            /// <summary>Returns all workspaces that have a storage_profile assigned, with resolved provider name strings.</summary>
            public const string GET_ALL_PROFILES_WITH_KEYS =
                $@"select ws.cuid, pi.id as profile_info_id, pi.mode, sp.name as storage_provider_key, stp.name as staging_provider_key
                   from workspace as ws
                   inner join profile_info as pi on pi.id = ws.storage_profile
                   left join provider as sp on sp.id = pi.storage_provider
                   left join provider as stp on stp.id = pi.staging_provider
                   where ws.storage_profile IS NOT NULL;";
        }
    }
}
