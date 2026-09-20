using Haley.Abstractions;
using Haley.Models;
using Microsoft.Extensions.Logging;

namespace Haley.Services;

public partial class StorageCoordinator {
    public async Task<IFeedback> DeleteDirectoryThumbnail(IVaultReadRequest request, bool permanent = false) {
        var result = new Feedback { Status = false };
        SemaphoreSlim gate = null;
        try {
            PrepareRequestContext(request);
            var access = CheckWriteAccess(request);
            if (!access.Status) return access;
            var info = await Indexer.GetDirectoryMetadata(request);
            gate = DirectoryThumbnailGate(info.FolderCuid);
            await gate.WaitAsync();
            info = await Indexer.GetDirectoryMetadata(request);
            if (string.IsNullOrWhiteSpace(info.ThumbnailRootCuid)) return result.SetStatus(true);
            var document = await Indexer.GetDirectoryThumbnailDocument(request);
            if (document != null && permanent) {
                await EnsureWorkspaceContextAsync(request, forceRefresh: true);
                // Validate every provider/path before changing the deletion state.
                DirectoryThumbnailPaths(request, document);
                document = await Indexer.PrepareDirectoryThumbnailPurge(request, info.ThumbnailRootCuid);
                foreach (var path in DirectoryThumbnailPaths(request, document)) {
                    try { File.Delete(path); }
                    catch (DirectoryNotFoundException) { /* An absent archive directory is already removed. */ }
                }
                await Indexer.CompleteDirectoryThumbnailPurge(request, document);
                return result.SetStatus(true);
            }
            if (document?.DeleteState == 3)
                return result.SetMessage("Permanent thumbnail deletion is pending. Retry Delete permanently to finish removing its stored files.");
            if (document != null) await Indexer.DeleteLatestDirectoryThumbnail(request, document);
            else await Indexer.ClearDirectoryThumbnail(request, info.ThumbnailRootCuid);
            return result.SetStatus(true);
        } catch (Exception ex) {
            _logger?.LogError(ex, "DirectoryThumbnailDelete failed (Permanent: {Permanent})", permanent);
            return result.SetMessage(ex.Message);
        } finally { gate?.Release(); }
    }

    private HashSet<string> DirectoryThumbnailPaths(IVaultReadRequest request, DeletedDocumentInfo document) {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var version in document.Versions) {
            var providers = GetProvidersForProfile(version.ProfileInfoId ?? 0, request.Scope.Module.Cuid.ToString("N"));
            Add(providers.primary, version.StorageRef);
            Add(providers.staging, version.StagingRef);
        }
        return paths;

        void Add(IStorageProvider provider, string storageRef) {
            if (string.IsNullOrWhiteSpace(storageRef)) return;
            if (provider is not FileSystemStorageProvider fileSystem)
                throw new NotSupportedException("Permanent directory thumbnail deletion requires filesystem storage for every stored version.");
            var workspace = FetchWorkspaceBasePath(request, fileSystem);
            paths.Add(Within(workspace, fileSystem.BuildFullPath(workspace, storageRef)));
            paths.Add(Within(Path.Combine(BasePath, "_deleted"), BuildDeletedArchivePath(request, storageRef)));
        }

        static string Within(string root, string path) {
            var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(boundary, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("The stored thumbnail path is outside its storage directory.");
            return fullPath;
        }
    }
}
