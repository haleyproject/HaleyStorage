namespace Haley.Utils;

public static class DirectoryVisibility {
    public static bool IsHiddenName(string name) => name?.TrimStart().StartsWith('.') == true;

    public static void ValidateName(string name, bool allowHidden) {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120 || name.Trim() is "." or ".."
            || name.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            throw new ArgumentException("A folder name must contain 1 to 120 characters and cannot contain path separators or traversal segments.");
        if (!allowHidden && IsHiddenName(name))
            throw new ArgumentException("You cannot create or rename a folder with a dot prefix. Dot-prefixed folders are reserved for system administration.");
    }
}
