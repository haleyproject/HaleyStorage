using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace Haley.Services {

    /// <summary>
    /// Partial class — CRUD operations (Upload, Download, Delete, Exists, GetSize, directory stubs).
    /// Handles staging vs direct-save routing and delegates byte I/O to the resolved <see cref="IStorageProvider"/>.
    /// </summary>
    public partial class StorageCoordinator : IStorageCoordinator {

        // ─── Upload ───────────────────────────────────────────────────────────

        /// <summary>
        /// Uploads a file from <see cref="IVaultFileWriteRequest.FileStream"/> to the resolved storage provider.
        /// When the module's <see cref="VaultProfileMode"/> is not <c>DirectSave</c> and a staging provider
        /// is configured, the file is written to staging first with <c>flags=4</c> (InStaging);
        /// direct-save sets <c>flags=8|64</c> (InStorage|Completed) immediately.
        /// </summary>
        /// <param name="input">Write request including scope (client/module/workspace), file stream, and conflict resolve mode.</param>
        /// <returns>An <see cref="IVaultResponse"/> indicating success, size, and DB indexer update status.</returns>
        public async Task<IVaultResponse> Upload(IVaultFileWriteRequest input) {
            var result = new VaultResponse() { Status = false, OriginalName = input?.OriginalName };
            try {
                if (input == null) { result.Message = "Input cannot be empty or null."; return result; }
                var rootCuid = (input.File as StorageFileRoute)?.RootCuid;
                var access = await CheckTargetWriteAccessAsync(input, input.File?.Id, input.File?.Cuid, rootCuid);
                if (!access.Status) { result.Message = access.Message; return result; }

                input.GenerateCallId();

                var gPaths = ProcessAndBuildStoragePath(input, true);

                if (string.IsNullOrWhiteSpace(input.OverrideRef)) {
                    result.Message = "Unable to generate the final storage path. Please check inputs.";
                    return result;
                }
                if (input.OverrideRef == gPaths.basePath)
                    throw new ArgumentException("No file save name is processed.");
                if (input.FileStream == null)
                    throw new ArgumentException("File stream is null. Nothing to save.");

                // ── Staging vs direct-save decision ───────────────────────────────
                // Resolution: workspace override → module → DirectSave default.
                var profileMode = ResolveProfileMode(input);
                var stagingProvider = ResolveStagingProvider(input);
                bool useStaging = profileMode != VaultProfileMode.DirectSave && stagingProvider != null
                                  && input.File != null && !string.IsNullOrWhiteSpace(input.File.StorageName);

                IStorageProvider writeProvider;
                string writePath;

                if (useStaging) {
                    // Build a key for the staging provider using the logical ID extracted from StorageName.
                    var logicalId = Path.GetFileNameWithoutExtension(input.File.StorageName);
                    var ext = Path.GetExtension(input.File.StorageName);
                    var stagingKey = stagingProvider.BuildStorageRef(logicalId, ext, SplitProvider, Config.SuffixFile);
                    writeProvider = stagingProvider;
                    writePath = stagingKey;

                    // Annotate the file route so the DB update records staging_path and flags.
                    if (input.File is StorageFileRoute sfr) {
                        sfr.StagingRef = stagingKey;
                        sfr.Flags = (int)VersionFlags.InStaging;
                        // Clear the primary storage ref: storage_path stays null in DB until promoted.
                        sfr.StorageRef = string.Empty;
                    }
                } else {
                    // DirectSave — write to primary provider and mark as complete immediately.
                    writeProvider = ResolveProvider(input);
                    writePath = input.OverrideRef;
                    if (input.File is StorageFileRoute sfr)
                        sfr.Flags = (int)(VersionFlags.InStorage | VersionFlags.Completed);
                }

                // Security: for FS, ensure target path stays within the storage root.
                if (writeProvider is FileSystemStorageProvider && !writePath.StartsWith(BasePath)) {
                    result.Message = "Not authorized for this path. Please check the inputs.";
                    return result;
                }

                if (input.BufferSize < (1024 * 80)) input.BufferSize = (1024 * 80);

                var writeResult = await writeProvider.WriteAsync(writePath, input.FileStream, input.BufferSize, input.WriteConflictMode);

                result.Status = writeResult.Success;
                result.PhysicalObjectExists = writeResult.AlreadyExisted;
                result.Message = writeResult.Message;

                if (input.File != null) result.SetResult(input.File);
                if (input.File is StorageFileRoute sfrUp) {
                    if (sfrUp.Actor == 0 && input.Actor.HasValue) sfrUp.Actor = input.Actor.Value;
                    result.VersionCuid = sfrUp.Cuid;
                    result.RootCuid = sfrUp.RootCuid;
                }
                if (result.Status) {
                    result.Size = input.FileStream.Length;
                    result.SizeHR = result.Size.ToFileSize(false);
                }
            } catch (Exception ex) {
                result.Message = ex.Message + Environment.NewLine + ex.StackTrace;
                result.Status = false;
            } finally {
                IFeedback upInfo = null;
                if (WriteMode && Indexer != null && input != null && input.Scope?.Module != null) {
                    if (input.File != null && result.Status) {
                        upInfo = await Indexer.UpdateDocVersionInfo(input.Scope.Module.Cuid.ToString("N"), input.File, input.CallID);
                    }
                    if (Indexer is MariaDBIndexing mdIdx) {
                        mdIdx.FinalizeTransaction(input.CallID, result.Status && !(upInfo == null || upInfo.Status == false));
                    }
                    if (upInfo?.Status != true)
                        _logger?.LogError($"Document version update FAILED: {upInfo?.Message}");
                    else
                        _logger?.LogDebug($"Document version update: {upInfo?.Status} — {upInfo?.Result}");
                }
            }
            return result;
        }

        // ─── Download ─────────────────────────────────────────────────────────

        /// <summary>
        /// Downloads a file for the given read request.
        /// If the file is still in staging (flags bit 4 set, bit 8 not set), the staging provider is
        /// queried first. Cloud staging providers may return a pre-signed <see cref="IVaultStreamResponse.AccessUrl"/>;
        /// callers should redirect the HTTP response rather than stream bytes when that URL is non-null.
        /// </summary>
        /// <param name="input">Read request that identifies the client/module/workspace and the specific file.</param>
        /// <param name="auto_search_extension">When <c>true</c>, scans the target directory for a matching file if no extension is specified.</param>
        public async Task<IVaultStreamResponse> Download(IVaultFileReadRequest input, bool auto_search_extension = true) {
            var result = new VaultStreamResponse() { Status = false, Stream = Stream.Null };

            var path = ProcessAndBuildStoragePath(input, true).targetPath;
            if (string.IsNullOrWhiteSpace(path)) return result;

            var comparison = StringComparison.OrdinalIgnoreCase;

            // ── Staging-aware read ─────────────────────────────────────────────
            // If the file is still in staging (InStaging bit set, InStorage bit clear),
            // read from the staging provider. Cloud staging providers return a pre-signed
            // redirect URL via GetAccessUrl; FS staging falls back to streaming.
            if (input.File is StorageFileRoute sfr && (sfr.Flags & 4) != 0 && (sfr.Flags & 8) == 0 && !string.IsNullOrWhiteSpace(sfr.StagingRef)) {

                var stagingProvider = ResolveStagingProvider(input);
                if (stagingProvider != null) {
                    // Try a pre-signed URL redirect first (zero-bytes-through-server for cloud).
                    var accessUrl = await stagingProvider.GetAccessUrl(sfr.StagingRef, TimeSpan.FromHours(1));
                    if (!string.IsNullOrWhiteSpace(accessUrl)) {
                        result.Status = true;
                        result.AccessUrl = accessUrl;
                        result.Stream = Stream.Null;
                        result.SaveName = input.File.StorageName;
                        return result;
                    }

                    // No redirect — stream bytes from staging.
                    var stagingRead = await stagingProvider.ReadAsync(sfr.StagingRef, auto_search_extension, comparison);
                    if (!stagingRead.Success) { result.Message = stagingRead.Message; return result; }
                    result.SaveName = !string.IsNullOrWhiteSpace(input.File?.DisplayName)
                        ? input.File.DisplayName
                        : input.File?.StorageName;
                    result.Status = true;
                    result.Extension = stagingRead.Extension;
                    result.Stream = stagingRead.Stream;
                    return result;
                }
            }

            // ── Normal read from primary provider ─────────────────────────────
            var readResult = await ResolveProvider(input).ReadAsync(path, auto_search_extension, comparison);

            if (!readResult.Success) { result.Message = readResult.Message; return result; }

            // Prefer the human-readable display name (from doc_info) when available.
            // Fall back to the internal storage name so the download always has a filename.
            result.SaveName = !string.IsNullOrWhiteSpace(input.File?.DisplayName)
                ? input.File.DisplayName
                : input.File?.StorageName;
            result.Status = true;
            result.Extension = readResult.Extension;
            result.Stream = readResult.Stream;
            return result;
        }

        /// <summary>
        /// Downloads a file directly from an <see cref="IVaultFileRoute"/> without a full scope context.
        /// Uses the default provider and combines <see cref="StorageCoordinator.BasePath"/> with
        /// <see cref="IVaultRoute.StorageRef"/> when the ref is not already rooted.
        /// </summary>
        public async Task<IVaultStreamResponse> Download(IVaultFileRoute input, bool auto_search_extension = true) {
            var result = new VaultStreamResponse() { Status = false, Stream = Stream.Null };

            if (input == null || string.IsNullOrWhiteSpace(input.StorageRef)) {
                result.Message = "File route path is empty.";
                return result;
            }

            // input.StorageRef is the relative storage reference. Combine with BasePath for FS provider.
            // For cloud providers the coordinator's default is used since there is no request scope.
            var path = Path.IsPathRooted(input.StorageRef) ? input.StorageRef : Path.Combine(BasePath, input.StorageRef);

            var readResult = await GetDefaultProvider().ReadAsync(path, auto_search_extension);

            if (!readResult.Success) { result.Message = readResult.Message; return result; }

            result.SaveName = input.StorageName;
            result.Status = true;
            result.Extension = readResult.Extension;
            result.Stream = readResult.Stream;
            return result;
        }

        // ─── Delete ───────────────────────────────────────────────────────────

        /// <summary>
        /// Soft-deletes either a specific version (<c>uid</c>) or an entire logical document (<c>ruid</c>).
        /// Version delete hides only that content version and any thumbnails attached to it.
        /// Document delete hides the whole logical document. Physical archive movement happens later
        /// only when a same-name reupload needs to free the active name slot.
        /// </summary>
        public async Task<IFeedback> Delete(IVaultFileReadRequest input, bool hardDelete = false) {
            var feedback = new Feedback() { Status = false };
            if (input == null) { feedback.Message = "Input cannot be empty or null."; return feedback; }
            if (Indexer == null) { feedback.Message = "Delete requires an indexer."; return feedback; }
            if (input?.Scope?.Workspace == null) { feedback.Message = "Workspace information is required."; return feedback; }

            var rootCuid = (input.File as StorageFileRoute)?.RootCuid;
            var access = await CheckTargetWriteAccessAsync(input, input.File?.Id, input.File?.Cuid, rootCuid);
            if (!access.Status) { feedback.Message = access.Message; return feedback; }

            input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));

            if (!string.IsNullOrWhiteSpace(input?.File?.Cuid)) {
                var deleteResult = await Indexer.SoftDeleteVersion(input);
                feedback.Status = deleteResult?.Status == true;
                feedback.Message = deleteResult?.Message ?? "Unable to delete the version.";
                if (feedback.Status && hardDelete && deleteResult?.Result != null) {
                    await EnsureWorkspaceContextAsync(input, forceRefresh: true);
                    await ArchiveDeletedVersionChain(input, deleteResult.Result, input.File.Cuid);
                }
                return feedback;
            }

            var docDeleteResult = await Indexer.SoftDeleteDocument(input);
            feedback.Status = docDeleteResult?.Status == true;
            feedback.Message = docDeleteResult?.Message ?? "Unable to delete the document.";
            if (feedback.Status && hardDelete && docDeleteResult?.Result != null) {
                await EnsureWorkspaceContextAsync(input, forceRefresh: true);
                await MoveDeletedDocumentFilesToArchive(input, docDeleteResult.Result);
                var tombstoneFileName = BuildDeletedTombstoneFileName(docDeleteResult.Result);
                var finalize = await Indexer.FinalizeDeletedDocumentArchive(input.Scope.Module.Cuid.ToString("N"), docDeleteResult.Result.DocumentId, tombstoneFileName);
                if (finalize?.Status != true) {
                    feedback.Status = false;
                    feedback.Message = finalize?.Message ?? "Unable to finalize deleted document archive metadata.";
                }
            }
            return feedback;
        }

        /// <summary>
        /// Restores either a soft-deleted version (<c>uid</c>) or a soft-deleted logical document (<c>ruid</c>).
        /// FileSystem-backed archived bytes are moved back before the DB flags are cleared.
        /// </summary>
        public async Task<IFeedback> Restore(IVaultFileReadRequest input, bool force = false) {
            var feedback = new Feedback() { Status = false };
            if (input == null) { feedback.Message = "Input cannot be empty or null."; return feedback; }
            if (Indexer == null) { feedback.Message = "Restore requires an indexer."; return feedback; }
            if (input?.Scope?.Workspace == null) { feedback.Message = "Workspace information is required."; return feedback; }

            var rootCuid = (input.File as StorageFileRoute)?.RootCuid;
            var access = await CheckTargetWriteAccessAsync(input, input.File?.Id, input.File?.Cuid, rootCuid);
            if (!access.Status) { feedback.Message = access.Message; return feedback; }

            input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));
            await EnsureWorkspaceContextAsync(input, forceRefresh: true);

            if (!string.IsNullOrWhiteSpace(input?.File?.Cuid)) {
                var deletedVersion = await Indexer.GetDeletedVersion(input);
                if (deletedVersion?.Status != true || deletedVersion.Result == null) {
                    feedback.Message = deletedVersion?.Message ?? "Deleted version not found.";
                    return feedback;
                }

                var versionReadiness = await EnsureDeletedVersionFilesAvailableForRestore(input, deletedVersion.Result, input.File.Cuid);
                if (!versionReadiness.Status) return versionReadiness;

                var target = deletedVersion.Result.Versions.FirstOrDefault(v => SameCuid(v.VersionCuid, input.File.Cuid));
                if (target == null) {
                    feedback.Message = "Deleted version not found.";
                    return feedback;
                }

                var restoreVersionResult = await Indexer.RestoreDeletedVersion(
                    input.Scope.Module.Cuid.ToString("N"),
                    deletedVersion.Result.DocumentId,
                    target.VersionId,
                    target.VersionNumber,
                    target.SubVersionNumber,
                    force);

                feedback.Status = restoreVersionResult?.Status == true;
                feedback.Message = restoreVersionResult?.Message ?? "Unable to restore the version.";
                return feedback;
            }

            IFeedback<DeletedDocumentInfo> documentInfo = await Indexer.GetDeletedDocument(input);
            if ((documentInfo?.Status != true || documentInfo.Result == null) && force && Indexer is MariaDBIndexing mdIndexer) {
                documentInfo = await mdIndexer.GetDocumentLifecycleForRestore(input);
            }

            if (documentInfo?.Status != true || documentInfo.Result == null) {
                feedback.Message = documentInfo?.Message ?? "Deleted document not found.";
                return feedback;
            }

            var readiness = await EnsureDeletedDocumentFilesAvailableForRestore(input, documentInfo.Result);
            if (!readiness.Status) return readiness;

            var restoreResult = await Indexer.RestoreDeletedDocument(input.Scope.Module.Cuid.ToString("N"), documentInfo.Result.DocumentId, force);
            feedback.Status = restoreResult?.Status == true;
            feedback.Message = restoreResult?.Message ?? "Unable to restore the document.";
            return feedback;
        }

        // ─── Exists / GetSize ─────────────────────────────────────────────────

        /// <summary>
        /// Checks whether a file or directory exists at the resolved path.
        /// </summary>
        /// <param name="isFilePath">
        /// <c>true</c> to check for a file via the resolved <see cref="IStorageProvider"/>;
        /// <c>false</c> to check for a physical directory (FS only).
        /// </param>
        public IFeedback Exists(IVaultReadRequest input, bool isFilePath = false) {
            var feedback = new Feedback() { Status = false };
            var path = ProcessAndBuildStoragePath(input, isFilePath).targetPath;
            if (string.IsNullOrWhiteSpace(path)) {
                feedback.Message = "Unable to generate path from provided inputs.";
                return feedback;
            }

            // For files: ask the provider.
            // For directories: virtual dirs are in DB — physical check only applies to FS provider.
            // Cloud providers cannot determine virtual folder existence without an indexer query (not yet implemented).
            feedback.Status = isFilePath? ResolveProvider(input).Exists(path) : (ResolveProvider(input) is FileSystemStorageProvider && Directory.Exists(path));
            if (!feedback.Status) feedback.Message = $"Does not exist: {path}";
            return feedback;
        }

        /// <summary>Returns the byte size of the file at the resolved path, or 0 if it does not exist.</summary>
        public long GetSize(IVaultReadRequest input) {
            var path = ProcessAndBuildStoragePath(input, true).targetPath;
            if (string.IsNullOrWhiteSpace(path)) return 0;
            return ResolveProvider(input).GetSize(path);
        }

        // ─── GetParent ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the display name of the parent directory for the given file.
        /// Requires a non-empty workspace CUID and file CUID; delegates to <see cref="IVaultIndexing"/>.
        /// </summary>
        public async Task<IFeedback<string>> GetParent(IVaultFileReadRequest input) {
            input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));
            if (Indexer != null) await Indexer.DemandDirectoryAccess(input);
            return await Indexer?.GetParentName(input);
        }

        // ─── Directory operations (virtual — pending MariaDB phase) ───────────

        /// <summary>
        /// Returns directory metadata. Currently a stub — returns a failure response until
        /// the MariaDB directory-query phase is implemented.
        /// </summary>
        public Task<IVaultDirResponse> GetDirectoryInfo(IVaultReadRequest input) {
            // TODO: virtual directories live in MariaDB — query Indexer.GetDirectoryInfo once available.
            return Task.FromResult<IVaultDirResponse>(new VaultDirResponse() { Status = false, Message = "GetDirectoryInfo requires indexer implementation (pending MariaDB phase)." });
        }

        /// <summary>
        /// Creates a virtual directory (folder) in MariaDB under the workspace identified by <paramref name="input"/>.
        /// The parent folder is taken from <c>input.Scope.Folder</c> (Id=0 means root).
        /// Folders are DB-only — no physical directory is created on disk.
        /// Returns an error when no indexer is configured or the coordinator is in read-only mode.
        /// </summary>
        public async Task<IVaultResponse> CreateDirectory(IVaultReadRequest input, string rawname) {
            var result = new VaultResponse { Status = false, OriginalName = rawname };
            if (Indexer == null) { result.Message = "No indexer is configured. Directory creation requires a DB indexer."; return result; }
            var access = CheckWriteAccess(input);
            if (!access.Status) { result.Message = access.Message; return result; }
            if (string.IsNullOrWhiteSpace(rawname)) { result.Message = "Folder name cannot be empty."; return result; }

            var fb = await Indexer.RegisterDirectory(input, rawname);
            result.Status = fb.Status;
            result.Message = fb.Status ? $"Folder '{rawname}' created (id={fb.Result.id})." : fb.Message;
            if (fb.Status) result.SetResult(fb.Result.cuid);
            return result;
        }

        /// <summary>
        /// Soft-deletes a virtual directory in MariaDB.
        /// </summary>
        public async Task<IFeedback> DeleteDirectory(IVaultReadRequest input, bool recursive) {
            var feedback = new Feedback() { Status = false };
            if (Indexer == null) { feedback.Message = "DeleteDirectory requires an indexer."; return feedback; }
            if (input?.Scope?.Workspace == null) { feedback.Message = "Workspace information is required."; return feedback; }
            var access = CheckWriteAccess(input);
            if (!access.Status) { feedback.Message = access.Message; return feedback; }

            input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));
            return await Indexer.SoftDeleteDirectory(input, recursive);
        }

        /// <summary>
        /// Restores a virtual directory. With <paramref name="force"/> enabled, recursively restores
        /// child directories and all restorable documents/versions under the subtree.
        /// </summary>
        public async Task<IFeedback> RestoreDirectory(IVaultReadRequest input, bool force = false) {
            var feedback = new Feedback() { Status = false };
            if (Indexer == null) { feedback.Message = "RestoreDirectory requires an indexer."; return feedback; }
            if (input?.Scope?.Workspace == null) { feedback.Message = "Workspace information is required."; return feedback; }
            var access = CheckWriteAccess(input);
            if (!access.Status) { feedback.Message = access.Message; return feedback; }

            input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));
            await EnsureWorkspaceContextAsync(input, forceRefresh: true);

            if (force) {
                if (Indexer is not MariaDBIndexing mdIndexer) {
                    feedback.Message = "Force restore requires a MariaDB-backed indexer.";
                    return feedback;
                }

                var plan = await mdIndexer.GetRestorableDocumentsForDirectory(input);
                if (plan?.Status != true) {
                    feedback.Message = plan?.Message ?? "Unable to prepare the directory restore plan.";
                    return feedback;
                }

                foreach (var document in plan.Result ?? Enumerable.Empty<DeletedDocumentInfo>()) {
                    var readiness = await EnsureDeletedDocumentFilesAvailableForRestore(input, document);
                    if (!readiness.Status) return readiness;
                }
            }

            return await Indexer.RestoreDirectory(input, force);
        }

        // ─── Revision backup helper ───────────────────────────────────────────

    }
}

