using Haley.Abstractions;
using Haley.Models;
using Haley.Utils;

namespace Haley.Services;

public partial class StorageCoordinator {
    private readonly SemaphoreSlim[] _directoryThumbnailLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    private SemaphoreSlim DirectoryThumbnailGate(string folderCuid) =>
        _directoryThumbnailLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(folderCuid) % (uint)_directoryThumbnailLocks.Length];

    public async Task<IFeedback<VaultDirectoryInfo>> GetDirectoryMetadata(IVaultReadRequest request) {
        var result = new Feedback<VaultDirectoryInfo>() { Status = false };
        try {
            PrepareRequestContext(request);
            var info = await Indexer.GetDirectoryMetadata(request);
            return result.SetStatus(true).SetResult(info);
        } catch (Exception ex) { return result.SetMessage(ex.Message); }
    }

    public async Task<IFeedback> SetDirectoryMetadata(IVaultReadRequest request, string metadata) {
        var result = new Feedback() { Status = false };
        try {
            PrepareRequestContext(request);
            var access = CheckWriteAccess(request);
            if (!access.Status) return access;
            await Indexer.SetDirectoryMetadata(request, metadata);
            return result.SetStatus(true);
        } catch (Exception ex) { return result.SetMessage(ex.Message); }
    }

    public async Task<IFeedback> RenameDirectory(IVaultReadRequest request, string name) {
        var result = new Feedback() { Status = false };
        try {
            PrepareRequestContext(request);
            var access = CheckWriteAccess(request);
            if (!access.Status) return access;
            await Indexer.RenameDirectory(request, name);
            return result.SetStatus(true);
        } catch (Exception ex) { return result.SetMessage(ex.Message); }
    }

    public async Task<IVaultResponse> UploadDirectoryThumbnail(IVaultReadRequest request, Stream stream, string fileName) {
        var result = new VaultResponse() { Status = false };
        SemaphoreSlim gate = null;
        try {
            PrepareRequestContext(request);
            var access = CheckWriteAccess(request);
            if (!access.Status) { result.Message = access.Message; return result; }
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            var allowed = (Config?.ThumbAllowedExtensions ?? "jpeg,jpg,png,webp,gif").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (extension.Length < 2 || !allowed.Contains(extension.TrimStart('.'), StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("The thumbnail image extension is not allowed.");
            var limit = Math.Max(0, Config?.ThumbMaxSizeKb ?? 500) * 1024L;
            if (stream == null || stream.Length == 0 || (limit > 0 && stream.Length > limit))
                throw new ArgumentException("The thumbnail is empty or exceeds the configured thumbnail size limit.");
            var info = await Indexer.GetDirectoryMetadata(request);
            gate = DirectoryThumbnailGate(info.FolderCuid);
            await gate.WaitAsync();
            var target = await Indexer.EnsureDirectoryThumbnail(request, extension);
            var upload = new StorageWriteRequest(request.Scope.Client.Name, request.Scope.Module.Name, request.Scope.Workspace.Name) {
                AllowHiddenDirectories = true, Actor = request.Actor, OriginalName = fileName,
                RequestedName = target.FileName, FileStream = stream, ReplaceExistingFile = false,
                IsDirectoryThumbnail = true
            };
            upload.Scope.Folder = new StorageFolderRoute { Cuid = target.FolderCuid };
            if (target.HasVersion) upload.SetFile(new StorageFileRoute { RootCuid = target.RootCuid });
            return await Upload(upload);
        } catch (Exception ex) { result.Message = ex.Message; return result; }
        finally { gate?.Release(); }
    }

    public async Task<IVaultStreamResponse> DownloadDirectoryThumbnail(IVaultReadRequest request) {
        try {
            PrepareRequestContext(request);
            var info = await Indexer.GetDirectoryMetadata(request);
            if (!info.HasThumbnail) return new VaultStreamResponse { Status = false, Message = "No directory thumbnail is available." };
            var read = new StorageReadFileRequest(request.Scope.Client.Name, request.Scope.Module.Name, request.Scope.Workspace.Name) { AllowHiddenDirectories = true };
            read.SetFile(new StorageFileRoute { RootCuid = info.ThumbnailRootCuid });
            var downloaded = await Download(read);
            if (downloaded.Status && !string.IsNullOrWhiteSpace(downloaded.Extension))
                downloaded.SaveName = Path.ChangeExtension(downloaded.SaveName, downloaded.Extension);
            return downloaded;
        } catch (Exception ex) { return new VaultStreamResponse { Status = false, Message = ex.Message }; }
    }

    public async Task<bool> CanAccessChunkDirectory(string versionCuid, bool allowHidden) {
        if (allowHidden) return true;
        try {
            var status = await TryRehydrateChunkSession(versionCuid);
            if (status == null) return false;
            var meta = _chunkSessions.TryGetValue(status.VersionId, out var session)
                ? session.Meta : await ReadMetadataAsync(Path.Combine(ChunkRoot, versionCuid), CancellationToken.None);
            return meta != null && Indexer != null && !await Indexer.IsHiddenVersion(meta.ModuleCuid, meta.VersionId);
        } catch { return false; }
    }
}
