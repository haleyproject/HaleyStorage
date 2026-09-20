using System;

namespace Haley.Models {
    /// <summary>
    /// Single row returned by folder browsing. Represents either a virtual folder or a logical file.
    /// File rows carry the latest version summary so clients can render a folder view without
    /// making a second round trip for every item.
    /// </summary>
    public class VaultBrowseItem {
        public bool IsHidden { get; set; }
        public string ItemType { get; set; } = string.Empty; // "folder" | "file"
        public long Id { get; set; }
        public string Cuid { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long? ActorId { get; set; }
        public long ParentId { get; set; }
        public string VirtualPath { get; set; } = string.Empty;
        public int DeleteState { get; set; }
        public bool IsDeleted { get; set; }
        public DateTime? Deleted { get; set; }
        public int? DocumentDeleteState { get; set; }
        public bool DocumentIsDeleted { get; set; }
        public DateTime? DocumentDeleted { get; set; }
        public int? LatestVersionDeleteState { get; set; }
        public bool LatestVersionIsDeleted { get; set; }
        public DateTime? LatestVersionDeleted { get; set; }
        public DateTime? Created { get; set; }
        public DateTime? Modified { get; set; }

        // Latest version summary — populated for file rows only.
        public long? LatestVersionId { get; set; }
        public string LatestVersionCuid { get; set; } = string.Empty;
        public int? LatestVersionNumber { get; set; }
        public int? VersionCount { get; set; }
        public bool HasThumbnail { get; set; }
        public DateTime? LatestVersionCreated { get; set; }
        public long? Size { get; set; }
        public string StorageName { get; set; } = string.Empty;
        public string StorageRef { get; set; } = string.Empty;
        public string StagingRef { get; set; } = string.Empty;
        public int? Flags { get; set; }
        public string Hash { get; set; } = string.Empty;
        public DateTime? SyncedAt { get; set; }
    }
}
