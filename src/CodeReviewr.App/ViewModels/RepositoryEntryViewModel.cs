using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeReviewr.App.ViewModels;

public partial class RepositoryEntryViewModel : ObservableObject
{
    public RepositoryEntryViewModel(string path, string name, string relativePath, string? branch)
    {
        Path = path;
        Name = name;
        RelativePath = relativePath;
        Branch = branch;
    }

    public string Path { get; }
    public string Name { get; }
    public string RelativePath { get; }
    public string? Branch { get; }

    [ObservableProperty] private bool _isCurrent;

    public string BranchDisplay => string.IsNullOrWhiteSpace(Branch) ? "" : Branch;
    public bool HasBranch => !string.IsNullOrWhiteSpace(Branch);

    public bool MatchesFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;

        return Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
               || (Branch?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
               || Path.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
