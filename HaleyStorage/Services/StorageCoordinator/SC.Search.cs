using Haley.Abstractions;
using Haley.Enums;
using Haley.Models;
using Haley.Utils;
using System;
using System.Threading.Tasks;

namespace Haley.Services {
    /// <summary>
    /// Partial class — workspace-scoped search for folders and files.
    /// Thin coordinator wrapper: validates preconditions, stamps workspace CUID, delegates to the indexer.
    /// </summary>
    public partial class StorageCoordinator : IStorageCoordinator {

        public async Task<IFeedback<VaultFolderBrowseResponse>> SearchItems(IVaultReadRequest input, string searchTerm, VaultSearchMode searchMode, string extension = null, bool recursive = false, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true) {

            var fb = new Feedback<VaultFolderBrowseResponse>();
            try {
                if (input == null)
                    return fb.SetMessage("Input request cannot be empty.");
                if (Indexer == null)
                    return fb.SetMessage("SearchItems requires an indexer.");
                if (input.Scope?.Workspace == null)
                    return fb.SetMessage("Workspace information is required.");

                input.Scope.Workspace.SetCuid(StorageUtils.GenerateCuid(input, VaultObjectType.WorkSpace));

                return await Indexer.SearchItems(input, searchTerm, searchMode, extension, recursive, page, pageSize, includeAll, sort, direction, kind, includeTotals);
            } catch (Exception ex) {
                return fb.SetMessage(ex.Message);
            }
        }
    }
}
