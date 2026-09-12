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
    /// Partial class — workspace-scoped search for folders and files (latest version only).
    /// Searches vault names (filename stems) using LIKE patterns; extension is a separate filter.
    /// Supports three scope modes: entire workspace, single directory, recursive subtree.
    /// </summary>
    internal partial class MariaDBIndexing {

        public async Task<IFeedback<VaultFolderBrowseResponse>> SearchItems(IVaultReadRequest request, string searchTerm, VaultSearchMode searchMode, string extension = null, bool recursive = false, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true) {

            var fb = new Feedback<VaultFolderBrowseResponse>();
            try {
                if (string.IsNullOrWhiteSpace(searchTerm) && string.IsNullOrWhiteSpace(extension))
                    return fb.SetMessage("Search requires a term or extension.");
                if (request?.Scope?.Module == null || request.Scope.Module.Cuid == Guid.Empty)
                    return fb.SetMessage("Module CUID is mandatory for search.");
                if (request.Scope?.Workspace == null || request.Scope.Workspace.Cuid == Guid.Empty)
                    return fb.SetMessage("Workspace CUID is mandatory for search.");

                if (page < 1) page = 1;
                if (pageSize < 1) pageSize = 50;
                if (pageSize > 200) pageSize = 200;

                var moduleCuid = request.Scope.Module.Cuid.ToString("N");
                if (!_agw.ContainsKey(moduleCuid))
                    return fb.SetMessage($"No adapter found for module {moduleCuid}.");

                var wsId = await ResolveWorkspaceId(request.Scope.Workspace.Cuid.ToString("N"));
                if (wsId < 1) return fb.SetMessage("Workspace is not registered in the core index.");

                var likePattern = string.IsNullOrWhiteSpace(searchTerm)
                    ? "%"
                    : BuildSearchPattern(searchTerm.Trim().ToLowerInvariant(), searchMode);
                // Stored extensions include the leading dot (".pdf"). API callers may send "pdf" or ".pdf".
                object extParam = BuildExtensionFilter(extension);

                var folderInfo = await ResolveFolderInfo(moduleCuid, request, wsId, includeAll);
                if (!folderInfo.status) return fb.SetMessage(folderInfo.message);

                var offset = (page - 1) * pageSize;
                var fetchSize = includeTotals ? pageSize : pageSize + 1;
                long totalDirs, totalFiles;
                IEnumerable<DbRow> rows;

                var directoryId = folderInfo.id;
                if (directoryId < 1) {
                    // Scope: entire workspace.
                    totalDirs  = !includeTotals || kind == VaultFolderItemKind.Files ? 0 : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.SEARCH.COUNT_DIRS_ALL_INCLUDE_DELETED : INSTANCE.SEARCH.COUNT_DIRS_ALL,  default, (WSPACE, wsId), (VALUE, likePattern), (EXT, extParam)) ?? 0;
                    totalFiles = !includeTotals || kind == VaultFolderItemKind.Folders ? 0 : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.SEARCH.COUNT_FILES_ALL_INCLUDE_DELETED : INSTANCE.SEARCH.COUNT_FILES_ALL, default, (WSPACE, wsId), (VALUE, likePattern), (EXT, extParam)) ?? 0;
                    rows       = await _agw.RowsAsync(moduleCuid, ApplyFolderListingOptions(includeAll ? INSTANCE.SEARCH.ITEMS_ALL_INCLUDE_DELETED : INSTANCE.SEARCH.ITEMS_ALL, "sr", sort, direction, kind), default, (WSPACE, wsId), (VALUE, likePattern), (EXT, extParam), (LIMIT_ROWS, fetchSize), (OFFSET_ROWS, offset));
                } else if (!recursive) {
                    // Scope: direct children of a specific directory.
                    totalDirs  = !includeTotals || kind == VaultFolderItemKind.Files ? 0 : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.SEARCH.COUNT_DIRS_IN_DIR_INCLUDE_DELETED : INSTANCE.SEARCH.COUNT_DIRS_IN_DIR,  default, (WSPACE, wsId), (PARENT, directoryId), (VALUE, likePattern), (EXT, extParam)) ?? 0;
                    totalFiles = !includeTotals || kind == VaultFolderItemKind.Folders ? 0 : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.SEARCH.COUNT_FILES_IN_DIR_INCLUDE_DELETED : INSTANCE.SEARCH.COUNT_FILES_IN_DIR, default, (WSPACE, wsId), (PARENT, directoryId), (VALUE, likePattern), (EXT, extParam)) ?? 0;
                    rows       = await _agw.RowsAsync(moduleCuid, ApplyFolderListingOptions(includeAll ? INSTANCE.SEARCH.ITEMS_IN_DIR_INCLUDE_DELETED : INSTANCE.SEARCH.ITEMS_IN_DIR, "sr", sort, direction, kind), default, (WSPACE, wsId), (PARENT, directoryId), (VALUE, likePattern), (EXT, extParam), (LIMIT_ROWS, fetchSize), (OFFSET_ROWS, offset));
                } else {
                    // Scope: recursive subtree of a directory (WITH RECURSIVE CTE).
                    totalDirs  = !includeTotals || kind == VaultFolderItemKind.Files ? 0 : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.SEARCH.COUNT_DIRS_RECURSIVE_INCLUDE_DELETED : INSTANCE.SEARCH.COUNT_DIRS_RECURSIVE,  default, (WSPACE, wsId), (PARENT, directoryId), (VALUE, likePattern), (EXT, extParam)) ?? 0;
                    totalFiles = !includeTotals || kind == VaultFolderItemKind.Folders ? 0 : await _agw.ScalarAsync<long?>(moduleCuid, includeAll ? INSTANCE.SEARCH.COUNT_FILES_RECURSIVE_INCLUDE_DELETED : INSTANCE.SEARCH.COUNT_FILES_RECURSIVE, default, (WSPACE, wsId), (PARENT, directoryId), (VALUE, likePattern), (EXT, extParam)) ?? 0;
                    rows       = await _agw.RowsAsync(moduleCuid, ApplyFolderListingOptions(includeAll ? INSTANCE.SEARCH.ITEMS_RECURSIVE_INCLUDE_DELETED : INSTANCE.SEARCH.ITEMS_RECURSIVE, "sr", sort, direction, kind), default, (WSPACE, wsId), (PARENT, directoryId), (VALUE, likePattern), (EXT, extParam), (LIMIT_ROWS, fetchSize), (OFFSET_ROWS, offset));
                }

                var rowList = rows.ToList();
                var hasNext = includeTotals
                    ? offset + rowList.Count < totalDirs + totalFiles
                    : rowList.Count > pageSize;
                var response = new VaultFolderBrowseResponse { WorkspaceId = wsId, WorkspaceCuid = request.Scope.Workspace.Cuid.ToString("N"), IsRoot = folderInfo.isRoot, CurrentFolderId = folderInfo.id, CurrentFolderCuid = folderInfo.cuid, CurrentFolderName = folderInfo.displayName, CurrentFolderParentId = folderInfo.parentId, IncludeAll = includeAll, Page = page, PageSize = pageSize, TotalsIncluded = includeTotals, HasNext = hasNext, TotalFolders = totalDirs, TotalFiles = totalFiles, TotalItems = totalDirs + totalFiles };

                foreach (var row in rowList.Take(pageSize))
                    response.Items.Add(MapBrowseItem(row));

                await ApplySearchPaths(moduleCuid, response, includeAll);

                return fb.SetStatus(true).SetResult(response);
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                return fb.SetMessage(ex.Message);
            }
        }

        /// <summary>Builds a MariaDB LIKE pattern from a pre-normalized (trimmed + lowercased) term.</summary>
        static string BuildSearchPattern(string normalizedTerm, VaultSearchMode mode) => mode switch {
            VaultSearchMode.StartsWith => $"{normalizedTerm}%",
            VaultSearchMode.EndsWith   => $"%{normalizedTerm}",
            VaultSearchMode.Contains   => $"%{normalizedTerm}%",
            _                          => normalizedTerm,   // Equals — exact match, no wildcards
        };

        static object BuildExtensionFilter(string extension) {
            if (string.IsNullOrWhiteSpace(extension)) return DBNull.Value;

            var normalized = extension.Trim().ToLowerInvariant();
            if (normalized == "*") return DBNull.Value;
            if (!normalized.StartsWith('.') && normalized != VaultConstants.DEFAULT_NAME)
                normalized = "." + normalized;

            return normalized.ToDBName();
        }
    }
}
