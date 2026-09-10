using Haley.Enums;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Haley.Models;

namespace Haley.Abstractions {
    public interface IStorageCoordinator : IFileStorageOperations, IVaultManagement, IFileFormatPolicy,IStorageProviderRegistry, IChunkedUploadOperations, IStorageDirectoryOperations {
        IStorageCoordinator SetConfig(IVaultRegistryConfig config);
        bool ThrowExceptions { get; }
        string GetStorageRoot();
        /// <summary>
        /// Resolves a request's provider-relative file reference into the provider path used for I/O.
        /// The returned base path and target path are not persistence values.
        /// </summary>
        (string basePath, string targetPath) ProcessAndBuildStoragePath(IVaultReadRequest input, bool allowRootAccess = false);
        bool WriteMode { get; }
        /// <summary>
        /// Evaluates the global and configured module/workspace write policy for a request.
        /// A successful response means mutation may proceed; reads are never restricted by this policy.
        /// </summary>
        IFeedback CheckWriteAccess(IVaultReadRequest request);
        /// <summary>Returns the configured and effective runtime write state for a module or workspace.</summary>
        StorageWritePolicyStatus GetWritePolicy(IVaultReadRequest request);
        /// <summary>
        /// Changes a module or workspace runtime write policy. A null value removes the explicit
        /// setting and restores inherited behavior. This does not alter startup configuration.
        /// </summary>
        StorageWritePolicyStatus SetWritePolicy(IVaultReadRequest request, bool? write);
        /// <summary>
        /// Evaluates write access against both the declared request scope and the workspace that owns
        /// an existing version or document. Use this before direct provider writes.
        /// </summary>
        Task<IFeedback> CheckTargetWriteAccessAsync(
            IVaultReadRequest request,
            long? versionId = null,
            string versionCuid = null,
            string documentCuid = null);
        // ── Provider / profile configuration ─────────────────────────────────
        /// <summary>
        /// Sets the runtime provider routing for a registered module without requiring a
        /// DB round-trip. Call this at startup (e.g. in Program.cs) after registration.
        /// <paramref name="storageProviderKey"/> — key of the primary provider (e.g. "FileSystem", "B2").
        /// <paramref name="stagingProviderKey"/> — key of the staging provider, or null for none.
        /// <paramref name="mode"/> — upload routing mode; defaults to DirectSave.
        /// Both provider keys must already be registered with <c>AddProvider</c>.
        /// Returns false if the module CUID is not found in the indexer cache.
        /// </summary>
        /// <summary>
        /// Returns the effective primary <see cref="IStorageProvider"/> for the given request scope.
        /// Applies the same resolution order as internal write/read paths:
        /// file profile_info_id → workspace override → module → global default.
        /// </summary>
        IStorageProvider GetPrimaryProvider(IVaultReadRequest request);

        /// <summary>
        /// Returns the staging <see cref="IStorageProvider"/> configured for the given request scope,
        /// or <c>null</c> when staging is not configured or is explicitly disabled.
        /// Applies the same resolution order as the internal write/read paths.
        /// </summary>
        IStorageProvider GetStagingProvider(IVaultReadRequest request);

        bool ConfigureModuleProviders(string moduleCuid, string storageProviderKey, string stagingProviderKey = null, VaultProfileMode mode = VaultProfileMode.DirectSave, long profileInfoId = 0);

        /// <summary>
        /// Sets the runtime provider routing for a registered workspace, overriding the module-level
        /// profile for this workspace only. Pass null for <paramref name="stagingProviderKey"/> to
        /// disable staging at workspace level. Both keys must already be registered with <c>AddProvider</c>.
        /// Returns false if the workspace CUID is not found in the indexer cache.
        /// </summary>
        bool ConfigureWorkspaceProviders(string workspaceCuid, string storageProviderKey, string stagingProviderKey = null, VaultProfileMode mode = VaultProfileMode.DirectSave);

        // ── Placeholder / Background-Move ────────────────────────────────────

        /// <summary>
        /// Reserves a DB record (document + doc_version + version_info with flags=256 Placeholder)
        /// and returns the pre-computed target storage location so an external process can copy
        /// the file out-of-band (USB drop, server-side move, cloud-native copy, etc.).
        /// <para>
        /// For FileSystem providers the parent shard directory is created immediately,
        /// so the caller can start the copy without any extra setup.
        /// </para>
        /// <para>
        /// After the copy completes, call <see cref="FinalizePlaceholder"/> to mark the version
        /// as InStaging or InStorage|Completed and record the final size and hash.
        /// </para>
        /// </summary>
        /// <param name="request">Scope (client/module/workspace). File route is ignored.</param>
        /// <param name="fileName">File name including extension (e.g. <c>"archive.mp4"</c>).</param>
        /// <param name="displayName">Optional human-readable display name stored in doc_info.</param>
        Task<IFeedback<PlaceholderInfo>> CreatePlaceholder(IVaultReadRequest request, string fileName, string displayName = null);

        /// <summary>
        /// Resolves an existing placeholder directly by its version CUID. Unlike normal file details,
        /// this operation includes an incomplete version carrying the Placeholder flag.
        /// </summary>
        Task<IFeedback<VaultFileDetailsResponse>> GetPlaceholderDetails(IVaultFileReadRequest request);

        /// <summary>
        /// Marks a placeholder version as complete after the out-of-band copy lands.
        /// <list type="bullet">
        ///   <item>toStaging=false → sets flags = InStorage|Completed (8|64); updates storage_ref.</item>
        ///   <item>toStaging=true  → sets flags = InStaging (4); updates staging_ref.</item>
        /// </list>
        /// When <paramref name="size"/> is <c>null</c> and the provider is FileSystem and
        /// <paramref name="toStaging"/> is false, the file size is read directly from disk.
        /// </summary>
        /// <param name="request">Scope identifying the module DB (client/module/workspace).</param>
        /// <param name="versionId">The VersionId returned by <see cref="CreatePlaceholder"/>.</param>
        /// <param name="toStaging">True if the file was copied to staging rather than primary storage.</param>
        /// <param name="size">File size in bytes, or null to auto-detect (FS only).</param>
        /// <param name="hash">Optional SHA-256 hash of the copied file.</param>
        Task<IFeedback> FinalizePlaceholder(IVaultReadRequest request, long versionId, bool toStaging = false, long? size = null, string hash = null);

        // ── Filesystem revision backups ───────────────────────────────────────

        /// <summary>
        /// Lists all <c>##v{n}##</c> revision backup files that exist beside the live file
        /// identified by <paramref name="request"/>. Ordered newest-first.
        /// Only meaningful for <see cref="FileSystemStorageProvider"/>; returns an empty list
        /// for all other providers. No DB query — all data comes from the filesystem.
        /// </summary>
        Task<IFeedback<List<VaultRevisionInfo>>> GetRevisions(IVaultFileReadRequest request);

        /// <summary>
        /// Streams the bytes of a specific <c>##v{n}##</c> revision backup.
        /// <paramref name="version"/> must match a value returned by <see cref="GetRevisions"/>.
        /// </summary>
        Task<IVaultStreamResponse> DownloadRevision(IVaultFileReadRequest request, int version);

        // ── Metadata ─────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the metadata string stored on a specific version (uid).
        /// <paramref name="request"/> provides the module scope; <paramref name="versionCuid"/> is the uid.
        /// </summary>
        Task<IFeedback<string>> GetVersionMetadata(IVaultReadRequest request, string versionCuid);

        /// <summary>
        /// Sets or clears metadata on a version (uid).
        /// When <see cref="IVaultRegistryConfig.AllowMetadataOnOldVersions"/> is <c>false</c> (default),
        /// returns an error if the supplied version is not the latest for its document.
        /// Pass <c>null</c> or an empty string to clear the metadata.
        /// </summary>
        Task<IFeedback> SetVersionMetadata(IVaultReadRequest request, string versionCuid, string metadata);

        /// <summary>
        /// Gets the metadata string stored at the document level (ruid).
        /// <paramref name="request"/> provides the module scope; <paramref name="documentCuid"/> is the ruid.
        /// </summary>
        Task<IFeedback<string>> GetDocumentMetadata(IVaultReadRequest request, string documentCuid);

        /// <summary>
        /// Sets or clears document-level metadata (ruid). Pass <c>null</c> or empty to clear.
        /// <paramref name="request"/> provides the module scope; <paramref name="documentCuid"/> is the ruid.
        /// </summary>
        Task<IFeedback> SetDocumentMetadata(IVaultReadRequest request, string documentCuid, string metadata);

        // ── Thumbnail ─────────────────────────────────────────────────────────

        /// <summary>
        /// Downloads the thumbnail for the file identified by <paramref name="request"/> (uid or ruid).
        /// Returns a failed response when no thumbnail exists for that version.
        /// Upload is handled by the standard <see cref="Upload"/> path with
        /// <see cref="IVaultFileWriteRequest.IsThumbnail"/> set to <c>true</c>.
        /// </summary>
        Task<IVaultStreamResponse> DownloadThumbnail(IVaultFileReadRequest request);

        // ── Cached Stats / Move ──────────────────────────────────────────────

        /// <summary>
        /// Returns cached direct and recursive counts for a workspace or directory.
        /// Stats are derived from DB rows, not filesystem scans.
        /// </summary>
        Task<IFeedback<VaultStatsSnapshot>> GetStats(IVaultReadRequest input, string extension = null);

        /// <summary>
        /// Processes queued stat events for the module identified by the request scope.
        /// </summary>
        Task<IFeedback> ProcessStatsEvents(IVaultReadRequest input, int batchSize = 1000);

        /// <summary>
        /// Rebuilds cached stats from the module DB source-of-truth tables.
        /// </summary>
        Task<IFeedback> RebuildStats(IVaultReadRequest input, long? workspaceId = null);

        /// <summary>
        /// Moves a logical document to a new directory in the same module.
        /// </summary>
        Task<IFeedback<VaultMoveResult>> MoveFile(IVaultFileReadRequest source, IVaultReadRequest target, bool rename = false);

        /// <summary>
        /// Moves a virtual directory subtree to a new parent in the same module.
        /// </summary>
        Task<IFeedback<VaultMoveResult>> MoveDirectory(IVaultReadRequest source, IVaultReadRequest target, bool rename = false);
    }
}
