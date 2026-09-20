namespace Haley.Models;

internal sealed class DirectoryThumbnailTarget {
    public string FolderCuid { get; set; } = string.Empty;
    public string RootCuid { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool HasVersion { get; set; }
}
