using Haley.Abstractions;
using Haley.Models;
using Microsoft.Extensions.Logging;
using System.Xml.Linq;
using static Haley.Internal.IndexingConstant;
using static Haley.Internal.IndexingQueries;

namespace Haley.Utils {
    /// <summary>
    /// Partial class — internal DB helpers for the registration and document-insert pipeline.
    /// Contains the workspace/directory/namestore "ensure" helpers and the low-level
    /// <c>InsertAndFetchID*</c> check-then-insert pattern.
    /// </summary>
    internal partial class MariaDBIndexing {
        /// <summary>
        /// Thread-safe one-shot validation: runs <see cref="CreateCoreDB"/> the first time it is called.
        /// Subsequent calls are no-ops, guarded by a <see cref="SemaphoreSlim"/>.
        /// </summary>
        public async Task EnsureValidation() {
            if (isValidated) return;
            await _validateLock.WaitAsync();
            try {
                if (!isValidated) {
                   var status = await CreateCoreDB();
                    if (status == null || !status.Status) throw new Exception(status.Message);
                    isValidated = true;
                }
            } finally {
                _validateLock.Release();
            }
        }

        /// <summary>
        /// Ensures the workspace row exists in the per-module DB. If absent, inserts it
        /// (insert-ignore by workspace ID). Returns the workspace numeric ID.
        /// Hydrates a persisted workspace on cache miss so runtime-created workspaces can be used
        /// without restarting the storage process.
        /// </summary>
        async Task<(bool status, long id)> EnsureWorkSpace(IVaultReadRequest request) {
            var dbid = request.Scope.Module.Cuid.ToString("N");
            if (!IsModuleAdapterRegistered(dbid))
                throw new InvalidOperationException($@"Module adapter '{dbid}' is not active in this storage process.");

            var wsCuidKey = request.Scope.Workspace.Cuid.ToString("N");
            if (!_cache.TryGetValue(wsCuidKey, out var wspace)) {
                var hydrated = await HydrateWorkspaceAsync(wsCuidKey).ConfigureAwait(false);
                if (!hydrated || !_cache.TryGetValue(wsCuidKey, out wspace))
                    throw new InvalidOperationException($@"Workspace '{wsCuidKey}' is not registered in the storage registry.");
            }

            //Check if workspace exists in the database.
            var ws = await InsertAndFetchIDScalar(dbid, () => (INSTANCE.WORKSPACE.EXISTS, Consolidate((ID, wspace.Id))), () => (INSTANCE.WORKSPACE.INSERT, Consolidate((ID, wspace.Id))), readOnly:request.ReadOnlyMode, $@"Unable to insert the workspace  {wspace.Id}"); //Workspace registration can happen without transaction, as it might be needed for other items later.
            return (ws > 0, ws);
        }

        /// <summary>
        /// Ensures a directory row exists in the per-module DB for the request's folder scope.
        /// Falls back to "default" when no folder is specified on the request.
        /// Returns the directory numeric ID and its compact-N CUID.
        /// </summary>
        async Task<(bool status, (long id, string uid) result)> EnsureDirectory(IVaultReadRequest request, long ws_id) {
            if (ws_id == 0) return (false, (0, string.Empty));
            var dbid = request.Scope.Module.Cuid.ToString("N");
            //If directory name is not provided, then go for "default" as usual
            var dirId = request.Scope.Folder?.Id ?? 0;
            var dirCuid = request.Scope.Folder?.Cuid ?? string.Empty;

            var dirParent = request.Scope.Folder?.Parent?.Id ?? 0;
            var dirName = request.Scope.Folder?.DisplayName ?? VaultConstants.DEFAULT_NAME;
            var dirDbName = dirName.ToDBName();
            var actor = request.Actor ?? 0L;

            Func<(string query, (string key, object value)[] parameters)> checkDirectory = () => {
                    if (dirId < 1 && string.IsNullOrWhiteSpace(dirCuid))
                        return (INSTANCE.DIRECTORY.EXISTS, Consolidate((WSPACE, ws_id), (PARENT, dirParent), (NAME, dirDbName)));
                    var query = dirId < 1 ? INSTANCE.DIRECTORY.EXISTS_BY_CUID : INSTANCE.DIRECTORY.EXISTS_BY_ID;
                    return (query, Consolidate((VALUE, dirId < 1 ? (object)ToDbCuid(dirCuid) : dirId)));
                };
            Func<(string query, (string key, object value)[] parameters)> insertDirectory = () => (INSTANCE.DIRECTORY.INSERT, Consolidate((WSPACE, ws_id), (PARENT, dirParent), (NAME, dirDbName), (DNAME, dirName), (ACTOR, actor)));

            var existing = await InsertAndFetchIDRead(dbid, checkDirectory, readOnly: true, callId: request.CallID);
            var wasCreated = existing.id < 1 && !request.ReadOnlyMode;
            var dirInfo = existing.id > 0
                ? existing
                : await InsertAndFetchIDRead(dbid,
                checkDirectory,
                insertDirectory,
                readOnly: request.ReadOnlyMode,
                $@"Unable to insert the directory {dirName} to the workspace : {ws_id}",
                false,
                callId: request.CallID); //Directory registration, same as the workspace doesn't need any transaction as it might be needed by other systems.

            if (wasCreated && dirInfo.id > 0)
                await TryQueueFolderCreateStatsEvent(dbid, dirInfo.id, new DbExecutionLoad(default, GetTransactionHandlerCache(request.CallID, dbid)));

            return (true, (dirInfo.id, dirInfo.uid));
        }

        /// <summary>
        /// Retrieves an open <see cref="ITransactionHandler"/> from the in-flight handler cache
        /// by <c>callId###dbId</c> key. Returns <c>null</c> when not found (auto-commit mode).
        /// </summary>
        ITransactionHandler GetTransactionHandlerCache(string callId, string dbid) {
            if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(dbid)) return null;
            var key = GetHandlerKey(callId, dbid);
            if (!_handlers.ContainsKey(key)) return null;
            //If handler was created long back, then remove it.
            return _handlers[key].handler;
        }

        /// <summary>
        /// Check-then-insert pattern that returns a single numeric ID.
        /// Runs <paramref name="check"/> first; if the row is absent and <paramref name="readOnly"/> is false,
        /// runs <paramref name="insert"/> then re-checks. Throws on failure.
        /// </summary>
        async Task<long> InsertAndFetchIDScalar(string dbid, Func<(string query,(string key,object value)[] parameters)> check, Func<(string query, (string key, object value)[] parameters)> insert = null, bool readOnly = false, string failureMessage = "Error", bool preCheck = true,string callId = null) {
            if (check == null) return 0;
            ITransactionHandler handler = GetTransactionHandlerCache(callId, dbid);

            var checkInput = check.Invoke();
            var load = new DbExecutionLoad(default, handler, false);
            var checkArgs = Array.ConvertAll(checkInput.parameters, p => (DbArg)p);
            long? id = null;
            if (preCheck) id = await _agw.ScalarAsync<long?>(dbid, checkInput.query, load, checkArgs);

            if (!id.HasValue) {
                if (insert == null || readOnly) return 0;
                var insertInput = insert.Invoke();
                await _agw.ExecAsync(dbid, insertInput.query, load, Array.ConvertAll(insertInput.parameters, p => (DbArg)p));
                id = await _agw.ScalarAsync<long?>(dbid, checkInput.query, load, checkArgs);
            }
            if (!id.HasValue) throw new Exception($@"{failureMessage} from the database {dbid}");
            return id.Value;
        }

        /// <summary>
        /// Check-then-insert pattern that returns both a numeric ID and a CUID string.
        /// Used for rows that have a <c>cuid</c> column (directory, document, doc_version).
        /// Throws when the row cannot be found or inserted.
        /// </summary>
        async Task<(long id, string uid)> InsertAndFetchIDRead(string dbid, Func<(string query, (string key, object value)[] parameters)> check = null, Func<(string query, (string key, object value)[] parameters)> insert = null, bool readOnly = false, string failureMessage = "Error", bool preCheck = true, string callId = null) {
            if (check == null) return (0, string.Empty);
            var checkInput = check.Invoke();
            ITransactionHandler handler = GetTransactionHandlerCache(callId, dbid);

            var load = new DbExecutionLoad(default, handler, false);
            var checkArgs = Array.ConvertAll(checkInput.parameters, p => (DbArg)p);
            DbRow row = null;
            if (preCheck) row = await _agw.RowAsync(dbid, checkInput.query, load, checkArgs);

            if (row == null || row.Count < 1) {
                if (insert == null || readOnly) return (0, string.Empty);
                var insertInput = insert.Invoke();
                await _agw.ExecAsync(dbid, insertInput.query, load, Array.ConvertAll(insertInput.parameters, p => (DbArg)p));
                row = await _agw.RowAsync(dbid, checkInput.query, load, checkArgs);
            }
            if (row == null || row.Count < 1) throw new Exception($@"{failureMessage} from the database {dbid}");
            return (row.GetLong("id"), row.GetString("uid") ?? string.Empty);
        }

        /// <summary>Convenience helper that converts a params array of named parameters to an array, matching the expected signature.</summary>
        (string key,object value)[] Consolidate(params (string, object)[] parameters) { return parameters; }

        /// <summary>
        /// Ensures the name-store chain exists: extension → vault name → name_store composite row.
        /// Returns the <c>name_store</c> ID, which is used as the FK when inserting a document row.
        /// </summary>
        async Task<(bool status, long id)> EnsureNameStore(IVaultReadRequest request) {
            if (string.IsNullOrWhiteSpace(request.RequestedName)) return (false, 0);
            return await EnsureNameStore(
                request.Scope.Module.Cuid.ToString("N"),
                request.RequestedName,
                request.ReadOnlyMode,
                request.CallID);
        }

        async Task<(bool status, long id)> EnsureNameStore(string dbid, string fileName, bool readOnly = false, string callId = null) {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(dbid)) return (false, 0);
            var name = Path.GetFileNameWithoutExtension(fileName)?.Trim();
            var ext = Path.GetExtension(fileName)?.Trim();
            if (string.IsNullOrWhiteSpace(ext)) ext = VaultConstants.DEFAULT_NAME;
            if (string.IsNullOrWhiteSpace(name)) return (false, 0);
            name = name.ToDBName();
            ext = ext.ToDBName();

            //Extension Exists?
            long extId = await InsertAndFetchIDScalar(dbid, () => (INSTANCE.EXTENSION.EXISTS, Consolidate((NAME, ext))), () => (INSTANCE.EXTENSION.INSERT, Consolidate((NAME, ext))), readOnly: readOnly, $@"Unable to fetch extension id for {ext}", callId: callId);

            // Name Exists ?
            long nameId = await InsertAndFetchIDScalar(dbid, () => (INSTANCE.VAULT.EXISTS, Consolidate((NAME, name))), () => (INSTANCE.VAULT.INSERT, Consolidate((NAME, name))), readOnly: readOnly, $@"Unable to fetch name id for {name}", callId: callId);

            //Namestore Exists?
            long nsId = await InsertAndFetchIDScalar(dbid, () => (INSTANCE.NAMESTORE.EXISTS, Consolidate((NAME, nameId), (EXT, extId))), () => (INSTANCE.NAMESTORE.INSERT, Consolidate((NAME, nameId), (EXT, extId))), readOnly: readOnly, $@"Unable to fetch name store id for name : {name} and extension : {ext}", callId: callId);

            return (true, nsId);
        }

        /// <summary>Builds the composite cache key used to store transaction handlers: <c>callid###dbid</c> (lowercase dbid).</summary>
        string GetHandlerKey(string callid, string dbid) { return $@"{callid}###{dbid.ToLower()}"; }

        /// <summary>
        /// Normalises a GUID string to the dashed format used by MariaDB's <c>uuid()</c> default
        /// (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>).
        /// <para>
        /// Required because callers (e.g. <c>AsObjectReadRequest</c>) pass compact-N GUIDs, but
        /// <c>directory</c>, <c>document</c>, and <c>doc_version</c> CUIDs are DB-generated via
        /// <c>DEFAULT uuid()</c> which always produces dashed UUIDs. Passing compact-N to a
        /// <c>WHERE cuid = @VALUE</c> clause would silently return no rows.
        /// </para>
        /// Returns the original string unchanged when it is not a valid GUID.
        /// </summary>
        static string ToDbCuid(string cuid) {
            if (!string.IsNullOrWhiteSpace(cuid) && Guid.TryParse(cuid, out var g))
                return g.ToString(); // default format = "D" (dashed)
            return cuid;
        }

        /// <summary>
        /// Core document-registration flow: ensures workspace → directory → name-store → document → doc_version,
        /// opens a transaction, sets the resulting ID and CUID on <paramref name="holder"/>, and returns both.
        /// Rolls back the transaction on any exception.
        /// </summary>
        async Task<(long id, Guid guid)> RegisterDocumentsInternal(IVaultReadRequest request, IVaultStorable holder) {
            try {
                if (request.ReadOnlyMode) throw new ArgumentException("Cannot register a document in readonly mode");
                CleanupStaleHandlers();
                var actor = request.Actor ?? 0L;
                //If we are in ParseMode, we still do all the process, but, store the file as is with Parsing information.
                //For parse mode, let us not throw any exception.
                //Generate a handler.
                var dbid = request.Scope.Module.Cuid.ToString("N");
                var handlerKey = GetHandlerKey(request.CallID, dbid);
                var handler = _agw.GetTransactionHandler(dbid);

                if (handler != null) {
                    if (_handlers.ContainsKey(handlerKey)) throw new Exception($@"A similar transaction with same key already exists: {handlerKey}");
                    _handlers.TryAdd(handlerKey, (handler, DateTime.UtcNow));
                    handler.Begin();
                }

                (long id, Guid guid) result = (0, Guid.Empty);
                var ws = await EnsureWorkSpace(request);
                if (!ws.status) return result;
                var dir = await EnsureDirectory(request, ws.id);
                if (!dir.status) return result;
                var ns = await EnsureNameStore(request);
                if (!ns.status) return result;

                var docInfo = await InsertAndFetchIDRead(dbid,() => (INSTANCE.DOCUMENT.EXISTS, Consolidate((PARENT, dir.result.id), (NAME, ns.id))), callId: request.CallID);
                bool docExists = docInfo.id != 0;
                if (!docExists) {
                    // Insert it.
                    docInfo = await InsertAndFetchIDRead(dbid,
                        () => (INSTANCE.DOCUMENT.EXISTS, Consolidate((PARENT, dir.result.id), (NAME, ns.id))),
                        ()=> (INSTANCE.DOCUMENT.INSERT, Consolidate((WSPACE,ws.id), (PARENT, dir.result.id), (NAME, ns.id))),
                         readOnly: request.ReadOnlyMode,
                        $@"Unable to insert document with name {request.RequestedName}",false, callId: request.CallID);
                    var dname = Path.GetFileName(request.RequestedName);
                    await _agw.ExecAsync(dbid, INSTANCE.DOCUMENT.INSERT_INFO, new DbExecutionLoad(default, handler), (PARENT, docInfo.id), (DNAME, dname), (ACTOR, actor));
                }

                int version = 1;
                //If Doc exists.. we just need to revise the version.
                if (docExists) {
                    //Assuming that there is a version. Get the latest version.
                    var currentVersion = await _agw.ScalarAsync<int?>(dbid, INSTANCE.DOCVERSION.FIND_LATEST, new DbExecutionLoad(default, handler), (PARENT, docInfo.id));
                    if (currentVersion.HasValue) version = currentVersion.Value + 1;
                }

                var dvInfo = await InsertAndFetchIDRead(dbid,
                    () => (INSTANCE.DOCVERSION.EXISTS, Consolidate((PARENT, docInfo.id), (VERSION, version))),
                    () => (INSTANCE.DOCVERSION.INSERT, Consolidate((PARENT, docInfo.id), (VERSION, version), (ACTOR, actor))),
                     readOnly: request.ReadOnlyMode,
                    $@"Unable to insert document version for the document {docInfo.id}", false, callId: request.CallID);

                if (dvInfo.id > 0 && !string.IsNullOrWhiteSpace(dvInfo.uid)) {
                    //Check if the incoming uid is in proper GUID format.
                    Guid dvId = Guid.Empty;
                    if (dvInfo.uid.IsValidGuid(out dvId) || dvInfo.uid.IsCompactGuid(out dvId)) {
                        result = (dvInfo.id, dvId);
                    }
                }

                if (holder != null) {
                    holder.SetCuid(result.guid);
                    holder.Id = result.id;   // IIdentityBase.Id has public setter
                    holder.Version = version;
                    if (holder is VaultStorable vs) vs.DocumentCuid = docInfo.uid;
                }

                return result;
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                var dbid = request.Scope.Module.Cuid.ToString("N");
                var handlerKey = GetHandlerKey(request.CallID, dbid);
                if (!string.IsNullOrWhiteSpace(handlerKey) && _handlers.ContainsKey(handlerKey)) {
                    _handlers[handlerKey].handler?.Rollback(); //roll everything back.
                    _handlers.Remove(handlerKey,out _);
                }
                if (ThrowExceptions) throw ex; //For Parse mode, let us not throw any exceptions.
                return (0, Guid.Empty);
            }
        }
       
    }
}
