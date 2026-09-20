using Haley.Abstractions;
using Haley.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using static Haley.Internal.IndexingConstant;
using static Haley.Internal.IndexingQueries;

namespace Haley.Utils {
    /// <summary>
    /// Partial class — virtual directory (folder) registration.
    /// Folders are DB-only — no physical directory is created.
    /// </summary>
    internal partial class MariaDBIndexing {
        /// <summary>
        /// Ensures a virtual folder row exists in the per-module DB.
        /// If the folder already exists (matched by workspace + parent + name), the existing record
        /// is returned without modification. If absent, it is inserted.
        /// Returns the folder's numeric ID and compact-N CUID.
        /// </summary>
        /// <param name="request">Scope providing module CUID (for DB routing) and workspace.
        /// The <c>Scope.Folder</c> identifies the PARENT folder; pass null or Id=0 for root.</param>
        /// <param name="folderName">Display name for the new folder (stored verbatim in <c>display_name</c>
        /// and as a DB-safe name in <c>name</c>).</param>
        public async Task<IFeedback<(long id, string cuid)>> RegisterDirectory(IVaultReadRequest request, string folderName) {
            var fb = new Feedback<(long id, string cuid)>();
            try {
                if (request == null) return fb.SetMessage("Request cannot be null.");
                if (string.IsNullOrWhiteSpace(folderName)) return fb.SetMessage("Folder name cannot be empty.");
                DirectoryVisibility.ValidateName(folderName, request.AllowHiddenDirectories);
                await DemandDirectoryAccess(request);
                if (request.ReadOnlyMode) return fb.SetMessage("Cannot create a folder in read-only mode.");

                var ws = await EnsureWorkSpace(request);
                if (!ws.status) return fb.SetMessage("Workspace not found or not registered.");

                var dbid = request.Scope.Module.Cuid.ToString("N");
                var actor = request.Actor ?? 0L;

                // Resolve the parent folder ID.
                // Priority: explicit Id → resolve by CUID or DisplayName → 0 (root).
                var parentId = request.Scope?.Folder?.Id ?? 0;
                if (parentId == 0 && request.Scope?.Folder != null) {
                    var f = request.Scope.Folder;
                    if (!string.IsNullOrWhiteSpace(f.Cuid) || !string.IsNullOrWhiteSpace(f.DisplayName)) {
                        var parentInfo = await ResolveFolderInfo(dbid, request, ws.id);
                        if (!parentInfo.status) return fb.SetMessage(parentInfo.message);
                        if (!parentInfo.isRoot) parentId = parentInfo.id;
                    }
                }

                var dirDbName = folderName.ToDBName();

                Func<(string query, (string key, object value)[] parameters)> checkDirectory =
                    () => (INSTANCE.DIRECTORY.EXISTS, Consolidate((WSPACE, ws.id), (PARENT, parentId), (NAME, dirDbName)));
                Func<(string query, (string key, object value)[] parameters)> insertDirectory =
                    () => (INSTANCE.DIRECTORY.INSERT, Consolidate((WSPACE, ws.id), (PARENT, parentId), (NAME, dirDbName), (DNAME, folderName), (ACTOR, actor)));

                var existing = await InsertAndFetchIDRead(dbid, checkDirectory, readOnly: true);
                var wasCreated = existing.id < 1 && !request.ReadOnlyMode;
                var dirInfo = existing.id > 0
                    ? existing
                    : await InsertAndFetchIDRead(dbid,
                    checkDirectory,
                    insertDirectory,
                    readOnly: request.ReadOnlyMode,
                    $"Unable to create directory '{folderName}' in workspace {ws.id}");

                if (wasCreated && dirInfo.id > 0)
                    await TryQueueFolderCreateStatsEvent(dbid, dirInfo.id, default);

                return fb.SetStatus(true).SetResult((dirInfo.id, dirInfo.uid));
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                return fb.SetMessage(ex.Message);
            }
        }
    }
}
