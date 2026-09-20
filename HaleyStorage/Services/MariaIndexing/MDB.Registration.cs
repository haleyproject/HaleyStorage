using Haley.Abstractions;
using Haley.Models;
using System.Text;
using Microsoft.Extensions.Logging;
using static Haley.Internal.IndexingConstant;
using static Haley.Internal.IndexingQueries;

namespace Haley.Utils {
    /// <summary>
    /// Partial class — vault hierarchy registration (client, module, workspace) and document registration.
    /// </summary>
    internal partial class MariaDBIndexing {
        /// <summary>
        /// Public entry point for document registration. Delegates to <c>RegisterDocumentsInternal</c>.
        /// </summary>
        /// <param name="holder">Receives the assigned DB ID and CUID after successful registration.</param>
        public async Task<(long id, Guid guid)> RegisterDocuments(IVaultReadRequest request, IVaultStorable holder) {
            return await RegisterDocumentsInternal(request, holder);
        }

        /// <summary>
        /// Creates a new content <c>doc_version</c> row (sub_ver=0) under the document that owns
        /// <paramref name="versionCuid"/>. Navigates: versionCuid → doc_version.parent → document.id
        /// → insert new doc_version (MAX(ver)+1, sub_ver=0).
        /// Filename and directory are irrelevant — only the CUID is used to locate the parent document.
        /// Opens a transaction keyed by <paramref name="callId"/> so <c>UpdateDocVersionInfo</c>
        /// can commit or rollback the whole unit in the coordinator's finally block.
        /// </summary>
        public async Task<(long id, Guid guid, int version)> RegisterNewDocVersion(string moduleCuid, string versionCuid, long? actor = null, string callId = null) {
            try {
                if (string.IsNullOrWhiteSpace(moduleCuid)) throw new ArgumentNullException(nameof(moduleCuid));
                if (string.IsNullOrWhiteSpace(versionCuid)) throw new ArgumentNullException(nameof(versionCuid));
                if (!_agw.ContainsKey(moduleCuid)) throw new ArgumentException($"No adapter found for module {moduleCuid}.");

                var handlerKey = GetHandlerKey(callId, moduleCuid);
                var handler = _agw.GetTransactionHandler(moduleCuid);
                if (handler != null && !string.IsNullOrWhiteSpace(callId)) {
                    if (_handlers.ContainsKey(handlerKey)) throw new Exception($"A transaction with key {handlerKey} already exists.");
                    _handlers.TryAdd(handlerKey, (handler, DateTime.UtcNow));
                    handler.Begin();
                }

                var load = new DbExecutionLoad(default, handler);
                var actorValue = actor ?? 0L;

                // 1. Resolve the parent document ID from the existing version CUID.
                var docId = await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.DOCVERSION.GET_DOCUMENT_ID_BY_VERSION_CUID, load, (VALUE, ToDbCuid(versionCuid)));
                if (!docId.HasValue || docId.Value < 1)
                    throw new ArgumentException($"No document found for version CUID '{versionCuid}'.");

                var documentState = await _agw.ScalarAsync<int?>(moduleCuid, INSTANCE.DOCUMENT.LOCK_DELETE_STATE, load, (ID, docId.Value));
                if (documentState != 0) throw new InvalidOperationException("Cannot add a version to a deleted document.");

                // 2. Determine next content version number (sub_ver=0 only).
                // Use all historical versions, not only active ones, so version numbers are never reused.
                var currentMax = await _agw.ScalarAsync<int?>(moduleCuid, INSTANCE.DOCVERSION.FIND_MAX_CONTENT_VERSION, load, (PARENT, docId.Value));
                int nextVersion = (currentMax ?? 0) + 1;

                // 3. Insert the new content doc_version row (sub_ver defaults to 0).
                await _agw.ExecAsync(moduleCuid, INSTANCE.DOCVERSION.INSERT, load, (PARENT, docId.Value), (VERSION, nextVersion), (ACTOR, actorValue));

                // 4. Fetch back the new row to get its auto-generated id and cuid.
                var dvRow = await _agw.RowAsync(moduleCuid, INSTANCE.DOCVERSION.EXISTS, load, (PARENT, docId.Value), (VERSION, nextVersion));
                if (dvRow == null || dvRow.Count < 1)
                    throw new Exception($"Unable to retrieve new doc_version for document {docId.Value}, version {nextVersion}.");

                long newId  = dvRow.GetLong("id");
                string newUid = dvRow.GetString("uid");

                if (newId < 1 || string.IsNullOrWhiteSpace(newUid))
                    throw new Exception("New doc_version row has an invalid id or uid.");

                if (!newUid.IsValidGuid(out Guid newGuid) && !newUid.IsCompactGuid(out newGuid))
                    throw new Exception($"Unable to parse GUID from new doc_version uid '{newUid}'.");

                return (newId, newGuid, nextVersion);
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                var handlerKey = GetHandlerKey(callId, moduleCuid);
                if (!string.IsNullOrWhiteSpace(handlerKey) && _handlers.ContainsKey(handlerKey)) {
                    _handlers[handlerKey].handler?.Rollback();
                    _handlers.Remove(handlerKey, out _);
                }
                if (ThrowExceptions) throw;
                return (0, Guid.Empty, 0);
            }
        }
        /// <summary>
        /// Upserts a client record in the core DB and updates its signing/encrypt/password keys.
        /// If the client already exists, only the display name, path, and keys are updated.
        /// Validates the DB schema via <c>EnsureValidation</c> before executing.
        /// </summary>
        public async Task<IFeedback> RegisterClient(IVaultClient info) {
            if (info == null) throw new ArgumentNullException("Input client directory info cannot be null");
            if (!info.TryValidate(out var msg)) throw new ArgumentException(msg);
            //We generate the hash_guid ourselves for the client.
            await EnsureValidation();

            //Do we even need to check if the client exists? Why dont' we directly upsert the values??? We need to check, because, if we try upsert, then each time , we end up with a new autogenerated id that is not consumed. So, we might end up with all ids' consumed in years. For safer side, we use upsert, also, we check if id exists and try to update separately.

            var cliId = await _agw.ScalarAsync<long?>(_key, CLIENT.EXISTS, default, (NAME, info.Name));
            var thandler = _agw.GetTransactionHandler(_key);
            if (cliId.HasValue) {
                using (thandler.Begin()) {
                    var load = new DbExecutionLoad(default, thandler);
                    await _agw.ExecAsync(_key, CLIENT.UPDATE, load, (DNAME, info.DisplayName), (ID, cliId.Value));
                    await _agw.ExecAsync(_key, CLIENT.UPSERTKEYS, load, (ID, cliId.Value), (SIGNKEY, info.SigningKey), (ENCRYPTKEY, info.EncryptKey), (PASSWORD, info.PasswordHash));
                }
            } else {
                using (thandler.Begin()) {
                    var load = new DbExecutionLoad(default, thandler);
                    await _agw.ExecAsync(_key, CLIENT.INSERT, load, (NAME, info.Name), (DNAME, info.DisplayName), (GUID, info.Guid.ToString("N")));
                    var clientId = await _agw.ScalarAsync<long?>(_key, CLIENT.EXISTS, load, (NAME, info.Name));
                    if (clientId.HasValue) {
                        await _agw.ExecAsync(_key, CLIENT.UPSERTKEYS, load, (ID, clientId.Value), (SIGNKEY, info.SigningKey), (ENCRYPTKEY, info.EncryptKey), (PASSWORD, info.PasswordHash));
                    }
                }
            }
            return await ValidateAndCache(CLIENT.EXISTS, "Client", info, null, (NAME, info.Name));
        }
        /// <summary>
        /// Upserts a module record in the core DB.
        /// If the module is new, also creates the per-module MariaDB schema via <c>CreateModuleDBInstance</c>
        /// (triggered through <c>ValidateAndCache</c>'s preProcess callback) and adds an adapter entry
        /// under the module's CUID.
        /// </summary>
        public async Task<IFeedback> RegisterModule(IVaultModule info) {
            if (info == null) throw new ArgumentNullException("Input Module directory info cannot be null");
            if (!info.TryValidate(out var msg)) throw new ArgumentNullException(msg);
            //We generate the hash_guid ourselves for the client.
            await EnsureValidation();

            //Check if client exists. If not throw exeception or don't register? //Send feedback.
            //var cexists = await _agw.Scalar(new AdapterArgs(_key) { Query = CLIENT.EXISTS }, (NAME, info.Client.Name));
            //if (cexists == null || !(cexists is int clientId)) throw new ArgumentException($@"Client {info.Client.Name} doesn't exist. Unable to index the module {info.DisplayName}.");
            //var mexists = await _agw.Scalar(new AdapterArgs(_key) { Query = MODULE.EXISTS }, (NAME, info.Name), (PARENT, clientId));
            var mId = await _agw.ScalarAsync<long?>(_key, MODULE.EXISTS_BY_CUID, default, (CUID, info.Cuid.ToString("N")));
            if (mId.HasValue) {
                await _agw.ExecAsync(_key, MODULE.UPDATE, default, (DNAME, info.DisplayName), (ID, mId.Value));
            } else {
                var clientId = await _agw.ScalarAsync<long?>(_key, CLIENT.EXISTS, default, (NAME, info.Client.Name));
                if (!clientId.HasValue) throw new ArgumentException($@"Client {info.Client.Name} doesn't exist. Unable to index the module {info.DisplayName}.");
                await _agw.ExecAsync(_key, MODULE.INSERT, default, (PARENT, clientId.Value), (NAME, info.Name), (DNAME, info.DisplayName), (GUID, info.Guid.ToString("N")), (CUID, info.Cuid.ToString("N")));
            }
            return await ValidateAndCache(MODULE.EXISTS_BY_CUID, "Module", info, CreateModuleDBInstance, (CUID, info.Cuid.ToString("N")));
        }
        /// <summary>
        /// Upserts a workspace record in the core DB.
        /// On insert, validates the parent module exists first; on update, patches display name, path, and control/parse modes.
        /// </summary>
        public async Task<IFeedback> RegisterWorkspace(IVaultWorkSpace info) {
            if (info == null) throw new ArgumentNullException("Input Module directory info cannot be null");
            if (!info.TryValidate(out var msg)) throw new ArgumentNullException(msg);
            //We generate the hash_guid ourselves for the client.
            await EnsureValidation();
            var wsCuid = info.Cuid.ToString("N");
            var wsId = await _agw.ScalarAsync<long?>(_key, WORKSPACE.EXISTS_BY_CUID, default, (CUID, wsCuid));
            if (wsId.HasValue) {
                await _agw.ExecAsync(_key, WORKSPACE.UPDATE, default, (DNAME, info.DisplayName), (STORAGENAME_MODE, (int)info.NameMode), (STORAGENAME_PARSE, (int)info.ParseMode), (STORAGE_REF, info.StorageRef), (IS_VIRTUAL, info.IsVirtual), (CASE_SENSITIVE, info.CaseSensitive), (ID, wsId.Value));
            } else {
                var moduleCuid = StorageUtils.GenerateCuid(info.Client.Name, info.Module.Name);
                var modId = await _agw.ScalarAsync<long?>(_key, MODULE.EXISTS_BY_CUID, default, (CUID, moduleCuid));
                if (!modId.HasValue) throw new ArgumentException($@"Module {info.Module.Name} doesn't exist. Unable to index the module {info.DisplayName}.");
                await _agw.ExecAsync(_key, WORKSPACE.INSERT, default, (PARENT, modId.Value), (NAME, info.Name), (DNAME, info.DisplayName), (GUID, info.Guid.ToString("N")), (CUID, wsCuid), (STORAGENAME_MODE, (int)info.NameMode), (STORAGENAME_PARSE, (int)info.ParseMode), (STORAGE_REF, info.StorageRef), (IS_VIRTUAL, info.IsVirtual), (CASE_SENSITIVE, info.CaseSensitive));
                //Cross check
                wsId = await _agw.ScalarAsync<long?>(_key, WORKSPACE.EXISTS_BY_CUID, default, (CUID, wsCuid));
                if (wsId == null || !wsId.HasValue) throw new ArgumentException($@"Workspace {info.Name} with CUID {wsCuid} was not created. Please check..");
            }
            var result = await ValidateAndCache(WORKSPACE.EXISTS_BY_CUID, "Workspace", info, null, (CUID, wsCuid));
            if (result.Status) {
                // Registration can update structural workspace settings. Refresh those values immediately,
                // while retaining any independently configured storage profile already held in memory.
                if (TryGetComponentInfo<VaultWorkSpace>(wsCuid, out var cached)) {
                    info.StorageProviderKey = cached.StorageProviderKey;
                    info.StagingProviderKey = cached.StagingProviderKey;
                    info.ProfileMode = cached.ProfileMode;
                    info.ProfileInfoId = cached.ProfileInfoId;
                }
                TryAddInfo(info, replace: true);
            }
            return result;
        }
        /// <summary>
        /// Creates the core <c>dss_core</c> schema from the <c>dsscore.sql</c> template if it does not already exist.
        /// Called once at startup via <c>EnsureValidation</c>.
        /// </summary>
        async Task<IFeedback> CreateCoreDB() {
            //var toReplace = new Dictionary<string, string> { ["lifecycle_state"] = }
               return await _agw.BootstrapDatabaseAsync(new DatabaseBootstrapArgs(_key) {
                   ContentProcessor = (content, dbname) => {
                       return content.Replace(DB_CORE_SEARCH_TERM, dbname);
                   },
                   FallbackDatabaseName = DB_CORE_FALLBACK_NAME,
                   SqlContent = Encoding.UTF8.GetString(ResourceUtils.GetEmbeddedResource(EMBEDDED_DBCORE_FILE))
               });
        }

        /// <summary>
        /// Creates a per-module MariaDB schema from the <c>dssclient.sql</c> template if it does not already exist,
        /// then duplicates the adapter gateway entry under the module's CUID so all subsequent per-module
        /// queries target the correct database.
        /// </summary>
        async Task CreateModuleDBInstance(IVaultObject dirInfo) {
            if (!(dirInfo is IVaultModule info)) return;
            if (string.IsNullOrWhiteSpace(info.DatabaseName)) info.DatabaseName = $@"{DB_MODULE_NAME_PREFIX}{info.Cuid.ToString("N")}";
            //So, when we create the module, we use the cuid as the database name.
            //TODO : IF A CUID IS CHANGED, THEN WE NEED TO UPDATE THE DATABASE NAME IN THE DB.
            var result = await _agw.BootstrapDatabaseAsync(new DatabaseBootstrapArgs(info.Cuid.ToString("N")) {
                ContentProcessor = (content, dbname) => {
                    return content.Replace(DB_CLIENT_SEARCH_TERM, dbname); },
                FallbackDatabaseName = info.DatabaseName,
                DatabaseName = info.DatabaseName,
                SqlContent = Encoding.UTF8.GetString(ResourceUtils.GetEmbeddedResource(EMBEDDED_DBCLIENT_FILE)),
                CloningAdapterKey = _key }).ConfigureAwait(false);
            if (result is null || !result.Status)
                throw new InvalidOperationException(
                    result?.Message ?? $"Unable to initialize module database '{info.DatabaseName}'.");
        }
    }
}
