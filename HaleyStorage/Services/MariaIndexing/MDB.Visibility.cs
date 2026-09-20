using Haley.Abstractions;
using Haley.Models;
using static Haley.Internal.IndexingQueries;
using static Haley.Internal.IndexingConstant;

namespace Haley.Utils;

internal partial class MariaDBIndexing {
    internal async Task<bool> IsHiddenDirectory(string moduleCuid, long directoryId, DbExecutionLoad load = default) =>
        directoryId > 0 && await _agw.ScalarAsync<bool>(moduleCuid, INSTANCE.VISIBILITY.IS_HIDDEN, load, (ID, directoryId));

    public async Task<bool> IsHiddenVersion(string moduleCuid, long versionId) {
        var directoryId = await FileDirectory(moduleCuid, versionId, null, null);
        return await IsHiddenDirectory(moduleCuid, directoryId);
    }

    async Task<long> FileDirectory(string moduleCuid, long? versionId, string versionCuid, string documentCuid) =>
        await _agw.ScalarAsync<long?>(moduleCuid, INSTANCE.VISIBILITY.FILE_DIRECTORY, default,
            ("@version_id", versionId.HasValue ? versionId.Value : DBNull.Value),
            ("@version_cuid", string.IsNullOrWhiteSpace(versionCuid) ? DBNull.Value : ToDbCuid(versionCuid)),
            ("@document_cuid", string.IsNullOrWhiteSpace(documentCuid) ? DBNull.Value : ToDbCuid(documentCuid))) ?? 0;

    public async Task DemandDirectoryAccess(IVaultReadRequest request, long? versionId = null, string versionCuid = null, string documentCuid = null) {
        if (request == null || request.AllowHiddenDirectories) return;
        var folder = request.Scope?.Folder;
        if (DirectoryVisibility.IsHiddenName(folder?.DisplayName))
            throw new InvalidOperationException("The requested folder is not available.");
        var module = request.Scope?.Module?.Cuid.ToString("N");
        if (string.IsNullOrWhiteSpace(module) || !_agw.ContainsKey(module)) return;
        long directoryId = folder?.Id ?? 0;
        if (directoryId < 1 && !string.IsNullOrWhiteSpace(folder?.Cuid))
            directoryId = await _agw.ScalarAsync<long?>(module, INSTANCE.DIRECTORY.GET_BY_CUID_ALL, default, (CUID, ToDbCuid(folder.Cuid))) ?? 0;
        if (directoryId < 1 && folder?.Parent?.Id > 0) directoryId = folder.Parent.Id;
        if (await IsHiddenDirectory(module, directoryId))
            throw new InvalidOperationException("The requested folder is not available.");
        var file = (request as IVaultFileReadRequest)?.File;
        versionId ??= file?.Id > 0 ? file.Id : null;
        versionCuid ??= file?.Cuid;
        documentCuid ??= (file as StorageFileRoute)?.RootCuid;
        // Filesystem processed-name reads reconstruct a path without populating file IDs.
        // Resolve their logical version identity before allowing that fast path.
        if (!versionId.HasValue && string.IsNullOrWhiteSpace(versionCuid) && string.IsNullOrWhiteSpace(documentCuid)
            && !string.IsNullOrWhiteSpace(file?.StorageName)) {
            var logicalName = System.IO.Path.GetFileNameWithoutExtension(file.StorageName.Trim());
            if (long.TryParse(logicalName, out var processedId) && processedId > 0) versionId = processedId;
            else if (Guid.TryParse(logicalName, out var processedCuid)) versionCuid = processedCuid.ToString("N");
        }
        if (versionId.HasValue || !string.IsNullOrWhiteSpace(versionCuid) || !string.IsNullOrWhiteSpace(documentCuid)) {
            directoryId = await FileDirectory(module, versionId, versionCuid, documentCuid);
            if (await IsHiddenDirectory(module, directoryId))
                throw new InvalidOperationException("The requested file is not available.");
        }
    }
}
