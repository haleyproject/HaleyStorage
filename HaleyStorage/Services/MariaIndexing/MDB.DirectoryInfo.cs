using Haley.Abstractions;
using Haley.Models;
using static Haley.Internal.IndexingQueries;
using static Haley.Internal.IndexingConstant;

namespace Haley.Utils;

internal partial class MariaDBIndexing {
    public async Task<VaultDirectoryInfo> GetDirectoryMetadata(IVaultReadRequest request) {
        await DemandDirectoryAccess(request);
        var module = request.Scope.Module.Cuid.ToString("N");
        var workspace = await ResolveWorkspaceId(request.Scope.Workspace.Cuid.ToString("N"));
        var folder = await ResolveFolderInfo(module, request, workspace);
        if (!folder.status || folder.isRoot) throw new InvalidOperationException("An active directory is required.");
        var row = await _agw.RowAsync(module, INSTANCE.DIRECTORY_INFO.GET, default, (ID, folder.id));
        var root = row?.GetString("uri") ?? string.Empty;
        var document = string.IsNullOrWhiteSpace(root) ? null : await _agw.RowAsync(module, INSTANCE.DIRECTORY_INFO.THUMBNAIL_DOCUMENT, default, (VALUE, ToDbCuid(root)));
        return new VaultDirectoryInfo {
            DirectoryId = folder.id, FolderCuid = folder.cuid, DisplayName = folder.displayName,
            Metadata = row?.GetString("metadata") ?? string.Empty, ThumbnailRootCuid = root,
            HasThumbnail = document != null && document.GetInt("delete_state") == 0 && document.GetInt("completed") > 0,
            IsHidden = await IsHiddenDirectory(module, folder.id)
        };
    }

    public async Task SetDirectoryMetadata(IVaultReadRequest request, string metadata) {
        var info = await GetDirectoryMetadata(request);
        await _agw.ExecAsync(request.Scope.Module.Cuid.ToString("N"), INSTANCE.DIRECTORY_INFO.SET_METADATA, default,
            (ID, info.DirectoryId), (VALUE, string.IsNullOrEmpty(metadata) ? DBNull.Value : metadata));
    }

    public async Task RenameDirectory(IVaultReadRequest request, string name) {
        DirectoryVisibility.ValidateName(name, request.AllowHiddenDirectories);
        var info = await GetDirectoryMetadata(request);
        var module = request.Scope.Module.Cuid.ToString("N");
        var gate = _statsLocks.GetOrAdd(module, _ => new System.Threading.SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try {
            var handler = _agw.GetTransactionHandler(module);
            using (handler?.Begin()) {
                var load = new DbExecutionLoad(default, handler);
                await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.RENAME, load,
                    (ID, info.DirectoryId), (NAME, name.ToDBName()), (DNAME, name.Trim()));
                await RebuildStatsInternal(module, load);
            }
            await RefreshCoreStats(module);
        } finally { gate.Release(); }
    }

    public async Task<DirectoryThumbnailTarget> EnsureDirectoryThumbnail(IVaultReadRequest request, string extension) {
        var info = await GetDirectoryMetadata(request);
        var module = request.Scope.Module.Cuid.ToString("N");
        var childRequest = new StorageReadRequest(request.Scope.Client.Name, request.Scope.Module.Name, request.Scope.Workspace.Name) {
            AllowHiddenDirectories = true, Actor = request.Actor
        };
        childRequest.Scope.Folder = new StorageFolderRoute { Cuid = info.FolderCuid };
        var child = await RegisterDirectory(childRequest, ".thumb");
        if (!child.Status) throw new InvalidOperationException(child.Message);
        var workspace = await ResolveWorkspaceId(request.Scope.Workspace.Cuid.ToString("N"));
        var name = $"dir-thumb-{Guid.NewGuid():N}{extension}";
        string root;
        var handler = _agw.GetTransactionHandler(module);
        using (handler?.Begin()) {
            var load = new DbExecutionLoad(default, handler);
            await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.ENSURE, load, (ID, info.DirectoryId));
            root = await _agw.ScalarAsync<string>(module, INSTANCE.DIRECTORY_INFO.LOCK, load, (ID, info.DirectoryId));
            if (string.IsNullOrWhiteSpace(root)) {
                var nameStore = await EnsureNameStore(module, name);
                if (!nameStore.status) throw new InvalidOperationException("Unable to allocate the thumbnail name.");
                await _agw.ExecAsync(module, INSTANCE.DOCUMENT.INSERT, load, (WSPACE, workspace), (PARENT, child.Result.id), (NAME, nameStore.id));
                var doc = await _agw.RowAsync(module, INSTANCE.DOCUMENT.EXISTS, load, (PARENT, child.Result.id), (NAME, nameStore.id));
                root = doc?.GetString("uid") ?? throw new InvalidOperationException("Unable to allocate the thumbnail document.");
                await _agw.ExecAsync(module, INSTANCE.DOCUMENT.INSERT_INFO, load, (PARENT, doc.GetLong("id")), (DNAME, name), (ACTOR, request.Actor ?? 0));
                await _agw.ExecAsync(module, INSTANCE.DIRECTORY_INFO.SET_THUMBNAIL, load, (ID, info.DirectoryId), (VALUE, root));
            }
        }
        var document = await _agw.RowAsync(module, INSTANCE.DIRECTORY_INFO.THUMBNAIL_DOCUMENT, default, (VALUE, ToDbCuid(root)));
        if (document == null || document.GetInt("delete_state") != 0 || document.GetLong("parent") != child.Result.id)
            throw new InvalidOperationException("The managed thumbnail document was deleted or moved. Restore it before replacing the thumbnail.");
        return new DirectoryThumbnailTarget {
            FolderCuid = child.Result.cuid, RootCuid = root,
            FileName = document.GetString("file_name") ?? name, HasVersion = document.GetInt("has_version") > 0
        };
    }
}
