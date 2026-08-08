using System.Diagnostics;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diagnostics;
using GitDelta.Git.Internal;

namespace GitDelta.Git;

/// <summary>List tags, create annotated tags, and push tags to origin.</summary>
public sealed class GitTagService(IGitProcessRunner runner, IRepositoryGateProvider gates) : IGitTagService
{
    private const string FieldSeparator = "\u0001";
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    public Task<IReadOnlyList<TagInfo>> ListTagsAsync(string repositoryPath, CancellationToken ct = default) =>
        gates.For(repositoryPath).RunReadAsync(async token =>
        {
            var format = string.Join(
                FieldSeparator,
                "%(refname:short)",
                "%(creatordate:iso-strict)",
                "%(objectname)",
                "%(contents:subject)");
            var result = await runner.RunAsync(
                repositoryPath,
                ["for-each-ref", "--sort=-creatordate", $"--format={format}", "refs/tags"],
                options: null,
                token).ConfigureAwait(false);

            return (IReadOnlyList<TagInfo>)ParseTags(result.Stdout);
        }, ct);

    public Task CreateAnnotatedTagAsync(
        string repositoryPath,
        string name,
        string message,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return gates.For(repositoryPath).RunIndexWriteAsync(
            token => runner.RunAsync(
                repositoryPath,
                ["tag", "-a", "-m", message, "--", name],
                options: null,
                token),
            ct);
    }

    public Task PushTagAsync(
        string repositoryPath,
        string name,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return gates.For(repositoryPath).RunNetworkAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var options = new GitProcessOptions
            {
                Timeout = NetworkTimeout,
                OnStdoutLine = line => progress?.Report(line),
                OnStderrLine = line => progress?.Report(line),
            };
            await runner.RunAsync(
                repositoryPath,
                ["push", "--progress", "origin", $"refs/tags/{name}"],
                options,
                token).ConfigureAwait(false);
            GitDeltaMeters.PushMs.Record(sw.Elapsed.TotalMilliseconds);
        }, ct);
    }

    public Task PushAllTagsAsync(
        string repositoryPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        gates.For(repositoryPath).RunNetworkAsync(async token =>
        {
            var sw = Stopwatch.StartNew();
            var options = new GitProcessOptions
            {
                Timeout = NetworkTimeout,
                OnStdoutLine = line => progress?.Report(line),
                OnStderrLine = line => progress?.Report(line),
            };
            await runner.RunAsync(
                repositoryPath,
                ["push", "--progress", "origin", "--tags"],
                options,
                token).ConfigureAwait(false);
            GitDeltaMeters.PushMs.Record(sw.Elapsed.TotalMilliseconds);
        }, ct);

    internal static List<TagInfo> ParseTags(string rawOutput)
    {
        var tags = new List<TagInfo>();
        foreach (var line in rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(FieldSeparator);
            if (fields.Length < 3)
                continue;

            var name = fields[0];
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var date = DateTimeOffset.MinValue;
            if (!string.IsNullOrWhiteSpace(fields[1])
                && DateTimeOffset.TryParse(fields[1], out var parsed))
            {
                date = parsed;
            }

            var targetOid = fields[2];
            string? message = null;
            if (fields.Length >= 4 && !string.IsNullOrWhiteSpace(fields[3]))
                message = fields[3];

            tags.Add(new TagInfo(name, date, targetOid, message));
        }

        return tags;
    }
}
