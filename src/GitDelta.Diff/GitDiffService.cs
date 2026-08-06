using System.Diagnostics;
using GitDelta.Core;
using GitDelta.Core.Abstractions;
using GitDelta.Core.Diagnostics;
using GitDelta.Core.Diff;

namespace GitDelta.Diff;

/// <summary>
/// Implements <see cref="IGitDiffService"/> on top of the raw <see cref="IGitDiffRawService"/>,
/// <see cref="PatchParser"/>, and the content-addressed <see cref="IDiffCache"/>.
///
/// Content identity for the cache key comes from the raw diff itself: the patch's own
/// <c>index &lt;old&gt;..&lt;new&gt;</c> header line, extracted by <see cref="PatchParser"/>. Because the
/// cache is keyed purely by (old content, new content, options), requests for different
/// <see cref="DiffScope"/>s that happen to describe identical content collapse onto the same cache
/// entry for free, exactly as Plan.md's "Target and caching" section describes.
/// </summary>
public sealed class GitDiffService : IGitDiffService
{
    private readonly IGitDiffRawService _rawService;
    private readonly IDiffCache _cache;
    private readonly IIntraLineDiffer? _intraLineDiffer;

    public GitDiffService(IGitDiffRawService rawService, IDiffCache cache, IIntraLineDiffer? intraLineDiffer = null)
    {
        _rawService = rawService;
        _cache = cache;
        _intraLineDiffer = intraLineDiffer;
    }

    public async Task<FileDiff> GetDiffAsync(
        string repositoryPath,
        FilePath path,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default)
    {
        using var activity = GitDeltaActivity.Source.StartActivity("git.diff");
        activity?.SetTag("diff.path", path.Value);
        activity?.SetTag("diff.scope", scope.ToString());

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var rawPatch = await _rawService.GetPatchAsync(repositoryPath, path, scope, options, ct).ConfigureAwait(false);
            var parsed = PatchParser.Parse(rawPatch, scope);

            var key = new FileDiffKey(parsed.OldContent, parsed.NewContent, options);
            if (_cache.TryGet(key, out var cached) && cached is not null)
            {
                activity?.SetTag("diff.cache_hit", true);
                return cached;
            }

            activity?.SetTag("diff.cache_hit", false);
            var enriched = _intraLineDiffer is null ? parsed : IntraLineEnricher.Enrich(parsed, _intraLineDiffer);
            _cache.Set(key, enriched);
            return enriched;
        }
        finally
        {
            GitDeltaMeters.DiffGenerationMs.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    public Task<IReadOnlyList<(FilePath Path, ContentId OldOid, ContentId NewOid, ChangeKind Kind)>> GetRawDiffAsync(
        string repositoryPath,
        DiffScope scope,
        DiffOptions options,
        CancellationToken ct = default) =>
        _rawService.GetRawFileListAsync(repositoryPath, scope, options, ct);
}
