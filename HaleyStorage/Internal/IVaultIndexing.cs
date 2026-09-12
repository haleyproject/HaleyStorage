using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using Haley.Models;
using Haley.Enums;
using Haley.Abstractions;

namespace Haley.Services {
    /// <summary>
    /// Internal contract for DB-backed vault indexing.
    /// Consumers never implement or inject this — the implementation is wired up internally
    /// by <see cref="StorageCoordinator"/> based on the DB choice (currently MariaDB only).
    /// </summary>
    internal interface IVaultIndexing {
        bool ThrowExceptions { get; }
        Task<IFeedback> RegisterClient(IVaultClient info);
        Task<IFeedback> RegisterModule(IVaultModule info);
        Task<IFeedback> RegisterWorkspace(IVaultWorkSpace info);
        Task<(long id, Guid guid)> RegisterDocuments(IVaultReadRequest request, IVaultStorable holder);
        /// <summary>
        /// Creates a new content <c>doc_version</c> row (sub_ver=0, version = MAX content ver + 1)
        /// under the document that owns <paramref name="versionCuid"/>. Skips filename/directory
        /// lookup entirely — the CUID is the only identity signal required.
        /// Returns the new version's DB id, CUID, and content version number.
        /// </summary>
        Task<(long id, Guid guid, int version)> RegisterNewDocVersion(string moduleCuid, string versionCuid, long? actor = null, string callId = null);
        Task<IFeedback> UpdateDocVersionInfo(string moduleCuid, IVaultFileRoute file, string callId = null);
        Task<IFeedback> UpdateDocDisplayName(string moduleCuid, long versionId, string displayName);
        Task<IFeedback> GetDocVersionInfo(string moduleCuid, long id);
        Task<IFeedback> GetDocVersionInfo(string moduleCuid, string cuid);
        Task<IFeedback> GetDocVersionInfoByStorageName(string moduleCuid, string storageName);
        Task<long> GetTargetWorkspaceId(string moduleCuid, long? versionId = null, string versionCuid = null, string documentCuid = null);
        /// <summary>Resolves a document CUID (ruid) to the latest version's full info row.</summary>
        Task<IFeedback> GetDocVersionInfoByDocCuid(string moduleCuid, string documentCuid);
        Task<IFeedback> GetDocVersionInfo(string moduleCuid, string wsCuid, string file_name, string dir_name = VaultConstants.DEFAULT_NAME, long dir_parent_id = 0);
        Task<IFeedback> GetDocVersionInfo(string moduleCuid, long wsId, string file_name, string dir_name = VaultConstants.DEFAULT_NAME, long dir_parent_id = 0);
        Task<IFeedback<VaultFolderBrowseResponse>> BrowseFolder(IVaultReadRequest request, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true);
        Task<IFeedback<(long id, string cuid)>> RegisterDirectory(IVaultReadRequest request, string folderName);
        /// <summary>
        /// Searches for matching folders and files (latest version only) across the workspace.
        /// The term is matched against vault names (filename stems); extension is a separate filter.
        /// </summary>
        Task<IFeedback<VaultFolderBrowseResponse>> SearchItems(IVaultReadRequest request, string searchTerm, VaultSearchMode searchMode, string extension = null, bool recursive = false, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true);
        Task<IFeedback<VaultFileDetailsResponse>> GetFileDetails(IVaultFileReadRequest request);
        Task<IFeedback<DeletedDocumentInfo>> SoftDeleteVersion(IVaultFileReadRequest request);
        Task<IFeedback<DeletedDocumentInfo>> SoftDeleteDocument(IVaultFileReadRequest request);
        Task<IFeedback<DeletedDocumentInfo>> GetDeletedVersion(IVaultFileReadRequest request);
        Task<IFeedback<DeletedDocumentInfo>> GetDeletedDocument(IVaultFileReadRequest request);
        Task<IFeedback<DeletedDocumentInfo>> GetDeletedDocumentByName(IVaultReadRequest request, string fileName);
        Task<IFeedback> FinalizeDeletedDocumentArchive(string moduleCuid, long documentId, string tombstoneFileName);
        Task<IFeedback> FinalizeDeletedVersionArchive(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo);
        Task<IFeedback> RestoreDeletedVersion(string moduleCuid, long documentId, long versionId, int versionNo, int subVersionNo, bool force = false);
        Task<IFeedback> RestoreDeletedDocument(string moduleCuid, long documentId, bool force = false);
        Task<IFeedback> SoftDeleteDirectory(IVaultReadRequest request, bool recursive);
        Task<IFeedback> RestoreDirectory(IVaultReadRequest request, bool force);
        Task<IFeedback<VaultStatsSnapshot>> GetStats(IVaultReadRequest request, string extension = null);
        Task<IFeedback> ProcessStatsEvents(string moduleCuid, int batchSize = 1000);
        Task<IFeedback> RebuildStats(string moduleCuid, long? workspaceId = null);
        Task<IFeedback<VaultMoveResult>> MoveDocument(IVaultFileReadRequest source, IVaultReadRequest target, bool rename);
        Task<IFeedback<VaultMoveResult>> MoveDirectory(IVaultReadRequest source, IVaultReadRequest target, bool rename);
        Task EnsureValidation();
        bool TryGetComponentInfo<T>(string key, out T component) where T : IVaultObject;
        Task<bool> HydrateModuleAsync(string moduleCuid);
        Task<bool> HydrateWorkspaceAsync(string workspaceCuid, bool forceRefresh = false);
        Task<bool> HydrateWorkspaceByIdAsync(long workspaceId, bool forceRefresh = false);
        bool IsModuleAdapterRegistered(string moduleCuid);
        Task<IReadOnlyList<string>> GetWorkspaceCuidsAsync(string moduleCuid);
        bool TryAddInfo(IVaultObject dirInfo, bool replace = false);
        IEnumerable<T> GetAllComponents<T>() where T : IVaultObject;
        Task<IFeedback<string>> GetParentName(IVaultFileReadRequest request);
        // ── Metadata ─────────────────────────────────────────────────────────
        /// <summary>Returns true if the given version CUID is the latest version of its document.</summary>
        Task<bool> IsLatestVersion(string moduleCuid, string versionCuid);
        Task<IFeedback<string>> GetVersionMetadata(string moduleCuid, string versionCuid);
        Task<IFeedback> SetVersionMetadata(string moduleCuid, string versionCuid, string metadata);
        Task<IFeedback<string>> GetDocumentMetadata(string moduleCuid, string documentCuid);
        Task<IFeedback> SetDocumentMetadata(string moduleCuid, string documentCuid, string metadata);
        // Chunking
        Task<IFeedback> UpsertChunkInfo(string moduleCuid, long versionId, long chunkSizeMb, int totalParts, string chunkFolderName, string chunkFolderPath, bool isCompleted = false, string callId = null);
        Task<IFeedback> UpsertChunkPart(string moduleCuid, long versionId, long partNumber, int sizeMb, string hash = null, string callId = null);
        Task<IFeedback> MarkChunkCompleted(string moduleCuid, long versionId, string callId = null);
        Task<IFeedback> AbortChunkVersion(string moduleCuid, long versionId, string callId = null);
        Task<IFeedback> CleanupChunkVersion(string moduleCuid, long versionId, bool removeIncompleteVersion, string callId = null);
        Task<IFeedback<ChunkUploadBrowseResponse>> ListActiveChunkUploads(string moduleCuid, int page = 1, int pageSize = 20);
        IFeedback BeginTransaction(string moduleCuid, string callId);
        // Storage profiles
        Task<long> UpsertProvider(string displayName, string description = null);
        Task<long> UpsertProfile(string displayName);
        Task<long> UpsertProfileInfo(int profileId, int version, int mode, string storageProviderKey, string stagingProviderKey, string metadataJson);
        Task<bool> SetModuleStorageProfile(string moduleCuid, int profileId);
        Task<bool> SetWorkspaceStorageProfile(string workspaceCuid, int profileInfoId);
        /// <summary>
        /// Walks all cached modules and restores any persisted storage-profile assignments from the DB.
        /// Call once at startup after module registrations are complete.
        /// </summary>
        Task RehydrateModuleProfilesAsync();
        /// <summary>
        /// Walks all cached workspaces and restores any persisted storage-profile overrides from the DB.
        /// Call once at startup after all registrations are complete.
        /// </summary>
        Task RehydrateWorkspaceProfilesAsync();
        /// <summary>
        /// Fetches the resolved provider keys and mode for a specific <c>profile_info.id</c>.
        /// Returns a dictionary with keys: <c>storage_provider_key</c>, <c>staging_provider_key</c>, <c>mode</c>.
        /// </summary>
        Task<IFeedback> GetProfileInfo(long profileInfoId);

        // ── Thumbnail ─────────────────────────────────────────────────────────

        /// <summary>Returns the <c>document.id</c> (parent) for a given <c>doc_version.id</c>. Returns 0 when not found.</summary>
        Task<long> GetDocumentIdByVersionId(string moduleCuid, long versionId);

        /// <summary>
        /// Inserts a new <c>doc_version</c> row with <c>sub_ver = MAX(sub_ver)+1</c> for the given
        /// (documentId, contentVer). Returns the new thumbnail version's DB id and CUID.
        /// </summary>
        Task<(long id, Guid guid)> RegisterThumbnailVersion(string moduleCuid, long documentId, int contentVer, long? actor = null, string callId = null);

        /// <summary>
        /// Fetches storage info for the latest thumbnail sub-version of a specific content version.
        /// Returns a failed <see cref="IFeedback"/> when no thumbnail exists.
        /// </summary>
        Task<IFeedback> GetLatestThumbInfo(string moduleCuid, long documentId, int contentVer);

        // ── Staging promotion ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the next batch of <see cref="StagedVersionRef"/> rows whose bytes are on a
        /// staging provider but have not yet been promoted to primary storage.
        /// Filter: <c>(flags &amp; 4) &gt; 0 AND (flags &amp; 8) = 0 AND synced_at IS NULL</c>.
        /// </summary>
        Task<IEnumerable<StagedVersionRef>> GetPendingStagedVersions(string moduleCuid, int batchSize = 20);

        /// <summary>
        /// Records the outcome of a successful promotion: writes the final <c>storage_ref</c>,
        /// updates <c>flags</c> (e.g. <c>8|64</c> for StageAndMove), and stamps <c>synced_at</c>.
        /// </summary>
        Task<IFeedback> UpdateVersionPromotion(string moduleCuid, long versionId, string storageRef, int newFlags, DateTime syncedAt, long size = 0, string hash = null);
    }
}
