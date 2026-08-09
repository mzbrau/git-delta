using GitDelta.Core;
using GitDelta.Core.Diff;

namespace GitDelta.App.ViewModels;

/// <summary>Host surface that presents file-history browse diffs into a WC or PR diff pane.</summary>
public interface IFileHistoryBrowseHost
{
    string? RepositoryPath { get; }

    /// <summary>Path currently shown in the History tab / browse subject.</summary>
    FilePath? BrowseSubjectPath { get; }

    /// <summary>
    /// When non-null, VsCurrent compares the historical OID against this revision (PR head).
    /// When null, VsCurrent compares against the worktree.
    /// </summary>
    CommitId? CurrentRevision { get; }

    DiffOptions BuildDiffOptions();

    /// <summary>
    /// Shows the diff-pane loading overlay before a browse git fetch starts.
    /// Must not clear existing rows so the previous diff remains visible under the spinner.
    /// </summary>
    Task BeginFileHistoryDiffLoadAsync();

    /// <summary>
    /// Clears the browse loading overlay when a load is cancelled or fails before present.
    /// Callers must only invoke this for the still-current browse generation.
    /// </summary>
    Task EndFileHistoryDiffLoadAsync();

    /// <summary>Presents a browse-mode diff for <paramref name="path"/> without leaving the current pane.</summary>
    Task PresentFileHistoryDiffAsync(FilePath path, FileDiff diff, CancellationToken ct);

    /// <summary>Leaves browse mode and reloads the normal WC/PR diff for the active selection.</summary>
    Task ExitFileHistoryBrowseAsync();

    /// <summary>
    /// Makes <paramref name="path"/> the active file (Recent Files if needed) and keeps browse mode
    /// for <paramref name="oid"/>.
    /// </summary>
    Task OpenPathInFileHistoryBrowseAsync(FilePath path, string oid, CancellationToken ct);
}
