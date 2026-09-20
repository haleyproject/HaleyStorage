using Haley.Abstractions;
using Haley.Models;
using static Haley.Internal.IndexingConstant;
using static Haley.Internal.IndexingQueries;

namespace Haley.Utils;

internal partial class MariaDBIndexing {
    public async Task<DeletedDocumentInfo> GetDirectoryThumbnailDocument(IVaultReadRequest request) {
        var info = await GetDirectoryMetadata(request);
        if (string.IsNullOrWhiteSpace(info.ThumbnailRootCuid)) return null;
        var module = request.Scope.Module.Cuid.ToString("N");
        var id = await _agw.ScalarAsync<long?>(module, INSTANCE.DIRECTORY_INFO.MANAGED_DOCUMENT, default,
            (ID, info.DirectoryId), (VALUE, ToDbCuid(info.ThumbnailRootCuid)));
        if (!id.HasValue) {
            var exists = await _agw.RowAsync(module, INSTANCE.DIRECTORY_INFO.THUMBNAIL_DOCUMENT, default, (VALUE, ToDbCuid(info.ThumbnailRootCuid)));
            if (exists != null) throw new InvalidOperationException("The managed thumbnail was moved or renamed. Restore its location and name before deleting it through the folder.");
            return null;
        }
        return await GetDocumentLifecycleById(module, id.Value);
    }

    public async Task ClearDirectoryThumbnail(IVaultReadRequest request, string rootCuid) {
        var info = await GetDirectoryMetadata(request);
        await _agw.ExecAsync(request.Scope.Module.Cuid.ToString("N"), INSTANCE.DIRECTORY_INFO.CLEAR_THUMBNAIL, default,
            (ID, info.DirectoryId), (VALUE, ToDbCuid(rootCuid)));
    }

    public async Task DeleteLatestDirectoryThumbnail(IVaultReadRequest request, DeletedDocumentInfo document) {
        var info = await GetDirectoryMetadata(request);
        var module = request.Scope.Module.Cuid.ToString("N");
        var handler = _agw.GetTransactionHandler(module);
        using (handler?.Begin()) {
            var load = new DbExecutionLoad(default, handler);
            var assigned = await _agw.ScalarAsync<string>(module, INSTANCE.DIRECTORY_INFO.LOCK, load, (ID, info.DirectoryId));
            if (!SameCuid(assigned, document.DocumentCuid))
                throw new InvalidOperationException("The directory thumbnail changed. Reload the folder and try again.");
            var state = await _agw.ScalarAsync<int?>(module, INSTANCE.DOCUMENT.LOCK_DELETE_STATE, load, (ID, document.DocumentId));
            if (state == 3) throw new InvalidOperationException("Permanent thumbnail deletion is pending. Retry Delete permanently.");
            var latest = await _agw.RowAsync(module, INSTANCE.DIRECTORY_INFO.LATEST_VERSION, load, (ID, document.DocumentId));
            if (state == 0 && latest != null) {
                await TryQueueVersionSoftDeleteStatsEvents(module, document.DocumentId, latest.GetLong("id"), latest.GetInt("ver"), 0, load);
                await _agw.ExecAsync(module, INSTANCE.DOCVERSION.SOFT_DELETE_BY_VERSION, load,
                    (PARENT, document.DocumentId), (VERSION, latest.GetInt("ver")), (DELETED, DateTime.UtcNow));
            }
            await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.CLEAR_THUMBNAIL, load,
                (ID, info.DirectoryId), (VALUE, ToDbCuid(document.DocumentCuid)));
        }
    }

    public async Task<DeletedDocumentInfo> PrepareDirectoryThumbnailPurge(IVaultReadRequest request, string rootCuid) {
        var info = await GetDirectoryMetadata(request);
        var module = request.Scope.Module.Cuid.ToString("N");
        var document = await GetDirectoryThumbnailDocument(request)
            ?? throw new InvalidOperationException("The directory thumbnail is no longer assigned.");
        var handler = _agw.GetTransactionHandler(module);
        using (handler?.Begin()) {
            var load = new DbExecutionLoad(default, handler);
            var assigned = await _agw.ScalarAsync<string>(module, INSTANCE.DIRECTORY_INFO.LOCK, load, (ID, info.DirectoryId));
            if (!SameCuid(assigned, rootCuid) || !SameCuid(document.DocumentCuid, rootCuid))
                throw new InvalidOperationException("The directory thumbnail changed. Reload the folder and try again.");
            var state = await _agw.ScalarAsync<int?>(module, INSTANCE.DOCUMENT.LOCK_DELETE_STATE, load, (ID, document.DocumentId));
            if (state == 0) await TryQueueDocumentSoftDeleteStatsEvents(module, document.DocumentId, load);
            // Record irreversible intent before removing bytes. Failed removals keep the association for retry.
            await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.PURGE_DOCUMENT, load, (ID, document.DocumentId));
            await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.DELETE_ACTIVE_VERSIONS, load, (ID, document.DocumentId));
        }
        return await GetDocumentLifecycleById(module, document.DocumentId);
    }

    public async Task CompleteDirectoryThumbnailPurge(IVaultReadRequest request, DeletedDocumentInfo document) {
        var info = await GetDirectoryMetadata(request);
        var module = request.Scope.Module.Cuid.ToString("N");
        var gate = _statsLocks.GetOrAdd(module, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try {
            var handler = _agw.GetTransactionHandler(module);
            using (handler?.Begin()) {
                var load = new DbExecutionLoad(default, handler);
                await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.PURGE_VERSIONS, load, (ID, document.DocumentId));
                await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.CLEAR_THUMBNAIL, load,
                    (ID, info.DirectoryId), (VALUE, ToDbCuid(document.DocumentCuid)));
                await RebuildStatsInternal(module, load);
            }
            await RefreshCoreStats(module);
        } finally { gate.Release(); }
    }
}
