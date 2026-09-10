using System.Collections.Generic;
using System;

namespace Haley.Models {
    /// <summary>
    /// Full metadata view for one logical document, including all versions.
    /// </summary>
    public class VaultFileDetailsResponse {
        public long DocumentId { get; set; }
        public string DocumentCuid { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long DocumentActorId { get; set; }
        public long WorkspaceId { get; set; }
        public string WorkspaceCuid { get; set; } = string.Empty;
        public string WorkspaceName { get; set; } = string.Empty;
        public long DirectoryId { get; set; }
        public string DirectoryCuid { get; set; } = string.Empty;
        public string DirectoryName { get; set; } = string.Empty;
        public long DirectoryActorId { get; set; }
        public long DirectoryParentId { get; set; }
        public int DeleteState { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? Deleted { get; set; }
        public int DocumentDeleteState { get; set; }
        public bool DocumentIsDeleted { get; set; }
        public DateTime? DocumentDeleted { get; set; }
        public int VersionCount { get; set; }
        /// <summary>Document-level metadata (from doc_info.metadata). Empty string if not set.</summary>
        public string DocumentMetadata { get; set; } = string.Empty;
        /// <summary>
        /// True when the latest content version has at least one thumbnail sub-version (<c>sub_ver &gt; 0</c>).
        /// Use <c>GET /file/view?uid=…&amp;thumb=1</c> to stream the thumbnail.
        /// </summary>
        public bool HasThumbnail { get; set; }
        public List<VaultFileVersionInfo> Versions { get; set; } = new();
    }
}
