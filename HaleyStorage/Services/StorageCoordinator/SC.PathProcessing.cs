using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using static Haley.Internal.IndexingQueries;

namespace Haley.Services {
    /// <summary>
    /// Partial class — path resolution pipeline.
    /// Converts a <see cref="IVaultReadRequest"/> into the fully-qualified storage path (or object key)
    /// used by a provider. Steps: prepare context → resolve workspace base path → resolve file route.
    /// </summary>
    public partial class StorageCoordinator : IStorageCoordinator {
        ConcurrentDictionary<string, string> _pathCache = new ConcurrentDictionary<string, string>();
        ConcurrentDictionary<string, DateTime> _workspaceRegistryRefresh = new ConcurrentDictionary<string, DateTime>();
        ConcurrentDictionary<string, SemaphoreSlim> _workspaceRegistryRefreshLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
        static readonly TimeSpan WorkspaceRegistryRefreshInterval = TimeSpan.FromSeconds(5);

        // ─────────────────────────────────────────────────────────────────────
        // Public entry point
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the workspace base path and the complete target path for a request.
        /// Single responsibility: orchestrate the three steps below and return both paths.
        /// </summary>
        public (string basePath, string targetPath) ProcessAndBuildStoragePath(IVaultReadRequest input, bool allowRootAccess = false) {
            PrepareRequestContext(input);                          // 1. Normalise CUID, mark virtual folder
            ResolveTargetWorkspaceContextAsync(input).GetAwaiter().GetResult();
            EnsureWorkspaceContextAsync(input).GetAwaiter().GetResult();
            var provider = ResolveProvider(input);                 // 2. Resolve provider once for all steps
            var bpath = FetchWorkspaceBasePath(input, provider);   // 3. Resolve workspace base path (cached)
            if (input is IVaultFileReadRequest fileRead)
                ProcessFileRoute(fileRead, provider).Wait();       // 4. Resolve file path (may query/register indexer)

            // 5. Provider joins base path + file ref using its own separator and validation rules.
            var fileRef = (input is IVaultFileReadRequest fr && fr.File != null)
                ? fr.File.StorageRef ?? string.Empty
                : string.Empty;
            var path = provider.BuildFullPath(bpath, fileRef);
            if (input is StorageReadRequest req) req.OverrideRef = path;
            return (bpath, path);
        }

        /// <summary>Returns the root storage directory used by the FileSystem provider.</summary>
        public string GetStorageRoot() => BasePath;

        /// <summary>
        /// Returns the sharding parameters (split length and depth) for a given identifier type.
        /// Numeric IDs use <see cref="IVaultRegistryConfig.SplitLengthNumber"/> /
        /// <see cref="IVaultRegistryConfig.DepthNumber"/>; hash/GUID IDs use the hash variants.
        /// </summary>
        /// <param name="isNumber"><c>true</c> for auto-increment IDs; <c>false</c> for compact-N GUIDs.</param>
        public (int length, int depth) SplitProvider(bool isNumber) {
            if (isNumber) return (Config.SplitLengthNumber, Config.DepthNumber);
            return (Config.SplitLengthHash, Config.DepthHash);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 1 — Request context preparation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Normalises the request before path resolution begins.
        /// - Workspace CUID is always derived deterministically from names (not trusted from caller).
        /// - Folders in a managed workspace are marked virtual (DB-only, no physical directory).
        /// </summary>
        void PrepareRequestContext(IVaultReadRequest input) {
            // Workspace CUID is always re-derived deterministically from names.
            input.Scope?.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));
            // All folders are virtual (DB-only). No physical directory marking needed.
        }

        /// <summary>
        /// Rebinds an ID-based file request to the workspace persisted on its document. A uid/ruid is
        /// authoritative; a missing or stale caller workspace must not redirect the physical read.
        /// </summary>
        async Task ResolveTargetWorkspaceContextAsync(IVaultReadRequest input) {
            if (Indexer == null || input is not IVaultFileReadRequest fileRequest || fileRequest.File == null)
                return;

            var file = fileRequest.File;
            var rootCuid = (file as StorageFileRoute)?.RootCuid;
            if (file.Id < 1 && string.IsNullOrWhiteSpace(file.Cuid) && string.IsNullOrWhiteSpace(rootCuid))
                return;

            var moduleCuid = input.Scope?.Module?.Cuid.ToString("N");
            if (string.IsNullOrWhiteSpace(moduleCuid)) return;

            var workspaceId = await Indexer.GetTargetWorkspaceId(
                moduleCuid,
                file.Id > 0 ? file.Id : null,
                file.Cuid,
                rootCuid);
            if (workspaceId < 1) return;

            var workspace = Indexer.GetAllComponents<VaultWorkSpace>()
                .FirstOrDefault(candidate => candidate.Id == workspaceId);
            if (workspace == null) {
                await Indexer.HydrateWorkspaceByIdAsync(workspaceId);
                workspace = Indexer.GetAllComponents<VaultWorkSpace>()
                    .FirstOrDefault(candidate => candidate.Id == workspaceId);
            }

            if (workspace != null)
                input.Scope.Workspace = workspace;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 2 — Workspace base path resolution
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the fully-qualified base path (or key prefix) for the workspace.
        /// Results are cached by workspace CUID after the first resolution.
        /// For the FileSystem provider the resolved path is verified to exist on disk;
        /// cloud providers use the same string as an object-key prefix — no directory check.
        /// </summary>
        string FetchWorkspaceBasePath(IVaultReadRequest request, IStorageProvider provider = null, bool ignoreCache = false) {
            Initialize().Wait(); // Ensures default structures exist. No-op in ReadOnly mode.
            provider ??= ResolveProvider(request);
            string result;

            if (!ignoreCache
                && _pathCache.TryGetValue(request.Scope.Workspace.Cuid.ToString("N"), out var cached)
                && !string.IsNullOrWhiteSpace(cached)) {
                result = cached;
            } else {
                var paths = new List<string> { BasePath };
                AddComponentPath(request, paths, provider);
                result = provider is FileSystemStorageProvider
                    ? Path.Combine(paths.ToArray())
                    : string.Join("/", paths.Select(p => p.Trim('/', '\\')));
            }

            _logger?.LogDebug($"Workspace base path: {result}");

            // Directory existence is only meaningful for the FileSystem provider.
            // Cloud providers use object keys — no directories to verify.
            if (provider is FileSystemStorageProvider && !IsVirtualWorkspace(request) && !Directory.Exists(result)) {
                throw new DirectoryNotFoundException(
                    $"Workspace base path '{result}' does not exist. " +
                    $"Ensure the client/module/workspace are registered and the storage root is accessible.");
            }

            return result;
        }

        /// <summary>
        /// Guards against uploading a file whose extension differs from the one already stored for
        /// this document. Called before replace and create-new-version paths when a CUID is supplied.
        /// Throws <see cref="ArgumentException"/> on mismatch; silently passes when the stored version
        /// has no storage_ref yet (placeholder) or when either extension is absent.
        /// </summary>
        async Task CheckCuidExtensionConsistency(IVaultFileReadRequest input, IVaultFileWriteRequest inputW, bool forupload) {
            if (!forupload || string.IsNullOrWhiteSpace(input.File?.Cuid)) return; // only check when targeting a specific version

            var incomingExt = Path.GetExtension(inputW?.OriginalName ?? string.Empty)?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(incomingExt)) return; // nothing to compare

            var moduleCuid = input.Scope.Module.Cuid.ToString("N");
            var existing = await Indexer.GetDocVersionInfo(moduleCuid, input.File.Cuid);
            if (existing?.Status != true || existing.Result is not Dictionary<string, object> dic) return; // can't determine — let it through

            // Prefer storage_ref; fall back to staging_ref (set when file is still in staging).
            var storagePath = dic.TryGetValue("path", out var p) ? p?.ToString() : null;
            var stagingPath = dic.TryGetValue("staging_path", out var sp) ? sp?.ToString() : null;
            var refPath = !string.IsNullOrWhiteSpace(storagePath) ? storagePath : stagingPath;
            if (string.IsNullOrWhiteSpace(refPath)) return; // placeholder — no stored bytes yet

            var storedExt = Path.GetExtension(refPath)?.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(storedExt)) return;

            if (!string.Equals(incomingExt, storedExt, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Extension mismatch: the document is stored as '{storedExt}' but the incoming file is '{incomingExt}'. " +
                    $"Upload without a uid to create a new document.");
        }

        /// <summary>
        /// When a request carries only a document-level CUID (ruid), resolve it to the latest content
        /// version CUID before the normal version-based upload flow runs. This keeps upload semantics
        /// consistent: extension checks, replace, new-version creation, and thumbnails all operate on
        /// a concrete content version uid.
        /// </summary>
        async Task ResolveVersionCuidFromRootCuid(IVaultFileReadRequest input, bool forupload) {
            if (input?.File is not StorageFileRoute sfr
                || string.IsNullOrWhiteSpace(sfr.RootCuid)
                || !string.IsNullOrWhiteSpace(sfr.Cuid))
                return;

            var moduleCuid = input.Scope.Module.Cuid.ToString("N");
            var existing = await Indexer.GetDocVersionInfoByDocCuid(moduleCuid, sfr.RootCuid);
            if (existing?.Status != true || existing.Result is not Dictionary<string, object> dic || dic.Count < 1) {
                if (forupload)
                    throw new ArgumentException($"No file found for the supplied ruid '{sfr.RootCuid}'. Upload without a uid/ruid to create a new file.");
                throw new ArgumentException($"File not found for the supplied ruid '{sfr.RootCuid}'.");
            }

            var resolvedCuid = dic.TryGetValue("uid", out var uidObj) ? uidObj?.ToString() : null;
            if (string.IsNullOrWhiteSpace(resolvedCuid)) {
                if (forupload)
                    throw new ArgumentException($"Unable to resolve the latest content version uid for ruid '{sfr.RootCuid}'.");
                throw new ArgumentException($"Unable to resolve the latest content version for ruid '{sfr.RootCuid}'.");
            }

            input.File.SetCuid(resolvedCuid);
            if (string.IsNullOrWhiteSpace(sfr.RootCuid)
                && dic.TryGetValue("ruid", out var ruidObj)
                && !string.IsNullOrWhiteSpace(ruidObj?.ToString()))
                sfr.RootCuid = ruidObj.ToString();
        }

        async Task<bool> CreateNewDocumentVersion(IVaultFileReadRequest input, IVaultFileWriteRequest inputW, bool forupload, VaultWorkSpace wInfo, IStorageProvider provider = null) {
            if (!forupload || string.IsNullOrWhiteSpace(input.File?.Cuid)) return false; //only applicable when uploading a specific version (replace=false scenario, along with the presence of a CUID to identify the version to be replaced).
            if (inputW?.ReplaceExistingFile == true && inputW.IsThumbnail != true) return false; // Thumbnail uploads always create a new sub-version row; they never replace the content row.

            var moduleCuid = input.Scope.Module.Cuid.ToString("N");
            long newVersionId;
            Guid newVersionGuid;
            int newVersionNo = 0;

            if (inputW.IsThumbnail) {
                // Thumbnail path: resolve content version → document id + ver number → insert with sub_ver.
                var cvFb = await Indexer.GetDocVersionInfo(moduleCuid, input.File.Cuid);
                if (cvFb?.Status != true || cvFb.Result is not Dictionary<string, object> cvDic)
                    throw new ArgumentException($"Unable to resolve content version for CUID '{input.File.Cuid}'.");

                if (!long.TryParse(cvDic["id"]?.ToString(), out long contentVersionId) || contentVersionId < 1)
                    throw new ArgumentException("Unable to read content version id.");
                if (!int.TryParse(cvDic["ver"]?.ToString(), out int contentVer) || contentVer < 1)
                    throw new ArgumentException("Unable to read content version number.");

                var documentId = await Indexer.GetDocumentIdByVersionId(moduleCuid, contentVersionId);
                if (documentId < 1)
                    throw new ArgumentException($"Unable to resolve parent document id for version id {contentVersionId}.");

                (newVersionId, newVersionGuid) = await Indexer.RegisterThumbnailVersion(moduleCuid, documentId, contentVer, input.Actor, input.CallID);
                if (newVersionId < 1)
                    throw new ArgumentException($"Unable to register thumbnail version for document {documentId}, ver {contentVer}.");
                newVersionNo = contentVer;
            } else {
                // Content path: insert a new doc_version with sub_ver=0 (default).
                // Capture the old version cuid before it gets overwritten, so we can carry forward the document ruid.
                var oldVersionCuid = input.File.Cuid;
                (newVersionId, newVersionGuid, newVersionNo) = await Indexer.RegisterNewDocVersion(moduleCuid, oldVersionCuid, input.Actor, input.CallID);
                if (newVersionId < 1)
                    throw new ArgumentException($"Unable to create a new version for version CUID '{oldVersionCuid}'.");

                // Carry the document-level cuid (ruid) forward to the new version's file route.
                if (input.File is StorageFileRoute sfrOld && string.IsNullOrWhiteSpace(sfrOld.RootCuid)) {
                    var oldFb = await Indexer.GetDocVersionInfo(moduleCuid, oldVersionCuid);
                    if (oldFb?.Status == true && oldFb.Result is Dictionary<string, object> oldDic
                        && oldDic.TryGetValue("ruid", out var ruidObj) && !string.IsNullOrWhiteSpace(ruidObj?.ToString()))
                        sfrOld.RootCuid = ruidObj.ToString();
                }
            }

            var extension = Path.GetExtension(inputW.OriginalName ?? string.Empty);
            var logicalId = wInfo?.NameMode == VaultNameMode.Number
                ? newVersionId.ToString()
                : newVersionGuid.ToString("N");

            input.File.SetId(newVersionId);
            input.File.SetCuid(newVersionGuid);
            input.File.Version = newVersionNo;
            input.File.StorageName = logicalId;
            input.File.StorageRef = provider.BuildStorageRef(logicalId, extension, SplitProvider, Config.SuffixFile);
            if (inputW.FileStream != null) input.File.Size = inputW.FileStream.Length;

            var newVersionInfo = await Indexer.GetDocVersionInfo(moduleCuid, newVersionId);
            if (newVersionInfo?.Status == true && newVersionInfo.Result is Dictionary<string, object> newVersionDic) {
                if (input.File.Version < 1 && int.TryParse(newVersionDic["ver"]?.ToString(), out var newVerNo) && newVerNo > 0)
                    input.File.Version = newVerNo;
                if (input.File is StorageFileRoute sfrNewRoute
                    && string.IsNullOrWhiteSpace(sfrNewRoute.RootCuid)
                    && newVersionDic.TryGetValue("ruid", out var ruidObj)
                    && !string.IsNullOrWhiteSpace(ruidObj?.ToString()))
                    sfrNewRoute.RootCuid = ruidObj.ToString();
            }

            // Stamp the active profile_info_id (same pattern as the normal upload path).
            if (input.File is StorageFileRoute sfrNew && sfrNew.ProfileInfoId == 0) {
                long activeProfileInfoId = 0;
                if (TryGetWorkspace(input, out VaultWorkSpace wsW) && wsW.ProfileInfoId > 0)
                    activeProfileInfoId = wsW.ProfileInfoId;
                else if (Indexer != null
                    && Indexer.TryGetComponentInfo<VaultModule>(input.Scope.Module.Cuid.ToString("N"), out VaultModule mW)
                    && mW.ProfileInfoId > 0)
                    activeProfileInfoId = mW.ProfileInfoId;
                if (activeProfileInfoId > 0) sfrNew.ProfileInfoId = activeProfileInfoId;
            }
            return true;
        }

        string DetermineTargetName(IVaultFileReadRequest input,bool forupload) {
            string targetFileName = string.Empty;

            IVaultFileWriteRequest inputW = input as IVaultFileWriteRequest;

            if (!string.IsNullOrWhiteSpace(input.RequestedName)) {
                targetFileName = Path.GetFileName(input.RequestedName);
            } else if (forupload) {
                if (!string.IsNullOrWhiteSpace(inputW!.OriginalName)) {
                    targetFileName = Path.GetFileName(inputW.OriginalName);
                } else if (inputW.FileStream is FileStream fs) {
                    targetFileName = Path.GetFileName(fs.Name);
                    if (string.IsNullOrWhiteSpace(inputW.OriginalName)) inputW.SetOriginalName(targetFileName);
                }
            }

            // ── Ensure extension is present ────────────────────────────────
            string targetExtension = Path.GetExtension(targetFileName ?? string.Empty);
            if (string.IsNullOrWhiteSpace(targetExtension) && forupload) {
                if (!string.IsNullOrWhiteSpace(inputW!.OriginalName))
                    targetExtension = Path.GetExtension(inputW.OriginalName);
                else if (inputW!.FileStream is FileStream fs)
                    targetExtension = Path.GetExtension(fs.Name);

                if (!string.IsNullOrWhiteSpace(targetExtension)) targetFileName += targetExtension;
                input.RequestedName = targetFileName;
            }

            // Format policy is skipped for thumbnail uploads — thumbnails use their own allowed extensions
            // (ThumbAllowedExtensions) validated at the controller level before reaching here.
            if (forupload && (input as IVaultFileWriteRequest)?.IsThumbnail != true && !IsFormatAllowed(targetExtension, FormatControlMode.Extension))
                throw new ArgumentException("Uploading this file format is not allowed.");

            if (string.IsNullOrWhiteSpace(input.RequestedName) && !string.IsNullOrWhiteSpace(targetFileName))
                input.RequestedName = targetFileName;
            return targetFileName;
        }

        async Task<bool> ResolvePathFast(IVaultFileReadRequest input, IVaultFileWriteRequest inputW, bool forupload, VaultWorkSpace wInfo, IStorageProvider provider = null) {
            if (!forupload && provider is FileSystemStorageProvider && wInfo != null && input.File != null) {
                string logicalId = null;
                string ext = Path.GetExtension(input.File.StorageName ?? input.RequestedName ?? string.Empty);

                if (wInfo.NameMode == VaultNameMode.Number && input.File.Id > 0)
                    logicalId = input.File.Id.ToString();
                else if (wInfo.NameMode == VaultNameMode.Guid && !string.IsNullOrWhiteSpace(input.File.Cuid)) {
                    // Storage names are always generated as compact-N (no dashes).
                    // Normalize regardless of what form the caller provided (dashed, braced, compact).
                    if (Guid.TryParse(input.File.Cuid, out var g))
                        logicalId = g.ToString("N");
                }

                if (!string.IsNullOrWhiteSpace(logicalId)) {
                    input.File.StorageRef = provider.BuildStorageRef(logicalId, ext, SplitProvider, Config.SuffixFile);
                    return true; // DB-free — no indexer query needed
                }

                // pn is the provider's processed/logical name. For local filesystem reads,
                // reconstruct the sharded path directly and avoid the module database.
                if (PopulateFromSavedPath(input, forupload, wInfo, provider))
                    return true;
            }

            // Attempt to resolve path without generating a new one (DB-backed).
            if ((!forupload || !string.IsNullOrWhiteSpace(input.File?.Cuid)) && input.File != null) {
                if (await GetPathFromIndexer(input, forupload, input.Scope.Workspace.Cuid.ToString("N"))) return true;
            }
            return false;
        }

        async Task RegisterRequestAndBuildPath(IVaultFileReadRequest input, IVaultFileWriteRequest inputW, bool forupload, VaultWorkSpace wInfo, string targetFileName,IStorageProvider provider = null) {

            if (string.IsNullOrWhiteSpace(targetFileName)) throw new ArgumentNullException("No target file name specified for this request.");

            var holder = new VaultStorable(targetFileName, wInfo.NameMode, wInfo.ParseMode, isVirtual: false);

            // Step A: register with indexer and populate holder.Id / holder.Cuid / holder.StorageName.
            // GenerateFileSystemSavePath is reused here only for the ID-registration side-effect;
            // the path it returns is only used for FS providers.
            var logicalResult = StorageUtils.GenerateFileSystemSavePath(
                holder,
                uidManager: (h) => {
                    // Only register in indexer when uploading — reads never create DB entries.
                    if (Indexer == null || !forupload) return (0, Guid.Empty);
                    return Indexer.RegisterDocuments(input, h).GetAwaiter().GetResult();
                },
                splitProvider: SplitProvider,
                suffix: Config.SuffixFile,
                throwExceptions: true);

            // Step B: build the actual storage reference via the resolved provider.
            // FS: sharded path (same as before). Cloud: flat object key.
            var targetFilePath = provider is FileSystemStorageProvider
                ? logicalResult.path
                : provider.BuildStorageRef(
                    logicalResult.name,
                    Path.GetExtension(targetFileName),
                    SplitProvider,
                    Config.SuffixFile);

            if (input.File == null)
                input.SetFile(new StorageFileRoute(targetFileName, targetFilePath) {
                    Id = holder.Id, Cuid = holder.Cuid.ToString("N"), Version = holder.Version, StorageName = holder.StorageName
                });

            input.File.StorageRef = targetFilePath;

            if (string.IsNullOrWhiteSpace(input.File.DisplayName)) input.File.SetDisplayName(input.RequestedName);
            if (string.IsNullOrWhiteSpace(input.File.Cuid)) input.File.SetCuid(holder.Cuid);
            if (string.IsNullOrWhiteSpace(input.File.StorageName)) input.File.StorageName = holder.StorageName;
            if (input.File.Id < 1) input.File.SetId(holder.Id);
            if (input.File is StorageFileRoute sfrReg && string.IsNullOrWhiteSpace(sfrReg.RootCuid))
                sfrReg.RootCuid = holder.DocumentCuid;
            if (forupload) input.File.Size = inputW!.FileStream?.Length ?? 0;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Step 3 — File route resolution
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves <see cref="IVaultFileReadRequest.File"/>.Path for the given request.
        /// For FS reads where the logical ID is already known, uses a DB-free sharded-path reconstruction.
        /// Otherwise queries the indexer by ID, CUID, or display name, calling
        /// <see cref="PopulateFileFromDic"/> to fill the route. For uploads, also registers
        /// the document with the indexer via <c>StorageUtils.GenerateFileSystemSavePath</c>.
        /// </summary>
        public async Task ProcessFileRoute(IVaultFileReadRequest input, IStorageProvider provider = null) {
            if (input == null) return;
            await EnsureWorkspaceContextAsync(input);
            provider ??= ResolveProvider(input);
            if (!string.IsNullOrWhiteSpace(input.OverrideRef)) return;
            if (input.File != null && !string.IsNullOrWhiteSpace(input.File.StorageRef)) return;

            IVaultFileWriteRequest inputW = input as IVaultFileWriteRequest;
            bool forupload = inputW != null;

            if (!Indexer.TryGetComponentInfo<VaultWorkSpace>(input.Scope.Workspace.Cuid.ToString("N"), out VaultWorkSpace wInfo) && forupload) {
                throw new Exception($"Unable to find workspace info. Name: {input.Scope.Workspace.Name} — Cuid: {input.Scope.Workspace.Cuid}.");
            }

            // If the caller supplied a document-level CUID (ruid) instead of a version CUID,
            // resolve it up front so the rest of the upload flow continues against a concrete
            // content version. This keeps replace/new-version/thumbnail behavior identical to uid uploads.
            await ResolveVersionCuidFromRootCuid(input, forupload);

            // ── Extension consistency check ────────────────────────────────
            // Skipped for thumbnail uploads — thumbnails intentionally differ in extension
            // from the parent document (e.g. a PDF document can have a JPEG thumbnail).
            if (inputW?.IsThumbnail != true)
                await CheckCuidExtensionConsistency(input, inputW, forupload);


            // ── CreateNewVersion fast path ─────────────────────────────────
            // replace=false at the controller sets CreateNewVersion=true on the write request.
            // Navigate versionCuid → parent document → new doc_version (version+1).
            // Filename and directory are irrelevant; the CUID is the sole identity signal.
            //APPLICABLE ONLY WHEN CUID OF THE FILE IS PRESENT OTHERWISE THIS REPLACE CONCEPT IS NOT APPLIACBLE.
            if (await CreateNewDocumentVersion(input, inputW, forupload, wInfo, provider)) return;

            // ── FS read-only fast path ─────────────────────────────────────
            // When the file ID (Number mode) or CUID (Guid mode) is already known and the
            // provider is local FileSystem, reconstruct the sharded path directly without
            // any DB round-trip. This is the "DB-free read" the design intends for FS.
            if (await ResolvePathFast(input, inputW, forupload, wInfo, provider)) return;

            // ── Determine the target filename ──────────────────────────────
            string targetFileName = DetermineTargetName(input, forupload) ?? string.Empty;

            if (forupload
                && inputW?.IsThumbnail != true
                && string.IsNullOrWhiteSpace(input.File?.Cuid)
                && string.IsNullOrWhiteSpace((input.File as StorageFileRoute)?.RootCuid)) {
                await ArchiveDeletedNameCollision(input, targetFileName);
            }

            if (input.File != null && !string.IsNullOrWhiteSpace(input.File.StorageRef)) return; //We already have what we need.. no need to check further.
                                                                                                 // ── Generate storage path and register with indexer ────────────
            await RegisterRequestAndBuildPath(input, inputW, forupload, wInfo, targetFileName, provider);
            
            // Stamp the effective profile_info_id so UpdateDocVersionInfo persists it to version_info.
            // Priority: workspace (most specific) → module.
            // This records exactly which profile was active when the file was stored,
            // enabling future reads to reconstruct the original provider even after a profile change.
            if (forupload && input.File is StorageFileRoute sfrWrite && sfrWrite.ProfileInfoId == 0) {
                long activeProfileInfoId = 0;
                if (TryGetWorkspace(input, out VaultWorkSpace wsW) && wsW.ProfileInfoId > 0)
                    activeProfileInfoId = wsW.ProfileInfoId;
                else if (Indexer != null
                    && Indexer.TryGetComponentInfo<VaultModule>(input.Scope.Module.Cuid.ToString("N"), out VaultModule mW)
                    && mW.ProfileInfoId > 0)
                    activeProfileInfoId = mW.ProfileInfoId;
                if (activeProfileInfoId > 0) sfrWrite.ProfileInfoId = activeProfileInfoId;
            }
        }

        /// <summary>
        /// Generates the sharded directory path for a workspace.
        /// Only <see cref="VaultObjectType.WorkSpace"/> is supported — clients and modules are
        /// DB-only hierarchy nodes with no physical directory.
        /// </summary>
        public (string name, string path) GenerateBasePath(IVaultObject input, VaultObjectType component) {
            if (component == VaultObjectType.File)
                throw new InvalidOperationException("GenerateBasePath does not support VaultObjectType.File. Use StorageUtils.GenerateFileSystemSavePath instead.");
            if (component != VaultObjectType.WorkSpace)
                throw new InvalidOperationException($"GenerateBasePath does not support VaultObjectType.{component}. Clients and modules are DB-only and have no physical path.");
            //We expect the input to be a vaultstorable and also to carry, Generate Name and GUID name mode.
            var result = StorageUtils.GenerateFileSystemSavePath(input, splitProvider: (n) => (2, 5), throwExceptions: false);
            return (result.name, "_" + result.path);
        }

        /// <summary>
        /// Resolves the workspace physical path segment and appends it to <paramref name="paths"/>.
        /// Uses the workspace's stored <see cref="VaultWorkSpace.StorageRef"/> when available;
        /// falls back to sharded-path generation.
        /// Virtual workspaces contribute no path segment.
        /// </summary>
        void AddComponentPath(IVaultReadRequest input, List<string> paths, IStorageProvider provider) {
            if (Indexer == null || input.Scope.Workspace == null) return;
            var wsCuid = input.Scope.Workspace.Cuid.ToString("N");

            if (Indexer.TryGetComponentInfo(wsCuid, out VaultWorkSpace ws)) {
                if (ws.IsVirtual) {
                    // Virtual workspaces have no workspace segment but still need client/module dirs for isolation.
                    paths.Add(!string.IsNullOrWhiteSpace(ws.Base) ? ws.Base : BuildWorkspaceParentPath(input));
                    CacheWorkspacePath(wsCuid, paths, provider);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(ws.StorageRef)) {
                    if (!string.IsNullOrWhiteSpace(ws.Base)) paths.Add(ws.Base);
                    paths.Add(ws.StorageRef);
                } else {
                    var seg = BuildFallbackWorkspacePath(input);
                    if (!string.IsNullOrWhiteSpace(seg)) paths.Add(seg);
                }
            } else {
                var seg = BuildFallbackWorkspacePath(input);
                if (!string.IsNullOrWhiteSpace(seg)) paths.Add(seg);
            }

            CacheWorkspacePath(wsCuid, paths, provider);
        }

        /// <summary>
        /// Builds the full relative workspace path (clientDir/moduleDir/_wsShardedPath) used when
        /// the workspace is not yet cached in the indexer or its StorageRef is not set.
        /// Always uses normalized (ToDBName) names for client and module segments.
        /// </summary>
        string BuildFallbackWorkspacePath(IVaultReadRequest input) {
            var parentPath = BuildWorkspaceParentPath(input);
            if (IsVirtualWorkspace(input)) return parentPath;

            var wsSegment = GenerateBasePath(input.Scope.Workspace, VaultObjectType.WorkSpace).path;
            if (string.IsNullOrWhiteSpace(parentPath)) return wsSegment;
            return Path.Combine(parentPath, wsSegment);
        }

        async Task EnsureWorkspaceContextAsync(IVaultReadRequest input, bool forceRefresh = false) {
            if (Indexer == null || input?.Scope?.Workspace == null) return;
            var workspaceCuid = input.Scope.Workspace.Cuid.ToString("N");
            var now = DateTime.UtcNow;
            if (!forceRefresh
                && Indexer.TryGetComponentInfo<VaultWorkSpace>(workspaceCuid, out _)
                && _workspaceRegistryRefresh.TryGetValue(workspaceCuid, out var refreshed)
                && now - refreshed < WorkspaceRegistryRefreshInterval)
                return;

            var refreshLock = _workspaceRegistryRefreshLocks.GetOrAdd(workspaceCuid, _ => new SemaphoreSlim(1, 1));
            await refreshLock.WaitAsync();
            try {
                now = DateTime.UtcNow;
                if (!forceRefresh
                    && Indexer.TryGetComponentInfo<VaultWorkSpace>(workspaceCuid, out _)
                    && _workspaceRegistryRefresh.TryGetValue(workspaceCuid, out refreshed)
                    && now - refreshed < WorkspaceRegistryRefreshInterval)
                    return;

                if (await Indexer.HydrateWorkspaceAsync(workspaceCuid, forceRefresh: true)) {
                    _pathCache.TryRemove(workspaceCuid, out _);
                    _workspaceRegistryRefresh.AddOrUpdate(workspaceCuid, now, (_, _) => now);

                    if (CheckWriteAccess(input).Status
                        && Indexer.TryGetComponentInfo(workspaceCuid, out VaultWorkSpace workspace)
                        && !workspace.IsVirtual
                        && ResolveProvider(input) is FileSystemStorageProvider)
                        Directory.CreateDirectory(GetContainedFileSystemPath(workspace.Base, workspace.StorageRef));
                }
            } finally {
                refreshLock.Release();
            }
        }

        bool IsVirtualWorkspace(IVaultReadRequest input) {
            if (input?.Scope?.Workspace == null) return false;
            var workspaceCuid = input.Scope.Workspace.Cuid.ToString("N");
            if (Indexer != null && Indexer.TryGetComponentInfo(workspaceCuid, out VaultWorkSpace workspace))
                return workspace.IsVirtual;
            if (input is StorageReadRequest concrete && concrete.WorkspaceIsVirtual)
                return true;
            return input.Scope.Workspace.Name.Equals(VaultConstants.DEFAULT_NAME, StringComparison.OrdinalIgnoreCase);
        }

        string BuildWorkspaceParentPath(IVaultReadRequest input) {
            var clientDir = input.Scope.Client?.Name?.ToDBName() ?? string.Empty;
            var moduleDir = input.Scope.Module?.Name?.ToDBName() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientDir)) return moduleDir;
            return string.IsNullOrWhiteSpace(moduleDir) ? clientDir : Path.Combine(clientDir, moduleDir);
        }

        void CacheWorkspacePath(string workspaceCuid, List<string> paths, IStorageProvider provider) {
            var joinedPath = provider is FileSystemStorageProvider
                ? Path.Combine(paths.ToArray())
                : string.Join("/", paths.Select(p => p.Trim('/', '\\')));
            _pathCache.AddOrUpdate(workspaceCuid, joinedPath, (_, _) => joinedPath);
        }

        /// <summary>
        /// Queries the indexer for an existing doc_version record and populates the file route.
        /// Looks up by ID or CUID first; falls back to name+directory search.
        /// Returns <c>true</c> when the file route was populated from the DB.
        /// Throws <see cref="ArgumentException"/> on reads when the file is not found.
        /// </summary>
        async Task<bool> GetPathFromIndexer(IVaultFileReadRequest input, bool forupload, string workspaceCuid) {
            if (!string.IsNullOrWhiteSpace(input.File?.Cuid) || input.File?.Id > 0) {
                var existing = input.File.Id > 0
                    ? await Indexer.GetDocVersionInfo(input.Scope.Module.Cuid.ToString("N"), input.File.Id)
                    : await Indexer.GetDocVersionInfo(input.Scope.Module.Cuid.ToString("N"), input.File.Cuid);

                if (existing?.Status == true && existing.Result is Dictionary<string, object> dic && dic.Count > 0)
                    return PopulateFileFromDic(input, dic);

                // A uid was explicitly supplied but doesn't exist in the DB.
                // Silently creating a new file under a caller-specified uid is wrong — throw instead.
                if (!forupload) {
                    var identityName = input.File.Id > 0 ? "id" : "uid";
                    var identityValue = input.File.Id > 0 ? input.File.Id.ToString() : input.File?.Cuid;
                    throw new ArgumentException($"File not found for the supplied {identityName} '{identityValue}'.");
                }
                if (forupload)
                    throw new ArgumentException($"No file found for the supplied uid '{input.File?.Cuid ?? input.File?.Id.ToString()}'. Upload without a uid to create a new file.");

            } else if (input.File is StorageFileRoute sfrRoot && !string.IsNullOrWhiteSpace(sfrRoot.RootCuid)) {
                // ruid path — resolve document CUID to the latest version before any other work.
                var existing = await Indexer.GetDocVersionInfoByDocCuid(input.Scope.Module.Cuid.ToString("N"), sfrRoot.RootCuid);

                if (existing?.Status == true && existing.Result is Dictionary<string, object> rdic && rdic.Count > 0) {
                    if (string.IsNullOrWhiteSpace(input.File.Cuid))
                        input.File.SetCuid(rdic["uid"]?.ToString());
                    return PopulateFileFromDic(input, rdic);
                }
                if (!forupload) throw new ArgumentException($"File not found for the supplied ruid '{sfrRoot.RootCuid}'.");

            } else if (!string.IsNullOrWhiteSpace(input.File?.StorageName)) {
                var existing = await Indexer.GetDocVersionInfoByStorageName(input.Scope.Module.Cuid.ToString("N"), input.File.StorageName);

                if (existing?.Status == true && existing.Result is Dictionary<string, object> sdic && sdic.Count > 0)
                    return PopulateFileFromDic(input, sdic);

                if (!forupload)
                    throw new ArgumentException($"File not found for the supplied storage name '{input.File.StorageName}'.");

            } else if (!string.IsNullOrWhiteSpace(input.File?.DisplayName) || !string.IsNullOrWhiteSpace(input.RequestedName)) {
                var searchName = input.File?.DisplayName ?? input.RequestedName;
                var dirName = input.Scope.Folder?.DisplayName ?? VaultConstants.DEFAULT_NAME;
                var dirParent = input.Scope.Folder?.Parent?.Id ?? 0;
                var existing = await Indexer.GetDocVersionInfo(input.Scope.Module.Cuid.ToString("N"), workspaceCuid, searchName, dirName, dirParent);

                if (existing?.Status == true && existing.Result is Dictionary<string, object> dic && dic.Count > 0) {
                    if (input.File == null)
                        input.SetFile(new StorageFileRoute(input.RequestedName, string.Empty) { Cuid = dic["uid"]?.ToString() });
                    if (string.IsNullOrWhiteSpace(input.File.Cuid)) input.File.SetCuid(dic["uid"]?.ToString());
                    if (string.IsNullOrWhiteSpace(input.File.DisplayName)) input.File.SetDisplayName(searchName);
                    return PopulateFileFromDic(input, dic);
                } else {
                    if (!forupload) throw new ArgumentException("File not found in the indexer.");
                }
            }
            return false;
        }

        /// <summary>
        /// Populates path, size, save-as name, staging path, and lifecycle flags on the file route
        /// from a <c>version_info</c> dictionary row returned by the indexer.
        /// Returns <c>true</c> if the file was located (storage or staging path was found).
        /// A file that is still in staging (flags bit 4 set, bit 8 not yet set) will have
        /// <see cref="IVaultFileRoute.StorageRef"/> set to the staging key so the coordinator
        /// can decide whether to stream from staging or redirect to a pre-signed URL.
        /// </summary>
        bool PopulateFileFromDic(IVaultFileReadRequest input, Dictionary<string, object> dic) {
            var storagePath = dic.TryGetValue("path", out var p) ? p?.ToString() : null;
            var stagingPath = dic.TryGetValue("staging_path", out var sp) ? sp?.ToString() : null;

            // A row exists but neither path is populated — treat as not found.
            if (string.IsNullOrWhiteSpace(storagePath) && string.IsNullOrWhiteSpace(stagingPath))
                return false;

            // Prefer the promoted storage path; fall back to staging path if file is still in staging.
            input.File.StorageRef = !string.IsNullOrWhiteSpace(storagePath) ? storagePath : stagingPath;

            if (long.TryParse(dic.TryGetValue("id", out var idObj) ? idObj?.ToString() : null, out var id) && id > 0)
                input.File.SetId(id);
            if (string.IsNullOrWhiteSpace(input.File.Cuid)
                && dic.TryGetValue("uid", out var uidObj)
                && !string.IsNullOrWhiteSpace(uidObj?.ToString()))
                input.File.SetCuid(uidObj.ToString());
            if (long.TryParse(dic["size"]?.ToString(), out var size)) input.File.Size = size;
            if (int.TryParse(dic["ver"]?.ToString(), out var version)) input.File.Version = version;
            if (input.File is StorageRoute route && long.TryParse(dic.TryGetValue("actor", out var actorObj) ? actorObj?.ToString() : null, out var actor))
                route.Actor = actor;
            // saveas_name = vi.storage_name alias; dname = di.display_name (human readable)
            input.File.StorageName = dic.TryGetValue("saveas_name", out var sn) ? sn?.ToString() ?? string.Empty : string.Empty;
            // Restore the human-readable display name from doc_info so callers can use it as the download filename.
            var dname = dic.TryGetValue("dname", out var dn) ? dn?.ToString() : null;
            if (!string.IsNullOrWhiteSpace(dname) && string.IsNullOrWhiteSpace(input.File.DisplayName))
                input.File.SetDisplayName(dname);

            // Flags, staging path, stored profile_info_id, and document cuid are on StorageFileRoute.
            if (input.File is StorageFileRoute sfr) {
                if (int.TryParse(dic["flags"]?.ToString(), out var flags)) sfr.Flags = flags;
                sfr.StagingRef = stagingPath ?? string.Empty;
                // Restore the profile_info_id that was active when this version was written.
                // ResolveProvider will use it to reconstruct the exact original provider chain.
                if (dic.TryGetValue("profile_info_id", out var pidObj)
                    && long.TryParse(pidObj?.ToString(), out var pid) && pid > 0)
                    sfr.ProfileInfoId = pid;
                // Restore the document-level cuid (ruid) so the upload response can return it.
                if (string.IsNullOrWhiteSpace(sfr.RootCuid)
                    && dic.TryGetValue("ruid", out var ruidObj)
                    && !string.IsNullOrWhiteSpace(ruidObj?.ToString()))
                    sfr.RootCuid = ruidObj.ToString();
            }

            return true;
        }

        /// <summary>
        /// Fast-path for reads where <see cref="IVaultFileRoute.StorageName"/> is already known.
        /// Validates the name matches the workspace's <see cref="VaultNameMode"/> then calls
        /// <see cref="IStorageProvider.BuildStorageRef"/> to reconstruct the sharded/flat key without a DB round-trip.
        /// Returns <c>false</c> for uploads or when StorageName is empty.
        /// </summary>
        bool PopulateFromSavedPath(IVaultFileReadRequest input, bool forupload, VaultWorkSpace wInfo, IStorageProvider provider) {
            if (forupload || string.IsNullOrWhiteSpace(input?.File?.StorageName)) return false;
            var rawName = input.File.StorageName.Trim();
            if (!string.Equals(Path.GetFileName(rawName), rawName, StringComparison.Ordinal))
                throw new ArgumentException("StorageName must be a processed name, not a path.");

            var sname = Path.GetFileNameWithoutExtension(rawName);
            var extension = Path.GetExtension(rawName);

            if (wInfo.NameMode == VaultNameMode.Number && !sname.IsNumber())
                throw new ArgumentException("StorageName must be numeric for this workspace.");

            if (wInfo.NameMode == VaultNameMode.Guid) {
                if (sname.IsCompactGuid(out Guid g) || sname.IsValidGuid(out g))
                    sname = g.ToString("N");
                else
                    throw new ArgumentException("StorageName must be a valid GUID for this workspace.");
            }

            // Use provider's key format: FS applies sharding, cloud returns a flat key.
            input.File.StorageRef = provider.BuildStorageRef(sname, extension, SplitProvider, Config.SuffixFile);
            return true;
        }
    }
}

