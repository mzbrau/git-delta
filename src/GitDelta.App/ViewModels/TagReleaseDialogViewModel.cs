using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitDelta.App.Services;
using GitDelta.Core;
using GitDelta.Core.Abstractions;

namespace GitDelta.App.ViewModels;

/// <summary>In-app overlay for listing tags and creating an annotated tag + push.</summary>
public partial class TagReleaseDialogViewModel : ObservableObject
{
    private readonly IGitTagService _tags;
    private readonly NotificationService _notifications;
    private readonly Func<Task> _onCompletedAsync;

    private string? _repoPath;
    private CancellationTokenSource? _loadCts;
    private readonly List<TagInfo> _allTags = [];

    public TagReleaseDialogViewModel(
        IGitTagService tags,
        NotificationService notifications,
        Func<Task> onCompletedAsync)
    {
        _tags = tags;
        _notifications = notifications;
        _onCompletedAsync = onCompletedAsync;
    }

    public ObservableCollection<TagListItemViewModel> FilteredTags { get; } = [];

    [ObservableProperty] private string? _currentBranch;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string _newTagName = "";
    [ObservableProperty] private string _tagMessage = "";
    [ObservableProperty] private bool _pushAllTags;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _loadError;

    public bool ShowBranchWarning =>
        !string.IsNullOrWhiteSpace(CurrentBranch)
        && !RebaseWizardViewModel.IsProtectedBranchName(CurrentBranch);

    public string? BranchWarningText =>
        ShowBranchWarning
            ? $"You are on '{CurrentBranch}', not main or master. Tags are usually created from the default branch."
            : null;

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public bool ShowEmptyTags =>
        !IsLoading && !HasLoadError && FilteredTags.Count == 0;

    public bool CanAddAndPush =>
        !IsBusy
        && !IsLoading
        && _repoPath is not null
        && !string.IsNullOrWhiteSpace(NewTagName)
        && !string.IsNullOrWhiteSpace(TagMessage);

    public async Task OpenAsync(string repositoryPath, string? currentBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        _repoPath = repositoryPath;
        CurrentBranch = currentBranch;
        FilterText = "";
        NewTagName = "";
        TagMessage = "";
        PushAllTags = false;
        LoadError = null;
        _allTags.Clear();
        FilteredTags.Clear();
        OnPropertyChanged(nameof(ShowBranchWarning));
        OnPropertyChanged(nameof(BranchWarningText));
        OnPropertyChanged(nameof(ShowEmptyTags));
        AddAndPushCommand.NotifyCanExecuteChanged();

        await ReloadTagsAsync();
    }

    public void Reset()
    {
        CancelLoad();
        _repoPath = null;
        CurrentBranch = null;
        FilterText = "";
        NewTagName = "";
        TagMessage = "";
        PushAllTags = false;
        IsLoading = false;
        IsBusy = false;
        LoadError = null;
        _allTags.Clear();
        FilteredTags.Clear();
        OnPropertyChanged(nameof(ShowBranchWarning));
        OnPropertyChanged(nameof(BranchWarningText));
        OnPropertyChanged(nameof(ShowEmptyTags));
        OnPropertyChanged(nameof(HasLoadError));
        AddAndPushCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddAndPush))]
    private async Task AddAndPushAsync()
    {
        if (_repoPath is null) return;

        var name = NewTagName.Trim();
        var message = TagMessage.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(message))
            return;

        IsBusy = true;
        AddAndPushCommand.NotifyCanExecuteChanged();
        try
        {
            await _tags.CreateAnnotatedTagAsync(_repoPath, name, message).ConfigureAwait(true);

            if (PushAllTags)
                await _tags.PushAllTagsAsync(_repoPath).ConfigureAwait(true);
            else
                await _tags.PushTagAsync(_repoPath, name).ConfigureAwait(true);

            _notifications.Info($"Tagged and pushed {name}");
            await _onCompletedAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // User closed or cancelled; leave dialog state as-is.
        }
        catch (Exception ex)
        {
            _notifications.Error($"Tag failed: {ex.Message}", () => _ = AddAndPushAsync(), ex);
        }
        finally
        {
            IsBusy = false;
            AddAndPushCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnCurrentBranchChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowBranchWarning));
        OnPropertyChanged(nameof(BranchWarningText));
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnNewTagNameChanged(string value) =>
        AddAndPushCommand.NotifyCanExecuteChanged();

    partial void OnTagMessageChanged(string value) =>
        AddAndPushCommand.NotifyCanExecuteChanged();

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowEmptyTags));
        AddAndPushCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) =>
        AddAndPushCommand.NotifyCanExecuteChanged();

    partial void OnLoadErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(ShowEmptyTags));
    }

    private async Task ReloadTagsAsync()
    {
        if (_repoPath is null) return;

        CancelLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;
        LoadError = null;

        try
        {
            var tags = await _tags.ListTagsAsync(_repoPath, cts.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_loadCts, cts))
                return;

            _allTags.Clear();
            _allTags.AddRange(tags);
            ApplyFilter();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded or closed.
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_loadCts, cts))
                return;
            LoadError = ex.Message;
            _allTags.Clear();
            FilteredTags.Clear();
            OnPropertyChanged(nameof(ShowEmptyTags));
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
                IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        FilteredTags.Clear();
        foreach (var tag in _allTags)
        {
            if (filter.Length > 0
                && tag.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) == false
                && (tag.Message is null
                    || tag.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) == false))
            {
                continue;
            }

            FilteredTags.Add(new TagListItemViewModel(tag));
        }

        OnPropertyChanged(nameof(ShowEmptyTags));
    }

    private void CancelLoad()
    {
        try
        {
            _loadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore.
        }

        _loadCts?.Dispose();
        _loadCts = null;
    }
}

/// <summary>Presentation wrapper for a <see cref="TagInfo"/> row in the tag dialog.</summary>
public sealed class TagListItemViewModel(TagInfo tag)
{
    public TagInfo Tag { get; } = tag;
    public string Name => Tag.Name;
    public string? Message => Tag.Message;

    public string DateDisplay =>
        Tag.Date == DateTimeOffset.MinValue
            ? "—"
            : Tag.Date.ToLocalTime().ToString("d MMM yyyy HH:mm");

    public string SecondaryText =>
        string.IsNullOrWhiteSpace(Message) ? DateDisplay : $"{DateDisplay} · {Message}";
}
