using CommunityToolkit.Mvvm.ComponentModel;

namespace GitDelta.App.ViewModels;

public partial class RepositoryEntryViewModel : ObservableObject
{
    public RepositoryEntryViewModel(
        string path,
        string name,
        string relativePath,
        string? branch,
        bool isLinkedWorktree = false,
        string? mainWorktreePath = null)
    {
        Path = path;
        Name = name;
        RelativePath = relativePath;
        Branch = branch;
        IsLinkedWorktree = isLinkedWorktree;
        MainWorktreePath = mainWorktreePath;
        ParentRepoName = string.IsNullOrWhiteSpace(mainWorktreePath)
            ? null
            : System.IO.Path.GetFileName(mainWorktreePath.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar));
    }

    public string Path { get; }
    public string Name { get; }
    public string RelativePath { get; }
    public string? Branch { get; }
    public bool IsLinkedWorktree { get; }
    public string? MainWorktreePath { get; }
    public string? ParentRepoName { get; }

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isPinned;

    public string DisplayName =>
        IsLinkedWorktree && !string.IsNullOrWhiteSpace(ParentRepoName)
            ? $"{Name} · {ParentRepoName}"
            : Name;

    public string BranchDisplay => string.IsNullOrWhiteSpace(Branch) ? "" : Branch;
    public bool HasBranch => !string.IsNullOrWhiteSpace(Branch);

    public string WorktreeTooltip =>
        IsLinkedWorktree && !string.IsNullOrWhiteSpace(MainWorktreePath)
            ? $"{Path}\nLinked to {MainWorktreePath}"
            : Path;

    public string SortGroupKey => MainWorktreePath ?? Path;
    public int SortOrderInGroup => IsLinkedWorktree ? 1 : 0;

    public bool MatchesFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || (Branch?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || (ParentRepoName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsExactNameMatch(string? filter) =>
        !string.IsNullOrWhiteSpace(filter)
        && (Name.Equals(filter.Trim(), StringComparison.OrdinalIgnoreCase)
            || DisplayName.Equals(filter.Trim(), StringComparison.OrdinalIgnoreCase));
}
