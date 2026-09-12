using Haley.Enums;
using Haley.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Haley.Abstractions {
    public interface IStorageDirectoryOperations {
        Task<IVaultDirResponse> GetDirectoryInfo(IVaultReadRequest input);
        Task<IFeedback<string>> GetParent(IVaultFileReadRequest input);
        Task<IFeedback<VaultFolderBrowseResponse>> BrowseFolder(IVaultReadRequest input, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true);
        /// <summary>
        /// Searches for folders and files whose vault name matches <paramref name="searchTerm"/>
        /// (using <paramref name="searchMode"/>), optionally filtered by extension.
        /// Scope: entire workspace (<paramref name="directoryId"/> = 0), a single directory, or
        /// a full recursive subtree (<paramref name="recursive"/> = true).
        /// Returns the latest file version for each matching document. Paginated.
        /// </summary>
        Task<IFeedback<VaultFolderBrowseResponse>> SearchItems(IVaultReadRequest input, string searchTerm, VaultSearchMode searchMode, string extension = null, bool recursive = false, int page = 1, int pageSize = 50, bool includeAll = false, VaultFolderSortMode sort = VaultFolderSortMode.Id, VaultSortDirection direction = VaultSortDirection.Asc, VaultFolderItemKind kind = VaultFolderItemKind.Both, bool includeTotals = true);
        Task<IFeedback<VaultFileDetailsResponse>> GetFileDetails(IVaultFileReadRequest input);
        Task<IVaultResponse> CreateDirectory(IVaultReadRequest input, string rawname);
        Task<IFeedback> DeleteDirectory(IVaultReadRequest input, bool recursive);
        Task<IFeedback> RestoreDirectory(IVaultReadRequest input, bool force = false);
    
    }
}
