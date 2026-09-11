using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static Haley.Internal.IndexingConstant;
using static Haley.Internal.IndexingQueries;

namespace Haley.Utils {
    internal partial class MariaDBIndexing {
        readonly ConcurrentDictionary<string, SemaphoreSlim> _statsLocks = new(StringComparer.OrdinalIgnoreCase);

        public async Task<IFeedback<VaultStatsSnapshot>> GetStats(IVaultReadRequest request, string extension = null) {
            var fb = new Feedback<VaultStatsSnapshot>();
            try {
                if (request == null) return fb.SetMessage("Request cannot be null.");
                if (request.Scope?.Module == null || request.Scope.Module.Cuid == Guid.Empty)
                    return fb.SetMessage("Module CUID is mandatory.");
                if (request.Scope?.Workspace == null || request.Scope.Workspace.Cuid == Guid.Empty)
                    return fb.SetMessage("Workspace CUID is mandatory.");

                var moduleCuid = request.Scope.Module.Cuid.ToString("N");
                if (!_agw.ContainsKey(moduleCuid)) return fb.SetMessage($"No adapter found for key {moduleCuid}.");

                var workspaceId = await ResolveWorkspaceId(request.Scope.Workspace.Cuid.ToString("N"));
                if (workspaceId < 1) return fb.SetMessage("Workspace is not registered in the core index.");

                var folderInfo = await ResolveFolderInfo(moduleCuid, request, workspaceId);
                if (!folderInfo.status) return fb.SetMessage(folderInfo.message);

                var nodeType = folderInfo.isRoot ? (int)VaultStatsNodeType.Workspace : (int)VaultStatsNodeType.Directory;
                var nodeId = folderInfo.isRoot ? workspaceId : folderInfo.id;
                object extFilter = NormalizeStatExtension(extension);

                var directRow = await _agw.RowAsync(moduleCuid, INSTANCE.STATS.GET_NODE_STAT, default, (NODE_TYPE, nodeType), (NODE_ID, nodeId));
                var recursiveRow = await _agw.RowAsync(moduleCuid, INSTANCE.STATS.GET_TREE_STAT, default, (NODE_TYPE, nodeType), (NODE_ID, nodeId));
                var directExtRows = await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_NODE_EXT_STATS, default, (NODE_TYPE, nodeType), (NODE_ID, nodeId), (EXT_NAME, extFilter));
                var treeExtRows = await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_TREE_EXT_STATS, default, (NODE_TYPE, nodeType), (NODE_ID, nodeId), (EXT_NAME, extFilter));

                var snapshot = new VaultStatsSnapshot {
                    NodeType = (VaultStatsNodeType)nodeType,
                    NodeId = nodeId,
                    WorkspaceId = workspaceId,
                    WorkspaceCuid = request.Scope.Workspace.Cuid.ToString("N"),
                    DirectoryId = folderInfo.isRoot ? 0 : folderInfo.id,
                    DirectoryCuid = folderInfo.cuid ?? string.Empty,
                    DirectoryName = folderInfo.displayName ?? string.Empty,
                    DirectoryPath = await ResolveDirectoryPath(moduleCuid, folderInfo.id, includeAll: false, new Dictionary<long, string>()),
                    Direct = MapCounters(directRow),
                    Recursive = MapCounters(recursiveRow),
                    Generated = DateTimeOffset.UtcNow
                };

                AddExtensionStats(snapshot, directExtRows, treeExtRows);
                return fb.SetStatus(true).SetResult(snapshot);
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                return fb.SetMessage(ex.Message);
            }
        }

        public async Task<IFeedback> ProcessStatsEvents(string moduleCuid, int batchSize = 1000) {
            var fb = new Feedback();
            try {
                if (string.IsNullOrWhiteSpace(moduleCuid)) return fb.SetMessage("Module CUID is mandatory.");
                if (!_agw.ContainsKey(moduleCuid)) return fb.SetMessage($"No adapter found for key {moduleCuid}.");

                if (batchSize < 1) batchSize = 100;
                if (batchSize > 5000) batchSize = 5000;

                var statsLock = _statsLocks.GetOrAdd(moduleCuid, _ => new SemaphoreSlim(1, 1));
                await statsLock.WaitAsync();
                try {
                    DbRows rows;
                    var handler = _agw.GetTransactionHandler(moduleCuid);
                    using (handler?.Begin()) {
                        var load = new DbExecutionLoad(default, handler);
                        rows = await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_PENDING, load, (BATCH_SIZE, batchSize));
                        foreach (var row in rows ?? Enumerable.Empty<DbRow>()) {
                            await ApplyStatDelta(moduleCuid, row, load);
                            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.MARK_PROCESSED, load, (ID, row.GetLong("id")), (MESSAGE, "processed"));
                        }
                    }

                    if (rows != null && rows.Count > 0)
                        await RefreshCoreStats(moduleCuid);

                    await TryCleanupProcessedStatsEvents(moduleCuid, batchSize);
                    var count = rows?.Count ?? 0;
                    var message = count == 0 ? "No stats events pending." : $"Processed {count} stats events.";
                    return fb.SetStatus(true).SetMessage(message).SetResult(count);
                } finally {
                    statsLock.Release();
                }
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                return fb.SetMessage(ex.Message);
            }
        }

        public async Task<IFeedback> RebuildStats(string moduleCuid, long? workspaceId = null) {
            var fb = new Feedback();
            try {
                if (string.IsNullOrWhiteSpace(moduleCuid)) return fb.SetMessage("Module CUID is mandatory.");
                if (!_agw.ContainsKey(moduleCuid)) return fb.SetMessage($"No adapter found for key {moduleCuid}.");

                var statsLock = _statsLocks.GetOrAdd(moduleCuid, _ => new SemaphoreSlim(1, 1));
                await statsLock.WaitAsync();
                try {
                    var handler = _agw.GetTransactionHandler(moduleCuid);
                    using (handler?.Begin()) {
                        var load = new DbExecutionLoad(default, handler);
                        await RebuildStatsInternal(moduleCuid, load);
                    }

                    await RefreshCoreStats(moduleCuid);
                    var message = workspaceId.HasValue
                        ? $"Stats rebuilt for module {moduleCuid}. Workspace-specific rebuild currently rebuilds the module for consistency."
                        : $"Stats rebuilt for module {moduleCuid}.";
                    return fb.SetStatus(true).SetMessage(message);
                } finally {
                    statsLock.Release();
                }
            } catch (Exception ex) {
                _logger?.LogError(ex.Message + Environment.NewLine + ex.StackTrace);
                return fb.SetMessage(ex.Message);
            }
        }

        async Task RebuildStatsInternal(string moduleCuid, DbExecutionLoad load) {
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.INSERT_RUN, load, (RUN_TYPE, "rebuild"), (STATUS, "started"), (MESSAGE, "exact rebuild started"));
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.CLEAR_STATS, load);
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.CLEAR_DIR_PATH, load);
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.CLEAR_EVENTS, load);
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.REBUILD_DIR_PATH, load);
            await RebuildStatsSet(moduleCuid, INSTANCE.STATS.REBUILD_NODE_STATS, INSTANCE.STATS.INSERT_REBUILD_NODE_STATS, load);
            await RebuildStatsSet(moduleCuid, INSTANCE.STATS.REBUILD_NODE_EXT_STATS, INSTANCE.STATS.INSERT_REBUILD_NODE_EXT_STATS, load);
            await RebuildStatsSet(moduleCuid, INSTANCE.STATS.REBUILD_TREE_STATS, INSTANCE.STATS.INSERT_REBUILD_TREE_STATS, load);
            await RebuildStatsSet(moduleCuid, INSTANCE.STATS.REBUILD_TREE_EXT_STATS, INSTANCE.STATS.INSERT_REBUILD_TREE_EXT_STATS, load);
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.INSERT_RUN, load, (RUN_TYPE, "rebuild"), (STATUS, "completed"), (MESSAGE, "exact rebuild completed"));
        }

        async Task RebuildStatsSet(string moduleCuid, string sourceQuery, string linkQuery, DbExecutionLoad load) {
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.DROP_REBUILD_TEMP, load);
            try {
                await _agw.ExecAsync(moduleCuid, sourceQuery, load);
                await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.INSERT_REBUILD_STATS, load);
                await _agw.ExecAsync(moduleCuid, linkQuery, load);
            } finally {
                await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.DROP_REBUILD_TEMP, load);
            }
        }

        async Task TryCleanupProcessedStatsEvents(string moduleCuid, int batchSize) {
            try {
                await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.DELETE_PROCESSED, default, (BATCH_SIZE, batchSize));
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to clean processed stats events for module {ModuleCuid}.", moduleCuid);
            }
        }

        internal async Task QueueCompletedVersionStatsEvent(string moduleCuid, long versionId, long oldSize, int oldFlags, DbExecutionLoad load) {
            var row = await _agw.RowAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCE, load, (ID, versionId));
            if (row == null || row.Count == 0) return;

            var newFlags = row.GetInt("flags");
            var oldCompleted = HasFlag(oldFlags, VersionFlags.Completed);
            var newCompleted = HasFlag(newFlags, VersionFlags.Completed);
            if (!newCompleted) return;

            var size = row.GetLong("size");
            var isThumb = row.GetInt("sub_version_no") > 0;
            var bytesDelta = size - (oldCompleted ? oldSize : 0);
            var counters = new VaultStatsCounters {
                ActiveBytes = bytesDelta,
                ActiveVersions = !oldCompleted && !isThumb ? 1 : 0,
                ActiveThumbnails = !oldCompleted && isThumb ? 1 : 0
            };

            if (!oldCompleted && !isThumb) {
                var otherContent = await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.STATS.COUNT_ACTIVE_COMPLETED_CONTENT_EXCLUDING, load, (PARENT, row.GetLong("document_id")), (ID, versionId)) ?? 0;
                if (otherContent == 0)
                    counters.ActiveDocuments = 1;
            }

            await QueueVersionEvent(moduleCuid, VaultStatsEventType.Upload, row, counters, load);
        }

        internal async Task TryQueueCompletedVersionStatsEvent(string moduleCuid, long versionId, long oldSize, int oldFlags, DbExecutionLoad load) {
            try {
                await QueueCompletedVersionStatsEvent(moduleCuid, versionId, oldSize, oldFlags, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue upload stats event for version {VersionId}.", versionId);
            }
        }

        internal async Task QueueFolderCreateStatsEvent(string moduleCuid, long folderId, DbExecutionLoad load) {
            var row = await _agw.RowAsync(moduleCuid, INSTANCE.DIRECTORY.GET_DETAILS_BY_ID_ALL, load, (VALUE, folderId));
            if (row == null || row.Count == 0) return;

            var workspaceId = row.GetLong("workspace");
            var parentId = row.GetLong("parent");
            var nodeType = parentId > 0 ? (int)VaultStatsNodeType.Directory : (int)VaultStatsNodeType.Workspace;
            var nodeId = parentId > 0 ? parentId : workspaceId;

            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.INSERT_DIR_PATH_FOR_DIRECTORY, load, (ID, folderId));
            await QueueStatsEventAsync(
                moduleCuid,
                VaultStatsEventType.FolderCreate,
                $"folder-create:{folderId}:{Guid.NewGuid():N}",
                nodeType,
                nodeId,
                workspaceId,
                null,
                null,
                null,
                new VaultStatsCounters { ActiveFolders = 1 },
                load);
        }

        internal async Task TryQueueFolderCreateStatsEvent(string moduleCuid, long folderId, DbExecutionLoad load) {
            try {
                await QueueFolderCreateStatsEvent(moduleCuid, folderId, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue folder-create stats event for directory {DirectoryId}.", folderId);
            }
        }

        internal async Task TryQueueDocumentSoftDeleteStatsEvents(string moduleCuid, long documentId, DbExecutionLoad load) {
            try {
                await QueueDocumentSoftDeleteStatsEvents(moduleCuid, documentId, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue document-delete stats events for document {DocumentId}.", documentId);
            }
        }

        internal async Task TryQueueVersionSoftDeleteStatsEvents(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo, DbExecutionLoad load) {
            try {
                await QueueVersionSoftDeleteStatsEvents(moduleCuid, documentId, versionId, versionNo, subVersionNo, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue version-delete stats events for version {VersionId}.", versionId);
            }
        }

        internal async Task TryQueueDocumentRestoreStatsEvents(string moduleCuid, long documentId, DbExecutionLoad load) {
            try {
                await QueueDocumentRestoreStatsEvents(moduleCuid, documentId, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue document-restore stats events for document {DocumentId}.", documentId);
            }
        }

        internal async Task TryQueueVersionRestoreStatsEvents(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo, DbExecutionLoad load) {
            try {
                await QueueVersionRestoreStatsEvents(moduleCuid, documentId, versionId, versionNo, subVersionNo, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue version-restore stats events for version {VersionId}.", versionId);
            }
        }

        internal async Task TryQueueDocumentArchiveStatsEvents(string moduleCuid, long documentId, DbExecutionLoad load) {
            try {
                await QueueDocumentArchiveStatsEvents(moduleCuid, documentId, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue document-archive stats events for document {DocumentId}.", documentId);
            }
        }

        internal async Task TryQueueVersionArchiveStatsEvents(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo, DbExecutionLoad load) {
            try {
                await QueueVersionArchiveStatsEvents(moduleCuid, documentId, versionId, versionNo, subVersionNo, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue version-archive stats events for version {VersionId}.", versionId);
            }
        }

        internal async Task TryQueueDirectorySoftDeleteStatsEvents(string moduleCuid, IEnumerable<long> directoryIds, IEnumerable<long> documentIds, DbExecutionLoad load) {
            try {
                await QueueDirectorySoftDeleteStatsEvents(moduleCuid, directoryIds, documentIds, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue directory-delete stats events.");
            }
        }

        internal async Task TryQueueDirectoryRestoreStatsEvents(string moduleCuid, IEnumerable<DbRow> directoryRows, IEnumerable<DeletedDocumentInfo> documents, DbExecutionLoad load) {
            try {
                await QueueDirectoryRestoreStatsEvents(moduleCuid, directoryRows, documents, load);
            } catch (Exception ex) {
                _logger?.LogWarning(ex, "Unable to queue directory-restore stats events.");
            }
        }

        async Task QueueDocumentSoftDeleteStatsEvents(string moduleCuid, long documentId, DbExecutionLoad load) {
            var activeContent = await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.STATS.COUNT_ACTIVE_COMPLETED_CONTENT, load, (PARENT, documentId)) ?? 0;
            var versionRows = await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCES_BY_PARENT, load, (PARENT, documentId));
            var firstRow = versionRows?.FirstOrDefault();
            if (firstRow != null && activeContent > 0 && firstRow.GetInt("document_delete_state") == 0) {
                await QueueDocumentEvent(moduleCuid, VaultStatsEventType.Delete, firstRow, new VaultStatsCounters { ActiveDocuments = -1, DeletedDocuments = 1 }, load);
            }

            foreach (var row in versionRows ?? Enumerable.Empty<DbRow>()) {
                if (!IsCompletedSource(row) || row.GetInt("version_delete_state") != 0) continue;
                await QueueVersionEvent(moduleCuid, VaultStatsEventType.Delete, row, ToDeleteDelta(row), load);
            }
        }

        async Task QueueVersionSoftDeleteStatsEvents(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo, DbExecutionLoad load) {
            var rows = subVersionNo == 0
                ? await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCES_BY_VERSION, load, (PARENT, documentId), (VERSION, versionNo))
                : new DbRows { await _agw.RowAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCE, load, (ID, versionId)) };

            var activeOthers = await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.STATS.COUNT_ACTIVE_COMPLETED_CONTENT_EXCLUDING, load, (PARENT, documentId), (ID, versionId)) ?? 0;
            var contentDeleting = false;
            foreach (var row in rows.Where(r => r != null)) {
                if (!IsCompletedSource(row) || row.GetInt("version_delete_state") != 0) continue;
                if (row.GetInt("sub_version_no") == 0) contentDeleting = true;
                await QueueVersionEvent(moduleCuid, VaultStatsEventType.Delete, row, ToDeleteDelta(row), load);
            }

            var source = rows.FirstOrDefault(r => r != null);
            if (contentDeleting && activeOthers == 0 && source != null)
                await QueueDocumentEvent(moduleCuid, VaultStatsEventType.Delete, source, new VaultStatsCounters { ActiveDocuments = -1 }, load);
        }

        async Task QueueDocumentRestoreStatsEvents(string moduleCuid, long documentId, DbExecutionLoad load) {
            var versionRows = await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCES_BY_PARENT, load, (PARENT, documentId));
            var firstRow = versionRows?.FirstOrDefault();
            var hasRestorableContent = versionRows?.Any(row => IsCompletedSource(row) && row.GetInt("sub_version_no") == 0 && IsRestorableDeleteState(row.GetInt("version_delete_state"))) == true;
            if (firstRow != null && firstRow.GetInt("document_delete_state") is 1 or 2 && hasRestorableContent)
                await QueueDocumentEvent(moduleCuid, VaultStatsEventType.Restore, firstRow, new VaultStatsCounters { ActiveDocuments = 1, DeletedDocuments = -1 }, load);

            foreach (var row in versionRows ?? Enumerable.Empty<DbRow>()) {
                if (!IsCompletedSource(row) || !IsRestorableDeleteState(row.GetInt("version_delete_state"))) continue;
                await QueueVersionEvent(moduleCuid, VaultStatsEventType.Restore, row, ToRestoreDelta(row), load);
            }
        }

        async Task QueueVersionRestoreStatsEvents(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo, DbExecutionLoad load) {
            var rows = subVersionNo == 0
                ? await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCES_BY_VERSION, load, (PARENT, documentId), (VERSION, versionNo))
                : new DbRows { await _agw.RowAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCE, load, (ID, versionId)) };

            var activeContent = await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.STATS.COUNT_ACTIVE_COMPLETED_CONTENT, load, (PARENT, documentId)) ?? 0;
            var restoresContent = rows.Any(row => row != null && IsCompletedSource(row) && row.GetInt("sub_version_no") == 0 && IsRestorableDeleteState(row.GetInt("version_delete_state")));

            foreach (var row in rows.Where(r => r != null)) {
                if (!IsCompletedSource(row) || !IsRestorableDeleteState(row.GetInt("version_delete_state"))) continue;
                await QueueVersionEvent(moduleCuid, VaultStatsEventType.Restore, row, ToRestoreDelta(row), load);
            }

            var source = rows.FirstOrDefault(r => r != null);
            if (restoresContent && activeContent == 0 && source != null)
                await QueueDocumentEvent(moduleCuid, VaultStatsEventType.Restore, source, new VaultStatsCounters { ActiveDocuments = 1 }, load);
        }

        async Task QueueDocumentArchiveStatsEvents(string moduleCuid, long documentId, DbExecutionLoad load) {
            var versionRows = await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCES_BY_PARENT, load, (PARENT, documentId));
            foreach (var row in versionRows ?? Enumerable.Empty<DbRow>()) {
                if (!IsCompletedSource(row) || row.GetInt("version_delete_state") != 1) continue;
                await QueueVersionEvent(moduleCuid, VaultStatsEventType.Archive, row, new VaultStatsCounters { ArchivedBytes = row.GetLong("size") }, load);
            }
        }

        async Task QueueVersionArchiveStatsEvents(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo, DbExecutionLoad load) {
            var rows = subVersionNo == 0
                ? await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCES_BY_VERSION, load, (PARENT, documentId), (VERSION, versionNo))
                : new DbRows { await _agw.RowAsync(moduleCuid, INSTANCE.STATS.GET_VERSION_SOURCE, load, (ID, versionId)) };

            foreach (var row in rows.Where(r => r != null)) {
                if (!IsCompletedSource(row) || row.GetInt("version_delete_state") != 1) continue;
                await QueueVersionEvent(moduleCuid, VaultStatsEventType.Archive, row, new VaultStatsCounters { ArchivedBytes = row.GetLong("size") }, load);
            }
        }

        async Task QueueDirectorySoftDeleteStatsEvents(string moduleCuid, IEnumerable<long> directoryIds, IEnumerable<long> documentIds, DbExecutionLoad load) {
            foreach (var documentId in documentIds.Distinct())
                await QueueDocumentSoftDeleteStatsEvents(moduleCuid, documentId, load);

            foreach (var directoryId in directoryIds.Distinct()) {
                var row = await _agw.RowAsync(moduleCuid, INSTANCE.DIRECTORY.GET_DETAILS_BY_ID_ALL, load, (VALUE, directoryId));
                if (row == null || row.Count == 0 || row.GetInt("delete_state") != 0) continue;
                await QueueFolderLifecycleEvent(moduleCuid, VaultStatsEventType.Delete, row, activeDelta: -1, deletedDelta: 1, load);
            }
        }

        async Task QueueDirectoryRestoreStatsEvents(string moduleCuid, IEnumerable<DbRow> directoryRows, IEnumerable<DeletedDocumentInfo> documents, DbExecutionLoad load) {
            foreach (var directoryRow in directoryRows.Where(r => IsRestorableDeleteState(r.GetInt("delete_state"))))
                await QueueFolderLifecycleEvent(moduleCuid, VaultStatsEventType.Restore, directoryRow, activeDelta: 1, deletedDelta: -1, load);

            foreach (var document in documents)
                await QueueDocumentRestoreStatsEvents(moduleCuid, document.DocumentId, load);
        }

        async Task QueueFolderLifecycleEvent(string moduleCuid, VaultStatsEventType eventType, DbRow row, long activeDelta, long deletedDelta, DbExecutionLoad load) {
            var workspaceId = row.GetLong("workspace");
            var parentId = row.GetLong("parent");
            var nodeType = parentId > 0 ? (int)VaultStatsNodeType.Directory : (int)VaultStatsNodeType.Workspace;
            var nodeId = parentId > 0 ? parentId : workspaceId;
            await QueueStatsEventAsync(
                moduleCuid,
                eventType,
                $"folder-state:{eventType}:{row.GetLong("id")}:{Guid.NewGuid():N}",
                nodeType,
                nodeId,
                workspaceId,
                null,
                null,
                null,
                new VaultStatsCounters { ActiveFolders = activeDelta, DeletedFolders = deletedDelta },
                load);
        }

        async Task QueueDocumentEvent(string moduleCuid, VaultStatsEventType eventType, DbRow source, VaultStatsCounters counters, DbExecutionLoad load) {
            var nodeId = source.GetLong("directory_id");
            await QueueStatsEventAsync(
                moduleCuid,
                eventType,
                $"doc-state:{eventType}:{source.GetLong("document_id")}:{Guid.NewGuid():N}",
                (int)VaultStatsNodeType.Directory,
                nodeId,
                source.GetLong("workspace"),
                source.GetLong("document_id"),
                null,
                source.GetString("ext"),
                counters,
                load);
        }

        async Task QueueVersionEvent(string moduleCuid, VaultStatsEventType eventType, DbRow source, VaultStatsCounters counters, DbExecutionLoad load) {
            await QueueStatsEventAsync(
                moduleCuid,
                eventType,
                $"version-state:{eventType}:{source.GetLong("version_id")}:{Guid.NewGuid():N}",
                (int)VaultStatsNodeType.Directory,
                source.GetLong("directory_id"),
                source.GetLong("workspace"),
                source.GetLong("document_id"),
                source.GetLong("version_id"),
                source.GetString("ext"),
                counters,
                load);
        }

        async Task QueueStatsEventAsync(
            string moduleCuid,
            VaultStatsEventType eventType,
            string eventKey,
            int nodeType,
            long nodeId,
            long workspaceId,
            long? documentId,
            long? versionId,
            string ext,
            VaultStatsCounters counters,
            DbExecutionLoad load) {
            if (counters == null || IsZero(counters)) return;

            await _agw.ExecAsync(
                moduleCuid,
                INSTANCE.STATS.QUEUE_EVENT,
                load,
                (EVENT_KEY, eventKey),
                (EVENT_TYPE, (int)eventType),
                (NODE_TYPE, nodeType),
                (NODE_ID, nodeId),
                (WORKSPACE_ID, workspaceId),
                (DOCUMENT_ID, documentId.HasValue ? documentId.Value : DBNull.Value),
                (VERSION_ID, versionId.HasValue ? versionId.Value : DBNull.Value),
                (EXT_NAME, string.IsNullOrWhiteSpace(ext) ? DBNull.Value : ext),
                (ACTIVE_FOLDERS_DELTA, counters.ActiveFolders),
                (DELETED_FOLDERS_DELTA, counters.DeletedFolders),
                (ACTIVE_DOCS_DELTA, counters.ActiveDocuments),
                (DELETED_DOCS_DELTA, counters.DeletedDocuments),
                (ACTIVE_VERSIONS_DELTA, counters.ActiveVersions),
                (DELETED_VERSIONS_DELTA, counters.DeletedVersions),
                (ACTIVE_THUMBS_DELTA, counters.ActiveThumbnails),
                (DELETED_THUMBS_DELTA, counters.DeletedThumbnails),
                (ACTIVE_BYTES_DELTA, counters.ActiveBytes),
                (DELETED_BYTES_DELTA, counters.DeletedBytes),
                (ARCHIVED_BYTES_DELTA, counters.ArchivedBytes),
                (PURGED_BYTES_DELTA, counters.PurgedBytes));
        }

        async Task ApplyStatDelta(string moduleCuid, DbRow evt, DbExecutionLoad load) {
            var nodeArgs = BuildStatOwnerArgs(evt);
            var nodeStatId = await EnsureStatOwner(
                moduleCuid,
                INSTANCE.STATS.GET_NODE_STAT_ID,
                INSTANCE.STATS.INSERT_NODE_STAT,
                INSTANCE.STATS.INSERT_STAT,
                INSTANCE.STATS.GET_LAST_STAT_ID,
                INSTANCE.STATS.DELETE_STAT_IF_UNUSED,
                load,
                nodeArgs);
            await ApplyStatDelta(moduleCuid, nodeStatId, evt, null, load);

            var treeTargets = await _agw.RowsAsync(
                moduleCuid,
                INSTANCE.STATS.GET_TREE_TARGETS,
                load,
                (WORKSPACE_ID, evt.GetLong("workspace")),
                (NODE_TYPE, evt.GetInt("node_type")),
                (NODE_ID, evt.GetLong("node_id")));

            foreach (var target in treeTargets) {
                var treeArgs = BuildStatOwnerArgs(evt, target);
                var treeStatId = await EnsureStatOwner(
                    moduleCuid,
                    INSTANCE.STATS.GET_TREE_STAT_ID,
                    INSTANCE.STATS.INSERT_TREE_STAT,
                    INSTANCE.STATS.INSERT_STAT,
                    INSTANCE.STATS.GET_LAST_STAT_ID,
                    INSTANCE.STATS.DELETE_STAT_IF_UNUSED,
                    load,
                    treeArgs);
                await ApplyStatDelta(moduleCuid, treeStatId, evt, target, load);
            }

            if (string.IsNullOrWhiteSpace(evt.GetString("ext"))) return;

            var nodeExtArgs = BuildStatOwnerArgs(evt, includeExtension: true);
            var nodeExtStatId = await EnsureStatOwner(
                moduleCuid,
                INSTANCE.STATS.GET_NODE_EXT_STAT_ID,
                INSTANCE.STATS.INSERT_NODE_EXT_STAT,
                INSTANCE.STATS.INSERT_STAT,
                INSTANCE.STATS.GET_LAST_STAT_ID,
                INSTANCE.STATS.DELETE_STAT_IF_UNUSED,
                load,
                nodeExtArgs);
            await ApplyStatDelta(moduleCuid, nodeExtStatId, evt, null, load);

            foreach (var target in treeTargets) {
                var treeExtArgs = BuildStatOwnerArgs(evt, target, includeExtension: true);
                var treeExtStatId = await EnsureStatOwner(
                    moduleCuid,
                    INSTANCE.STATS.GET_TREE_EXT_STAT_ID,
                    INSTANCE.STATS.INSERT_TREE_EXT_STAT,
                    INSTANCE.STATS.INSERT_STAT,
                    INSTANCE.STATS.GET_LAST_STAT_ID,
                    INSTANCE.STATS.DELETE_STAT_IF_UNUSED,
                    load,
                    treeExtArgs);
                await ApplyStatDelta(moduleCuid, treeExtStatId, evt, target, load);
            }
        }

        async Task<long> EnsureStatOwner(
            string adapter,
            string getOwnerQuery,
            string insertOwnerQuery,
            string insertStatQuery,
            string getLastStatIdQuery,
            string cleanupStatQuery,
            DbExecutionLoad load,
            DbArg[] ownerArgs) {
            var current = await _agw.ScalarAsync<long?>(adapter, getOwnerQuery, load, ownerArgs);
            if (current.HasValue && current.Value > 0) return current.Value;

            await _agw.ExecAsync(adapter, insertStatQuery, load);
            var statId = await _agw.ScalarAsync<long?>(adapter, getLastStatIdQuery, load);
            if (!statId.HasValue || statId.Value < 1)
                throw new InvalidOperationException("Unable to create the statistics payload.");

            var insertArgs = ownerArgs.Concat(new DbArg[] { (STAT_ID, statId.Value) }).ToArray();
            try {
                await _agw.ExecAsync(adapter, insertOwnerQuery, load, insertArgs);
                return statId.Value;
            } catch {
                try {
                    await _agw.ExecAsync(adapter, cleanupStatQuery, load, (STAT_ID, statId.Value));
                    current = await _agw.ScalarAsync<long?>(adapter, getOwnerQuery, load, ownerArgs);
                    if (current.HasValue && current.Value > 0) return current.Value;
                } catch {
                    // Preserve the original relation-insert failure.
                }
                throw;
            }
        }

        async Task ApplyStatDelta(string moduleCuid, long statId, DbRow evt, DbRow target, DbExecutionLoad load) {
            var args = BuildEventDeltaArgs(evt, target)
                .Concat(new DbArg[] { (STAT_ID, statId) })
                .ToArray();
            await _agw.ExecAsync(moduleCuid, INSTANCE.STATS.APPLY_STAT_DELTA, load, args);
        }

        DbArg[] BuildStatOwnerArgs(DbRow evt, DbRow target = null, bool includeExtension = false) {
            var args = new List<DbArg> {
                (NODE_TYPE, target?.GetInt("node_type") ?? evt.GetInt("node_type")),
                (NODE_ID, target?.GetLong("node_id") ?? evt.GetLong("node_id")),
                (WORKSPACE_ID, target?.GetLong("workspace") ?? evt.GetLong("workspace"))
            };
            if (includeExtension)
                args.Add((EXT_NAME, evt.GetString("ext")));
            return args.ToArray();
        }

        DbArg[] BuildEventDeltaArgs(DbRow evt, DbRow target = null) {
            var nodeType = target?.GetInt("node_type") ?? evt.GetInt("node_type");
            var nodeId = target?.GetLong("node_id") ?? evt.GetLong("node_id");
            var workspaceId = target?.GetLong("workspace") ?? evt.GetLong("workspace");
            return new DbArg[] {
                (NODE_TYPE, nodeType),
                (NODE_ID, nodeId),
                (WORKSPACE_ID, workspaceId),
                (EXT_NAME, string.IsNullOrWhiteSpace(evt.GetString("ext")) ? DBNull.Value : evt.GetString("ext")),
                (ACTIVE_FOLDERS_DELTA, evt.GetLong("active_folders_delta")),
                (DELETED_FOLDERS_DELTA, evt.GetLong("deleted_folders_delta")),
                (ACTIVE_DOCS_DELTA, evt.GetLong("active_docs_delta")),
                (DELETED_DOCS_DELTA, evt.GetLong("deleted_docs_delta")),
                (ACTIVE_VERSIONS_DELTA, evt.GetLong("active_versions_delta")),
                (DELETED_VERSIONS_DELTA, evt.GetLong("deleted_versions_delta")),
                (ACTIVE_THUMBS_DELTA, evt.GetLong("active_thumbs_delta")),
                (DELETED_THUMBS_DELTA, evt.GetLong("deleted_thumbs_delta")),
                (ACTIVE_BYTES_DELTA, evt.GetLong("active_bytes_delta")),
                (DELETED_BYTES_DELTA, evt.GetLong("deleted_bytes_delta")),
                (ARCHIVED_BYTES_DELTA, evt.GetLong("archived_bytes_delta")),
                (PURGED_BYTES_DELTA, evt.GetLong("purged_bytes_delta"))
            };
        }

        async Task RefreshCoreStats(string moduleCuid) {
            var coreModuleCuid = Guid.TryParse(moduleCuid, out var parsedModuleCuid)
                ? parsedModuleCuid.ToString("N")
                : moduleCuid.Trim();
            var module = await _agw.RowAsync(_key, STATS_CORE.GET_MODULE_IDS, default, (CUID, coreModuleCuid));
            if (module == null || module.Count == 0)
                throw new InvalidOperationException($"Module CUID '{coreModuleCuid}' was not found in the core registry while refreshing statistics.");

            var moduleId = module.GetLong("module_id");
            var clientId = module.GetLong("client_id");
            var workspaceRows = await _agw.RowsAsync(moduleCuid, INSTANCE.STATS.GET_WORKSPACE_TREE_STATS);

            var handler = _agw.GetTransactionHandler(_key);
            using (handler?.Begin()) {
                var load = new DbExecutionLoad(default, handler);
                foreach (var row in workspaceRows ?? Enumerable.Empty<DbRow>()) {
                    var ownerArgs = new DbArg[] {
                        (WORKSPACE_ID, row.GetLong("node_id")),
                        (ID, moduleId),
                        (PARENT, clientId)
                    };
                    var statId = await EnsureStatOwner(
                        _key,
                        STATS_CORE.GET_WORKSPACE_STAT_ID,
                        STATS_CORE.INSERT_WORKSPACE_STAT,
                        STATS_CORE.INSERT_STAT,
                        STATS_CORE.GET_LAST_STAT_ID,
                        STATS_CORE.DELETE_STAT_IF_UNUSED,
                        load,
                        ownerArgs);
                    await OverwriteStat(_key, statId, MapCounters(row), load);
                }

                var moduleStatId = await EnsureStatOwner(
                    _key,
                    STATS_CORE.GET_MODULE_STAT_ID,
                    STATS_CORE.INSERT_MODULE_STAT,
                    STATS_CORE.INSERT_STAT,
                    STATS_CORE.GET_LAST_STAT_ID,
                    STATS_CORE.DELETE_STAT_IF_UNUSED,
                    load,
                    new DbArg[] { (ID, moduleId), (PARENT, clientId) });
                var moduleTotals = await _agw.RowAsync(_key, STATS_CORE.GET_MODULE_TOTALS, load, (ID, moduleId));
                await OverwriteStat(_key, moduleStatId, MapCounters(moduleTotals), load);

                var clientStatId = await EnsureStatOwner(
                    _key,
                    STATS_CORE.GET_CLIENT_STAT_ID,
                    STATS_CORE.INSERT_CLIENT_STAT,
                    STATS_CORE.INSERT_STAT,
                    STATS_CORE.GET_LAST_STAT_ID,
                    STATS_CORE.DELETE_STAT_IF_UNUSED,
                    load,
                    new DbArg[] { (PARENT, clientId) });
                var clientTotals = await _agw.RowAsync(_key, STATS_CORE.GET_CLIENT_TOTALS, load, (PARENT, clientId));
                await OverwriteStat(_key, clientStatId, MapCounters(clientTotals), load);
            }
        }

        async Task OverwriteStat(string adapter, long statId, VaultStatsCounters counters, DbExecutionLoad load) {
            await _agw.ExecAsync(
                adapter,
                STATS_CORE.OVERWRITE_STAT,
                load,
                (STAT_ID, statId),
                (ACTIVE_FOLDERS_DELTA, counters.ActiveFolders),
                (DELETED_FOLDERS_DELTA, counters.DeletedFolders),
                (ACTIVE_DOCS_DELTA, counters.ActiveDocuments),
                (DELETED_DOCS_DELTA, counters.DeletedDocuments),
                (ACTIVE_VERSIONS_DELTA, counters.ActiveVersions),
                (DELETED_VERSIONS_DELTA, counters.DeletedVersions),
                (ACTIVE_THUMBS_DELTA, counters.ActiveThumbnails),
                (DELETED_THUMBS_DELTA, counters.DeletedThumbnails),
                (ACTIVE_BYTES_DELTA, counters.ActiveBytes),
                (DELETED_BYTES_DELTA, counters.DeletedBytes),
                (ARCHIVED_BYTES_DELTA, counters.ArchivedBytes),
                (PURGED_BYTES_DELTA, counters.PurgedBytes));
        }

        static VaultStatsCounters ToDeleteDelta(DbRow row) {
            var size = row.GetLong("size");
            var isThumb = row.GetInt("sub_version_no") > 0;
            return new VaultStatsCounters {
                ActiveVersions = isThumb ? 0 : -1,
                DeletedVersions = isThumb ? 0 : 1,
                ActiveThumbnails = isThumb ? -1 : 0,
                DeletedThumbnails = isThumb ? 1 : 0,
                ActiveBytes = -size,
                DeletedBytes = size
            };
        }

        static VaultStatsCounters ToRestoreDelta(DbRow row) {
            var size = row.GetLong("size");
            var isThumb = row.GetInt("sub_version_no") > 0;
            var wasArchived = row.GetInt("version_delete_state") == 2;
            return new VaultStatsCounters {
                ActiveVersions = isThumb ? 0 : 1,
                DeletedVersions = isThumb ? 0 : -1,
                ActiveThumbnails = isThumb ? 1 : 0,
                DeletedThumbnails = isThumb ? -1 : 0,
                ActiveBytes = size,
                DeletedBytes = -size,
                ArchivedBytes = wasArchived ? -size : 0
            };
        }

        static bool IsCompletedSource(DbRow row)
            => HasFlag(row.GetInt("flags"), VersionFlags.Completed);

        static bool HasFlag(int flags, VersionFlags flag)
            => (flags & (int)flag) == (int)flag;

        static bool IsZero(VaultStatsCounters counters)
            => counters.ActiveFolders == 0 &&
               counters.DeletedFolders == 0 &&
               counters.ActiveDocuments == 0 &&
               counters.DeletedDocuments == 0 &&
               counters.ActiveVersions == 0 &&
               counters.DeletedVersions == 0 &&
               counters.ActiveThumbnails == 0 &&
               counters.DeletedThumbnails == 0 &&
               counters.ActiveBytes == 0 &&
               counters.DeletedBytes == 0 &&
               counters.ArchivedBytes == 0 &&
               counters.PurgedBytes == 0;

        static object NormalizeStatExtension(string extension) {
            if (string.IsNullOrWhiteSpace(extension)) return DBNull.Value;
            var normalized = extension.Trim().ToLowerInvariant();
            if (normalized == "*") return DBNull.Value;
            if (!normalized.StartsWith('.') && normalized != VaultConstants.DEFAULT_NAME)
                normalized = "." + normalized;
            return normalized.ToDBName();
        }

        static VaultStatsCounters MapCounters(DbRow row) {
            if (row == null || row.Count == 0) return new VaultStatsCounters();
            return new VaultStatsCounters {
                ActiveFolders = row.GetLong("active_folders"),
                DeletedFolders = row.GetLong("deleted_folders"),
                ActiveDocuments = row.GetLong("active_docs"),
                DeletedDocuments = row.GetLong("deleted_docs"),
                ActiveVersions = row.GetLong("active_versions"),
                DeletedVersions = row.GetLong("deleted_versions"),
                ActiveThumbnails = row.GetLong("active_thumbs"),
                DeletedThumbnails = row.GetLong("deleted_thumbs"),
                ActiveBytes = row.GetLong("active_bytes"),
                DeletedBytes = row.GetLong("deleted_bytes"),
                ArchivedBytes = row.GetLong("archived_bytes"),
                PurgedBytes = row.GetLong("purged_bytes")
            };
        }

        static void AddExtensionStats(VaultStatsSnapshot snapshot, IEnumerable<DbRow> directRows, IEnumerable<DbRow> recursiveRows) {
            var byExt = new Dictionary<string, VaultExtensionStats>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in directRows ?? Enumerable.Empty<DbRow>()) {
                var ext = row.GetString("ext") ?? VaultConstants.DEFAULT_NAME;
                if (!byExt.TryGetValue(ext, out var stat)) {
                    stat = new VaultExtensionStats { Extension = ext };
                    byExt[ext] = stat;
                }
                stat.Direct = MapCounters(row);
            }

            foreach (var row in recursiveRows ?? Enumerable.Empty<DbRow>()) {
                var ext = row.GetString("ext") ?? VaultConstants.DEFAULT_NAME;
                if (!byExt.TryGetValue(ext, out var stat)) {
                    stat = new VaultExtensionStats { Extension = ext };
                    byExt[ext] = stat;
                }
                stat.Recursive = MapCounters(row);
            }

            snapshot.Extensions = byExt.Values.OrderBy(v => v.Extension).ToList();
        }
    }
}
