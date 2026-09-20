namespace Haley.Models;

public sealed class VaultDirectoryInfo {
    public string FolderCuid { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
    public bool HasThumbnail { get; set; }
    public bool IsHidden { get; set; }
    internal long DirectoryId { get; set; }
    internal string ThumbnailRootCuid { get; set; } = string.Empty;
}
