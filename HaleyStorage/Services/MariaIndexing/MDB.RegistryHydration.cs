using Haley.Enums;
using Haley.Models;
using static Haley.Internal.IndexingConstant;
using static Haley.Internal.IndexingQueries;

namespace Haley.Utils {
    internal partial class MariaDBIndexing {
        public async Task<bool> HydrateModuleAsync(string moduleCuid) {
            if (string.IsNullOrWhiteSpace(moduleCuid)) return false;
            if (TryGetComponentInfo<VaultModule>(moduleCuid, out var cachedModule)) {
                await EnsureValidation();
                if (!_agw.ContainsKey(moduleCuid))
                    await CreateModuleDBInstance(cachedModule);
                return _agw.ContainsKey(moduleCuid);
            }

            await EnsureValidation();
            var row = await _agw.RowAsync(_key, MODULE.GET_BY_CUID, default, (CUID, moduleCuid));
            if (row == null || row.Count == 0) return false;

            var clientName = row.GetString("client_name");
            var clientDisplayName = row.GetString("client_display_name");
            var moduleName = row.GetString("name");
            var moduleDisplayName = row.GetString("display_name");
            if (string.IsNullOrWhiteSpace(clientName) || string.IsNullOrWhiteSpace(moduleName))
                return false;

            var client = new VaultClient("registry", "registry", "registry", clientDisplayName ?? clientName) {
                Id = row.GetLong("client_id")
            };
            TryAddInfo(client);

            var module = new VaultModule(clientName, moduleDisplayName ?? moduleName) {
                Id = row.GetLong("id"),
                DatabaseName = $"{DB_MODULE_NAME_PREFIX}{moduleCuid}"
            };
            module.SetCuid(moduleCuid);
            TryAddInfo(module);

            if (!_agw.ContainsKey(moduleCuid))
                await CreateModuleDBInstance(module);

            var moduleProfileId = row.GetLong("module_profile_id");
            if (moduleProfileId > 0)
                await HydrateModuleProfileAsync(moduleCuid, (int)moduleProfileId);

            return true;
        }

        public bool IsModuleAdapterRegistered(string moduleCuid)
            => !string.IsNullOrWhiteSpace(moduleCuid) && _agw.ContainsKey(moduleCuid);

        public async Task<IReadOnlyList<string>> GetWorkspaceCuidsAsync(string moduleCuid) {
            if (string.IsNullOrWhiteSpace(moduleCuid)) return Array.Empty<string>();
            await EnsureValidation();
            var rows = await _agw.RowsAsync(_key, WORKSPACE.GET_CUIDS_BY_MODULE_CUID, default, (CUID, moduleCuid));
            return rows
                .Select(row => row.GetString("cuid"))
                .Where(cuid => !string.IsNullOrWhiteSpace(cuid))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Loads a workspace from the persisted registry after a cache miss, but only when its
        /// module adapter is already active in this process. Module activation remains explicit.
        /// </summary>
        public async Task<bool> HydrateWorkspaceAsync(string workspaceCuid, bool forceRefresh = false) {
            if (string.IsNullOrWhiteSpace(workspaceCuid)) return false;
            if (!forceRefresh && TryGetComponentInfo<VaultWorkSpace>(workspaceCuid, out _)) return true;

            await EnsureValidation();
            var row = await _agw.RowAsync(_key, WORKSPACE.GET_BY_CUID, default, (CUID, workspaceCuid));
            if (row == null || row.Count == 0) return false;

            var clientName = row.GetString("client_name");
            var clientDisplayName = row.GetString("client_display_name");
            var moduleName = row.GetString("module_name");
            var moduleDisplayName = row.GetString("module_display_name");
            var workspaceDisplayName = row.GetString("display_name");
            var moduleCuid = row.GetString("module_cuid");
            if (string.IsNullOrWhiteSpace(clientName)
                || string.IsNullOrWhiteSpace(moduleName)
                || string.IsNullOrWhiteSpace(workspaceDisplayName)
                || string.IsNullOrWhiteSpace(moduleCuid))
                return false;

            if (!IsModuleAdapterRegistered(moduleCuid)
                || !TryGetComponentInfo<VaultModule>(moduleCuid, out _))
                return false;

            var isVirtual = ReadRegistryBoolean(row, "is_virtual");
            var caseSensitive = ReadRegistryBoolean(row, "case_sensitive");
            var workspace = new VaultWorkSpace(clientName, moduleName, workspaceDisplayName, isVirtual) {
                Id = row.GetLong("id"),
                StorageRef = row.GetString("storage_ref") ?? string.Empty,
                Base = Path.Combine(
                    caseSensitive ? clientDisplayName ?? clientName : clientName.ToDBName(),
                    caseSensitive ? moduleDisplayName ?? moduleName : moduleName.ToDBName()),
                NameMode = (VaultNameMode)row.GetInt("storagename_mode"),
                ParseMode = (VaultNameParseMode)row.GetInt("storagename_parse"),
                CaseSensitive = caseSensitive
            };
            workspace.SetCuid(workspaceCuid);
            TryAddInfo(workspace, replace: true);

            var workspaceProfileId = row.GetLong("workspace_profile_id");
            if (workspaceProfileId > 0)
                await HydrateWorkspaceProfileAsync(workspaceCuid, (int)workspaceProfileId);

            return true;
        }

        public async Task<bool> HydrateWorkspaceByIdAsync(long workspaceId, bool forceRefresh = false) {
            if (workspaceId < 1) return false;
            await EnsureValidation();
            var workspaceCuid = await _agw.ScalarAsync<string>(
                _key,
                WORKSPACE.GET_CUID_BY_ID,
                default,
                (ID, workspaceId));
            return !string.IsNullOrWhiteSpace(workspaceCuid)
                && await HydrateWorkspaceAsync(workspaceCuid, forceRefresh);
        }

        static bool ReadRegistryBoolean(DbRow row, string key) {
            if (!row.TryGetValue(key, out var value) || value == null || value == DBNull.Value)
                return false;
            if (value is bool boolean) return boolean;
            if (long.TryParse(value.ToString(), out var number)) return number != 0;
            return bool.TryParse(value.ToString(), out boolean) && boolean;
        }
    }
}
