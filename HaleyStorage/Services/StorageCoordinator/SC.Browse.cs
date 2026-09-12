using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using System;
using System.Threading.Tasks;

namespace Haley.Services {
    /// <summary>
    /// Partial class — DB-backed browse/explore APIs.
    /// </summary>
    public partial class StorageCoordinator : IStorageCoordinator {
        public async Task<IFeedback<VaultFolderBrowseResponse>> BrowseFolder(IVaultReadRequest input, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true) {
            var fb = new Feedback<VaultFolderBrowseResponse>();
            try {
                if (input == null) return fb.SetMessage("Input request cannot be empty.");
                if (Indexer == null) return fb.SetMessage("BrowseFolder requires an indexer.");
                if (input.Scope?.Workspace == null) return fb.SetMessage("Workspace information is required.");

                input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));
                return await Indexer.BrowseFolder(input, page, pageSize, includeAll, sort, direction, kind, includeTotals);
            } catch (Exception ex) {
                return fb.SetMessage(ex.Message);
            }
        }

        public async Task<IFeedback<VaultFileDetailsResponse>> GetFileDetails(IVaultFileReadRequest input) {
            var fb = new Feedback<VaultFileDetailsResponse>();
            try {
                if (input == null) return fb.SetMessage("Input request cannot be empty.");
                if (Indexer == null) return fb.SetMessage("GetFileDetails requires an indexer.");
                if (input.Scope?.Workspace == null) return fb.SetMessage("Workspace information is required.");

                input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, Enums.VaultObjectType.WorkSpace));
                return await Indexer.GetFileDetails(input);
            } catch (Exception ex) {
                return fb.SetMessage(ex.Message);
            }
        }
    }
}
