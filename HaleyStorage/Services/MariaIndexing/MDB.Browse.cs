using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using static Haley.Internal.IndexingConstant;
using static Haley.Internal.IndexingQueries;

namespace Haley.Utils {
    /// <summary>
    /// Partial class — DB-backed browse/explore APIs for folders and file history.
    /// </summary>
    internal partial class MariaDBIndexing {
        public async Task<IFeedback<VaultFolderBrowseResponse>> BrowseFolder(IVaultReadRequest request, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true) {
            var fb = new Feedback<VaultFolderBrowseResponse>();
            try {
                if (request == null) return fb.SetMessage("Input request cannot be empty.");
                if (request.Scope?.Module == null || request.Scope.Module.Cuid == Guid.Empty)
                    return fb.SetMessage("Module CUID is mandatory to browse a folder.");
                if (request.Scope?.Workspace == null || request.Scope.Workspace.Cuid == Guid.Empty)
                    return fb.SetMessage("Workspace CUID is mandatory to browse a folder.");

                if (page < 1) page = 1;
                if (pageSize < 1) pageSize = 10;
                if (pageSize > 200) pageSize = 200;

                var moduleCuid = request.Scope.Module.Cuid.ToString("N");
                if (!_agw.ContainsKey(moduleCuid)) return fb.SetMessage($@"No adapter found for the key {moduleCuid}");

                var wsId = await ResolveWorkspaceId(request.Scope.Workspace.Cuid.ToString("N"));
                if (wsId < 1) return fb.SetMessage("Workspace is not registered in the core index.");

                var folderInfo = await ResolveFolderInfo(moduleCuid, request, wsId, includeAll);
                if (!folderInfo.status) return fb.SetMessage(folderInfo.message);

                var totalFolders = !includeTotals || kind == VaultFolderItemKind.Files
                    ? 0
                    : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.DIRECTORY.COUNT_CHILDREN_ALL : INSTANCE.DIRECTORY.COUNT_CHILDREN, default, (WSPACE, wsId), (PARENT, folderInfo.id)) ?? 0;
                var totalFiles = !includeTotals || kind == VaultFolderItemKind.Folders
                    ? 0
                    : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.DOCUMENT.COUNT_BY_DIRECTORY_ALL : INSTANCE.DOCUMENT.COUNT_BY_DIRECTORY, default, (WSPACE, wsId), (PARENT, folderInfo.id)) ?? 0;
                var offset = (page - 1) * pageSize;
                var fetchSize = includeTotals ? pageSize : pageSize + 1;

                var query = ApplyFolderListingOptions(includeAll ? INSTANCE.DIRECTORY.BROWSE_ITEMS_ALL : INSTANCE.DIRECTORY.BROWSE_ITEMS, "browse_items", sort, direction, kind);
                var rows = await _agw.RowsAsync(moduleCuid, query, default, (WSPACE, wsId), (PARENT, folderInfo.id), (LIMIT_ROWS, fetchSize), (OFFSET_ROWS, offset));
                var hasNext = includeTotals
                    ? offset + rows.Count < totalFolders + totalFiles
                    : rows.Count > pageSize;

                var response = new VaultFolderBrowseResponse { WorkspaceId = wsId, WorkspaceCuid = request.Scope.Workspace.Cuid.ToString("N"), IsRoot = folderInfo.isRoot, CurrentFolderId = folderInfo.id, CurrentFolderCuid = folderInfo.cuid, CurrentFolderName = folderInfo.displayName, CurrentFolderParentId = folderInfo.parentId, IncludeAll = includeAll, Page = page, PageSize = pageSize, TotalsIncluded = includeTotals, HasNext = hasNext, TotalFolders = totalFolders, TotalFiles = totalFiles, TotalItems = totalFolders + totalFiles };

                foreach (var row in rows.Take(pageSize)) {
                    response.Items.Add(MapBrowseItem(row));
                }

                await ApplyBrowsePaths(moduleCuid, response, folderInfo.id, includeAll);

                return fb.SetStatus(true).SetResult(response);
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                return fb.SetMessage(ex.Message);
            }
        }

        public async Task<IFeedback<VaultFileDetailsResponse>> GetFileDetails(IVaultFileReadRequest request) {
            var fb = new Feedback<VaultFileDetailsResponse>();
            try {
                if (request == null) return fb.SetMessage("Input request cannot be empty.");
                if (request.Scope?.Module == null || request.Scope.Module.Cuid == Guid.Empty)
                    return fb.SetMessage("Module CUID is mandatory to fetch file details.");

                var moduleCuid = request.Scope.Module.Cuid.ToString("N");
                if (!_agw.ContainsKey(moduleCuid)) return fb.SetMessage($@"No adapter found for the key {moduleCuid}");

                var documentId = await ResolveDocumentId(moduleCuid, request, includeAll: true);
                if (documentId < 1) return fb.SetMessage("Unable to resolve the target file.");

                var docRow = await _agw.RowAsync(moduleCuid, INSTANCE.DOCUMENT.GET_DETAILS_BY_ID_ALL, default, (ID, documentId));
                if (docRow == null) return fb.SetMessage("Unable to fetch document details.");

                var versionRows = await _agw.RowsAsync(moduleCuid, INSTANCE.DOCVERSION.GET_ALL_CONTENT_BY_PARENT_ALL, default, (PARENT, documentId));

                // Check if the latest content version has a thumbnail sub-version.
                bool hasThumb = false;
                var latestActiveVersion = versionRows.FirstOrDefault(r => r.GetInt("delete_state") == 0);
                if (latestActiveVersion != null) {
                    var latestVer = latestActiveVersion.GetInt("version_no");
                    if (latestVer > 0) {
                        var latestSubVer = await _agw.ScalarAsync<int?>(moduleCuid, INSTANCE.DOCVERSION.FIND_LATEST_SUB_VER, default,
                            (PARENT, documentId), (VERSION, latestVer));
                        hasThumb = (latestSubVer ?? 0) > 0;
                    }
                }

                var documentDeleteState = docRow.GetInt("delete_state");
                var documentDeleted = docRow.GetDateTime("deleted");
                var latestVersion = versionRows.FirstOrDefault();
                var effectiveDeleteState = documentDeleteState > 0
                    ? documentDeleteState
                    : latestActiveVersion == null
                        ? latestVersion?.GetInt("delete_state") ?? 1
                        : 0;
                var effectiveDeleted = documentDeleteState > 0
                    ? documentDeleted
                    : effectiveDeleteState > 0
                        ? latestVersion?.GetDateTime("deleted")
                        : null;
                var workspaceId = docRow.GetLong("workspace_id");
                var workspace = GetAllComponents<VaultWorkSpace>().FirstOrDefault(candidate => candidate.Id == workspaceId);
                if (workspace == null && await HydrateWorkspaceByIdAsync(workspaceId))
                    workspace = GetAllComponents<VaultWorkSpace>().FirstOrDefault(candidate => candidate.Id == workspaceId);

                var response = new VaultFileDetailsResponse { DocumentId = docRow.GetLong("document_id"), DocumentCuid = docRow.GetString("document_cuid") ?? string.Empty, DisplayName = docRow.GetString("display_name") ?? string.Empty, DocumentActorId = docRow.GetLong("document_actor_id"), WorkspaceId = workspaceId, WorkspaceCuid = workspace?.Cuid.ToString("N") ?? string.Empty, WorkspaceName = workspace?.DisplayName ?? string.Empty, DirectoryId = docRow.GetLong("directory_id"), DirectoryCuid = docRow.GetString("directory_cuid") ?? string.Empty, DirectoryName = docRow.GetString("directory_name") ?? string.Empty, DirectoryActorId = docRow.GetLong("directory_actor_id"), DirectoryParentId = docRow.GetLong("directory_parent_id"), DeleteState = effectiveDeleteState, IsDeleted = effectiveDeleteState > 0, Deleted = effectiveDeleted, DocumentDeleteState = documentDeleteState, DocumentIsDeleted = documentDeleteState > 0, DocumentDeleted = documentDeleted, VersionCount = versionRows.Count, DocumentMetadata = docRow.GetString("doc_metadata") ?? string.Empty, HasThumbnail = hasThumb };

                foreach (var row in versionRows) {
                    var versionDeleteState = row.GetInt("delete_state");
                    response.Versions.Add(new VaultFileVersionInfo { VersionId = row.GetLong("version_id"), VersionCuid = row.GetString("version_cuid") ?? string.Empty, VersionNumber = row.GetInt("version_no"), ActorId = row.GetLong("actor_id"), DeleteState = versionDeleteState, IsDeleted = versionDeleteState > 0, Deleted = row.GetDateTime("deleted"), Created = row.GetDateTime("version_created"), Size = row.GetNullableLong("size"), StorageName = row.GetString("storage_name") ?? string.Empty, StorageRef = row.GetString("storage_ref") ?? string.Empty, StagingRef = row.GetString("staging_ref") ?? string.Empty, Flags = row.GetInt("flags"), Hash = row.GetString("hash") ?? string.Empty, SyncedAt = row.GetDateTime("synced_at"), Metadata = row.GetString("metadata") ?? string.Empty });
                }

                return fb.SetStatus(true).SetResult(response);
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                return fb.SetMessage(ex.Message);
            }
        }

        async Task<long> ResolveWorkspaceId(string workspaceCuid) {
            if (string.IsNullOrWhiteSpace(workspaceCuid)) return 0;
            return await _agw.ScalarAsync<long?>(_key, WORKSPACE.EXISTS_BY_CUID, default, (CUID, workspaceCuid)) ?? 0;
        }

        async Task<(bool status, string message, bool isRoot, long id, string cuid, string displayName, long parentId)> ResolveFolderInfo(string moduleCuid, IVaultReadRequest request, long workspaceId, bool includeAll = false) {
            var folder = request.Scope?.Folder;
            if (folder == null || (folder.Id < 1 && string.IsNullOrWhiteSpace(folder.Cuid) && string.IsNullOrWhiteSpace(folder.DisplayName))) {
                return (true, string.Empty, true, 0, string.Empty, string.Empty, 0);
            }

            DbRow row = null;
            if (folder.Id > 0) {
                row = await _agw.RowAsync(moduleCuid, includeAll ? INSTANCE.DIRECTORY.GET_DETAILS_BY_ID_ALL : INSTANCE.DIRECTORY.GET_DETAILS_BY_ID, default, (VALUE, folder.Id));
            } else if (!string.IsNullOrWhiteSpace(folder.Cuid)) {
                row = await _agw.RowAsync(moduleCuid, includeAll ? INSTANCE.DIRECTORY.GET_DETAILS_BY_CUID_ALL : INSTANCE.DIRECTORY.GET_DETAILS_BY_CUID, default, (VALUE, ToDbCuid(folder.Cuid)));
            } else {
                var parentId = folder.Parent?.Id ?? 0;
                row = await _agw.RowAsync(moduleCuid, includeAll ? INSTANCE.DIRECTORY.GET_DETAILS_ALL : INSTANCE.DIRECTORY.GET_DETAILS, default, (WSPACE, workspaceId), (PARENT, parentId), (NAME, folder.DisplayName.ToDBName()));
            }

            if (row == null) return (false, "Folder not found.", false, 0, string.Empty, string.Empty, 0);
            if (row.GetLong("workspace") != workspaceId) return (false, "Folder does not belong to the requested workspace.", false, 0, string.Empty, string.Empty, 0);

            return (true, string.Empty, false, row.GetLong("id"), row.GetString("uid") ?? string.Empty, row.GetString("display_name") ?? string.Empty, row.GetLong("parent"));
        }

        async Task<long> ResolveDocumentId(string moduleCuid, IVaultFileReadRequest request, bool includeAll = false) {
            if (request?.File?.Id > 0) {
                return await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.DOCVERSION.GET_DOCUMENT_ID_BY_VERSION_ID, default, (VALUE, request.File.Id)) ?? 0;
            }

            if (!string.IsNullOrWhiteSpace(request?.File?.Cuid)) {
                return await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.DOCVERSION.GET_DOCUMENT_ID_BY_VERSION_CUID, default, (VALUE, ToDbCuid(request.File.Cuid))) ?? 0;
            }

            if (!string.IsNullOrWhiteSpace((request?.File as StorageFileRoute)?.RootCuid)) {
                var rootCuid = ToDbCuid((request.File as StorageFileRoute).RootCuid);
                return await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.DOCUMENT.EXISTS_BY_CUID_ALL : INSTANCE.DOCUMENT.GET_BY_CUID, default, (CUID, rootCuid)) ?? 0;
            }

            if (request?.Scope?.Workspace == null || request.Scope.Workspace.Cuid == Guid.Empty) return 0;

            var wsId = await ResolveWorkspaceId(request.Scope.Workspace.Cuid.ToString("N"));
            if (wsId < 1) return 0;

            var folderInfo = await ResolveFolderInfo(moduleCuid, request, wsId);
            if (!folderInfo.status) return 0;

            var fileName = request.File?.DisplayName ?? request.RequestedName;
            if (string.IsNullOrWhiteSpace(fileName)) return 0;

            var dirName = folderInfo.isRoot ? VaultConstants.DEFAULT_NAME : folderInfo.displayName;
            var dirParentId = folderInfo.isRoot ? 0 : folderInfo.parentId;
            var name = System.IO.Path.GetFileNameWithoutExtension(fileName).ToDBName();
            var extension = System.IO.Path.GetExtension(fileName)?.ToDBName() ?? VaultConstants.DEFAULT_NAME;

            return await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.DOCUMENT.GET_BY_NAME, default, (NAME, name), (EXT, extension), (WSPACE, wsId), (PARENT, dirParentId), (DIRNAME, dirName.ToDBName())) ?? 0;
        }

        static VaultBrowseItem MapBrowseItem(DbRow row) {
            var deleteState = row.GetInt("delete_state");
            var documentDeleteState = row.GetNullableInt("document_delete_state");
            var versionDeleteState = row.GetNullableInt("version_delete_state");
            return new VaultBrowseItem { ItemType = row.GetString("item_type") ?? string.Empty, Id = row.GetLong("id"), Cuid = row.GetString("uid") ?? string.Empty, DisplayName = row.GetString("display_name") ?? string.Empty, ActorId = row.GetNullableLong("actor_id"), ParentId = row.GetLong("parent_id"), VirtualPath = row.GetString("virtual_path") ?? string.Empty, DeleteState = deleteState, IsDeleted = deleteState > 0, Deleted = row.GetDateTime("deleted"), DocumentDeleteState = documentDeleteState, DocumentIsDeleted = documentDeleteState > 0, DocumentDeleted = row.GetDateTime("document_deleted"), LatestVersionDeleteState = versionDeleteState, LatestVersionIsDeleted = versionDeleteState > 0, LatestVersionDeleted = row.GetDateTime("version_deleted"), Created = row.GetDateTime("created"), Modified = row.GetDateTime("modified"), LatestVersionId = row.GetNullableLong("version_id"), LatestVersionCuid = row.GetString("version_cuid") ?? string.Empty, LatestVersionNumber = row.GetNullableInt("version_no"), VersionCount = row.GetNullableInt("version_count"), HasThumbnail = row.GetInt("has_thumbnail") > 0, LatestVersionCreated = row.GetDateTime("version_created"), Size = row.GetNullableLong("size"), StorageName = row.GetString("storage_name") ?? string.Empty, StagingRef = row.GetString("staging_ref") ?? string.Empty, StorageRef = row.GetString("storage_ref") ?? string.Empty, Flags = row.GetNullableInt("flags"), Hash = row.GetString("hash") ?? string.Empty, SyncedAt = row.GetDateTime("synced_at") };
        }

        static string ApplyFolderListingOptions(string sql, string alias, VaultFolderSortMode sort, VaultSortDirection direction, VaultFolderItemKind kind) {
            var currentOrder = $"order by {alias}.sort_group asc, {alias}.display_name asc, {alias}.id asc";
            var itemFilter = kind switch {
                VaultFolderItemKind.Folders => $"where {alias}.sort_group = 0",
                VaultFolderItemKind.Files   => $"where {alias}.sort_group = 1",
                _                           => string.Empty
            };
            var replacement = string.IsNullOrWhiteSpace(itemFilter)
                ? BuildFolderOrderClause(alias, sort, direction)
                : $"{itemFilter}{Environment.NewLine}                       {BuildFolderOrderClause(alias, sort, direction)}";
            return sql.Replace(currentOrder, replacement, StringComparison.Ordinal);
        }

        static string BuildFolderOrderClause(string alias, VaultFolderSortMode sort, VaultSortDirection direction) {
            var dir = direction == VaultSortDirection.Desc ? "desc" : "asc";
            return sort switch {
                VaultFolderSortMode.Name =>
                    $"order by {alias}.sort_group asc, {alias}.display_name {dir}, {alias}.id asc",
                VaultFolderSortMode.Created =>
                    $"order by {alias}.sort_group asc, ({alias}.created is null) asc, {alias}.created {dir}, {alias}.id asc",
                VaultFolderSortMode.Modified =>
                    $"order by {alias}.sort_group asc, (coalesce({alias}.modified, {alias}.created) is null) asc, coalesce({alias}.modified, {alias}.created) {dir}, {alias}.id asc",
                _ =>
                    $"order by {alias}.sort_group asc, {alias}.id {dir}"
            };
        }
    }
}
