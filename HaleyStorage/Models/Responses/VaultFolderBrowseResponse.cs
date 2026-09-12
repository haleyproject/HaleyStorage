using System.Collections.Generic;

namespace Haley.Models {
    /// <summary>
    /// Paged folder listing rooted at the current virtual directory.
    /// </summary>
    public class VaultFolderBrowseResponse {
        public long WorkspaceId { get; set; }
        public string WorkspaceCuid { get; set; } = string.Empty;
        public bool IsRoot { get; set; }
        public long CurrentFolderId { get; set; }
        public string CurrentFolderCuid { get; set; } = string.Empty;
        public string CurrentFolderName { get; set; } = string.Empty;
        public string CurrentFolderPath { get; set; } = string.Empty;
        public long CurrentFolderParentId { get; set; }
        public bool IncludeAll { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public bool TotalsIncluded { get; set; } = true;
        public bool HasNext { get; set; }
        public long TotalItems { get; set; }
        public long TotalFolders { get; set; }
        public long TotalFiles { get; set; }
        public List<VaultBrowseItem> Items { get; set; } = new();
    }
}
