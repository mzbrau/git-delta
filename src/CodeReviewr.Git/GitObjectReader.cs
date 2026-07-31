using System.Globalization;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using CodeReviewr.Git.Internal;

namespace CodeReviewr.Git;

/// <summary>
/// Reads blob content through a long-lived `git cat-file --batch` process, which amortises
/// process-spawn cost across many object reads (syntax highlighting fetches whole-file blobs
/// for both diff sides). Requests are serialised: `--batch` is a strict request/response
/// protocol over a single pair of pipes, so concurrent callers queue behind one another.
/// </summary>
public sealed class GitObjectReader(IGitProcessRunner runner) : IGitObjectReader
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private ILongLivedGitProcess? _process;
    private string? _repositoryPath;

    public async Task<byte[]> ReadBlobAsync(string repositoryPath, ContentId oid, CancellationToken ct = default)
    {
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var process = await EnsureStartedAsync(repositoryPath).ConfigureAwait(false);

            await GitBatchProtocol.WriteRequestLineAsync(process.StandardInput, oid.Value, ct).ConfigureAwait(false);

            var header = await GitBatchProtocol.ReadHeaderLineAsync(process.StandardOutput, ct).ConfigureAwait(false)
                ?? throw new GitException("git cat-file --batch ended unexpectedly while reading an object header.");

            var parts = header.Split(' ');
            if (parts.Length == 2 && parts[1] == "missing")
                throw new GitException($"Object '{oid}' was not found in the object database.");

            if (parts.Length < 3)
                throw new GitException($"Unexpected `git cat-file --batch` header: '{header}'.");

            var size = long.Parse(parts[2], CultureInfo.InvariantCulture);
            var buffer = new byte[size];
            await GitBatchProtocol.ReadExactAsync(process.StandardOutput, buffer, ct).ConfigureAwait(false);

            // The payload is followed by a single trailing newline before the next response.
            var trailer = new byte[1];
            await GitBatchProtocol.ReadExactAsync(process.StandardOutput, trailer, ct).ConfigureAwait(false);

            CodeReviewrMeters.GitBytesRead.Add(size);
            return buffer;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task<ContentId> HashObjectAsync(string repositoryPath, string filePath, bool write, CancellationToken ct = default)
    {
        var args = new List<string> { "hash-object" };
        if (write)
            args.Add("-w");
        args.Add("--");
        args.Add(filePath);

        var result = await runner.RunAsync(repositoryPath, args, options: null, ct).ConfigureAwait(false);
        return ContentId.FromSha(result.Stdout.Trim());
    }

    private async Task<ILongLivedGitProcess> EnsureStartedAsync(string repositoryPath)
    {
        if (_process is { HasExited: false } && string.Equals(_repositoryPath, repositoryPath, StringComparison.Ordinal))
            return _process;

        if (_process is not null)
            await _process.DisposeAsync().ConfigureAwait(false);

        _process = runner.StartLongLived(repositoryPath, ["cat-file", "--batch"]);
        _repositoryPath = repositoryPath;
        return _process;
    }

    public async ValueTask DisposeAsync()
    {
        await _requestLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not null)
            {
                await _process.DisposeAsync().ConfigureAwait(false);
                _process = null;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }
}
