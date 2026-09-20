using Haley.Abstractions;
using Haley.Enums;
using Haley.Utils;
using Haley.Models;
using System;
using System.Threading.Tasks;

namespace Haley.Services {
    /// <summary>
    /// Partial class — version-level and document-level metadata read/write operations.
    /// Enforces <see cref="IVaultRegistryConfig.AllowMetadataOnOldVersions"/> on set operations.
    /// </summary>
    public partial class StorageCoordinator : IStorageCoordinator {

        /// <inheritdoc/>
        public async Task<IFeedback<string>> GetVersionMetadata(IVaultReadRequest request, string versionCuid) {
            var fb = new Feedback<string>();
            try {
                if (request == null) return fb.SetMessage("Request cannot be null.");
                if (string.IsNullOrWhiteSpace(versionCuid)) return fb.SetMessage("Version CUID (uid) is required.");
                await Indexer.DemandDirectoryAccess(request, versionCuid: versionCuid);
                var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
                return await Indexer.GetVersionMetadata(moduleCuid, versionCuid);
            } catch (Exception ex) {
                fb.SetMessage(ex.Message);
                if (ThrowExceptions) throw;
                return fb;
            }
        }

        /// <inheritdoc/>
        public async Task<IFeedback> SetVersionMetadata(IVaultReadRequest request, string versionCuid, string metadata) {
            var fb = new Feedback();
            try {
                if (request == null) return fb.SetMessage("Request cannot be null.");
                if (string.IsNullOrWhiteSpace(versionCuid)) return fb.SetMessage("Version CUID (uid) is required.");
                var access = await CheckTargetWriteAccessAsync(request, versionCuid: versionCuid);
                if (!access.Status) return fb.SetMessage(access.Message);
                var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
                if (!Config.AllowMetadataOnOldVersions) {
                    var isLatest = await Indexer.IsLatestVersion(moduleCuid, versionCuid);
                    if (!isLatest)
                        return fb.SetMessage("Metadata can only be set on the latest version. Set AllowMetadataOnOldVersions = true to override.");
                }
                return await Indexer.SetVersionMetadata(moduleCuid, versionCuid, metadata);
            } catch (Exception ex) {
                fb.SetMessage(ex.Message);
                if (ThrowExceptions) throw;
                return fb;
            }
        }

        /// <inheritdoc/>
        public async Task<IFeedback<string>> GetDocumentMetadata(IVaultReadRequest request, string documentCuid) {
            var fb = new Feedback<string>();
            try {
                if (request == null) return fb.SetMessage("Request cannot be null.");
                if (string.IsNullOrWhiteSpace(documentCuid)) return fb.SetMessage("Document CUID (ruid) is required.");
                await Indexer.DemandDirectoryAccess(request, documentCuid: documentCuid);
                var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
                return await Indexer.GetDocumentMetadata(moduleCuid, documentCuid);
            } catch (Exception ex) {
                fb.SetMessage(ex.Message);
                if (ThrowExceptions) throw;
                return fb;
            }
        }

        /// <inheritdoc/>
        public async Task<IFeedback> SetDocumentMetadata(IVaultReadRequest request, string documentCuid, string metadata) {
            var fb = new Feedback();
            try {
                if (request == null) return fb.SetMessage("Request cannot be null.");
                if (string.IsNullOrWhiteSpace(documentCuid)) return fb.SetMessage("Document CUID (ruid) is required.");
                var access = await CheckTargetWriteAccessAsync(request, documentCuid: documentCuid);
                if (!access.Status) return fb.SetMessage(access.Message);
                var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);
                return await Indexer.SetDocumentMetadata(moduleCuid, documentCuid, metadata);
            } catch (Exception ex) {
                fb.SetMessage(ex.Message);
                if (ThrowExceptions) throw;
                return fb;
            }
        }
    }
}
