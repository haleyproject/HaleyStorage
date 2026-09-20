using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Haley.Services {
    /// <summary>
    /// Partial class — thumbnail download.
    /// Upload is handled by the standard <see cref="Upload"/> path with
    /// <see cref="IVaultFileWriteRequest.IsThumbnail"/> set to <c>true</c>.
    /// </summary>
    public partial class StorageCoordinator : IStorageCoordinator {

        /// <inheritdoc/>
        public async Task<IVaultStreamResponse> DownloadThumbnail(IVaultFileReadRequest request) {
            var result = new VaultStreamResponse() { Status = false, Stream = Stream.Null };
            try {
                if (request == null) { result.Message = "Request cannot be null."; return result; }

                var moduleCuid = StorageUtils.GenerateCuid(request, VaultObjectType.Module);

                // Resolve the content version so we can look up its ver number and document id.
                PrepareRequestContext(request);
                await Indexer.DemandDirectoryAccess(request);
                await ProcessFileRoute(request);
                await Indexer.DemandDirectoryAccess(request);

                if (request.File == null || string.IsNullOrWhiteSpace(request.File.Cuid)) {
                    result.Message = "Unable to resolve content version from request.";
                    return result;
                }

                // Get the content version row to extract id and ver number.
                var versionInfoFb = await Indexer.GetDocVersionInfo(moduleCuid, request.File.Cuid);
                if (versionInfoFb?.Status != true || versionInfoFb.Result is not Dictionary<string, object> vDic) {
                    result.Message = "Content version not found.";
                    return result;
                }

                if (!long.TryParse(vDic["id"]?.ToString(), out long contentVersionId) || contentVersionId < 1) {
                    result.Message = "Unable to read content version id.";
                    return result;
                }

                if (!int.TryParse(vDic["ver"]?.ToString(), out int contentVer) || contentVer < 1) {
                    result.Message = "Unable to read content version number.";
                    return result;
                }

                // Resolve document id from the content version id.
                var documentId = await Indexer.GetDocumentIdByVersionId(moduleCuid, contentVersionId);
                if (documentId < 1) { result.Message = "Unable to resolve parent document id."; return result; }

                // Fetch the latest thumbnail sub-version for this (document, content ver).
                var thumbFb = await Indexer.GetLatestThumbInfo(moduleCuid, documentId, contentVer);
                if (thumbFb?.Status != true || thumbFb.Result is not Dictionary<string, object> thumbDic) {
                    result.Message = "No thumbnail found for this file.";
                    return result;
                }

                var storagePath = thumbDic.TryGetValue("path", out var p) ? p?.ToString() : null;
                var stagingPath = thumbDic.TryGetValue("staging_path", out var sp) ? sp?.ToString() : null;
                var flags = thumbDic.TryGetValue("flags", out var fl) && int.TryParse(fl?.ToString(), out var flagValue)
                    ? (VersionFlags)flagValue
                    : VersionFlags.None;
                var profileInfoId = thumbDic.TryGetValue("profile_info_id", out var pid) && long.TryParse(pid?.ToString(), out var resolvedProfileInfoId)
                    ? resolvedProfileInfoId
                    : 0L;

                if (string.IsNullOrWhiteSpace(storagePath) && string.IsNullOrWhiteSpace(stagingPath)) {
                    result.Message = "Thumbnail has no storage path in DB.";
                    return result;
                }

                var targetRef = !string.IsNullOrWhiteSpace(storagePath) ? storagePath : stagingPath;
                ProviderReadResult readResult;
                string accessUrl = null;

                if ((flags & VersionFlags.InStaging) != 0 && (flags & VersionFlags.InStorage) == 0 && !string.IsNullOrWhiteSpace(stagingPath)) {
                    var stagingProvider = profileInfoId > 0
                        ? GetProvidersForProfile(profileInfoId, moduleCuid).staging
                        : ResolveStagingProvider(request);

                    if (stagingProvider == null) {
                        result.Message = "Thumbnail is marked as staged, but no staging provider is available.";
                        return result;
                    }

                    accessUrl = await stagingProvider.GetAccessUrl(stagingPath, TimeSpan.FromHours(1));
                    readResult = string.IsNullOrWhiteSpace(accessUrl)
                        ? await stagingProvider.ReadAsync(stagingPath, autoSearchExtension: false)
                        : ProviderReadResult.Ok(Stream.Null, string.Empty);
                } else {
                    var provider = profileInfoId > 0
                        ? GetProvidersForProfile(profileInfoId, moduleCuid).primary
                        : ResolveProvider(request);
                    var basePath = FetchWorkspaceBasePath(request, provider);
                    var fullPath = provider.BuildFullPath(basePath, targetRef);
                    readResult = await provider.ReadAsync(fullPath, autoSearchExtension: false);
                }

                if (!readResult.Success) { result.Message = readResult.Message; return result; }

                var saveasName = thumbDic.TryGetValue("saveas_name", out var sn) ? sn?.ToString() ?? "thumb" : "thumb";
                var resolvedExtension = !string.IsNullOrWhiteSpace(readResult.Extension)
                    ? readResult.Extension
                    : Path.GetExtension(targetRef);
                if (string.IsNullOrWhiteSpace(Path.GetExtension(saveasName)) && !string.IsNullOrWhiteSpace(resolvedExtension))
                    saveasName += resolvedExtension;

                result.Status = true;
                result.Stream = readResult.Stream;
                result.AccessUrl = accessUrl;
                // Use the thumbnail's own extension (e.g. .jpg) — not the document's extension.
                result.Extension = !string.IsNullOrWhiteSpace(resolvedExtension) ? resolvedExtension : Path.GetExtension(saveasName);
                result.SaveName = saveasName;
                return result;
            } catch (Exception ex) {
                result.Message = ex.Message + Environment.NewLine + ex.StackTrace;
                _logger?.LogError(result.Message);
                if (ThrowExceptions) throw;
                return result;
            }
        }
    }
}
