using System.Collections.Concurrent;
using CodeReviewr.Core;
using CodeReviewr.Core.Abstractions;
using CodeReviewr.Core.Diagnostics;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace CodeReviewr.Diff;

/// <summary>A single tokenised span within one line, e.g. an identifier, keyword, or string literal.</summary>
public readonly record struct SyntaxSpan(int Start, int Length, string Scope);

/// <summary>The tokens produced for one (one-based) line of a tokenised file.</summary>
public sealed record LineTokens(int LineNumber, IReadOnlyList<SyntaxSpan> Spans);

/// <summary>
/// Whole-file tokenisation result, cached by content identity. Callers map spans onto a
/// <see cref="Core.Diff.DiffLine"/> by looking up <see cref="Core.Diff.DiffLine.OldLine"/> or
/// <see cref="Core.Diff.DiffLine.NewLine"/> (matching whichever side <see cref="Content"/> is).
/// </summary>
public sealed class FileSyntaxTokens
{
    private readonly Dictionary<int, IReadOnlyList<SyntaxSpan>> _byLine;

    internal FileSyntaxTokens(ContentId content, string grammarScope, IReadOnlyList<LineTokens> lines)
    {
        Content = content;
        GrammarScope = grammarScope;
        Lines = lines;
        _byLine = new Dictionary<int, IReadOnlyList<SyntaxSpan>>(lines.Count);
        foreach (var line in lines)
            _byLine[line.LineNumber] = line.Spans;
    }

    public ContentId Content { get; }
    public string GrammarScope { get; }
    public IReadOnlyList<LineTokens> Lines { get; }

    /// <summary>Spans for a one-based line number, or empty if the line has none (or is out of range).</summary>
    public IReadOnlyList<SyntaxSpan> ForLine(int oneBasedLineNumber) =>
        _byLine.TryGetValue(oneBasedLineNumber, out var spans) ? spans : Array.Empty<SyntaxSpan>();
}

public interface ISyntaxTokenService
{
    /// <summary>
    /// Tokenises whole file content, never just the visible hunks — TextMate grammars are stateful
    /// line to line, so a hunk beginning inside a block comment or multi-line string would highlight
    /// incorrectly if tokenised alone. Returns <see langword="null"/> when no grammar is known for
    /// the path's extension, or when the content exceeds the configured size cap.
    /// </summary>
    ValueTask<FileSyntaxTokens?> TokeniseAsync(
        ContentId content,
        FilePath path,
        string text,
        CancellationToken ct = default);
}

/// <summary>
/// Syntax tokenisation backed by TextMateSharp. Tokens are cached by (<see cref="ContentId"/>,
/// grammar scope), which is exactly the cache key Plan.md specifies for syntax highlighting, so
/// tokens survive a view-mode switch and never need invalidating — they simply stop being requested.
/// </summary>
public sealed class SyntaxTokenService : ISyntaxTokenService
{
    private const int DefaultSizeCapBytes = 1_000_000;
    private const int DefaultLineLengthCap = 10_000;
    private static readonly TimeSpan PerLineTimeout = TimeSpan.FromSeconds(1);

    private readonly RegistryOptions _registryOptions;
    private readonly Registry _registry;
    private readonly ISettingsStore? _settingsStore;
    private readonly ConcurrentDictionary<string, IGrammar?> _grammarsByScope = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(ContentId Content, string Scope), FileSyntaxTokens> _cache = new();

    public SyntaxTokenService(ISettingsStore? settingsStore = null, ThemeName theme = ThemeName.DarkPlus)
    {
        _settingsStore = settingsStore;
        _registryOptions = new RegistryOptions(theme);
        _registry = new Registry(_registryOptions);
    }

    public ValueTask<FileSyntaxTokens?> TokeniseAsync(ContentId content, FilePath path, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        var settings = _settingsStore?.Current;
        var sizeCap = settings?.SyntaxHighlightingSizeCapBytes ?? DefaultSizeCapBytes;
        var lineLengthCap = settings?.SyntaxHighlightingLineLengthCap ?? DefaultLineLengthCap;

        if (text.Length > sizeCap)
            return ValueTask.FromResult<FileSyntaxTokens?>(null);

        var scope = ResolveScope(path);
        if (scope is null)
            return ValueTask.FromResult<FileSyntaxTokens?>(null);

        var cacheKey = (content, scope);
        if (!content.IsEmpty && _cache.TryGetValue(cacheKey, out var cached))
        {
            CodeReviewrMeters.CacheHits.Add(1);
            return ValueTask.FromResult<FileSyntaxTokens?>(cached);
        }

        CodeReviewrMeters.CacheMisses.Add(1);

        var grammar = _grammarsByScope.GetOrAdd(scope, LoadGrammarSafe);
        if (grammar is null)
            return ValueTask.FromResult<FileSyntaxTokens?>(null);

        var result = Tokenise(content, scope, grammar, text, lineLengthCap);
        if (!content.IsEmpty)
            _cache[cacheKey] = result;

        return ValueTask.FromResult<FileSyntaxTokens?>(result);
    }

    private IGrammar? LoadGrammarSafe(string scope)
    {
        try
        {
            return _registry.LoadGrammar(scope);
        }
        catch
        {
            return null;
        }
    }

    private static FileSyntaxTokens Tokenise(ContentId content, string scope, IGrammar grammar, string text, int lineLengthCap)
    {
        var lines = new List<LineTokens>();
        IStateStack? ruleStack = null;
        var lineNumber = 0;
        var pos = 0;
        var tokenisedLines = 0L;

        while (pos < text.Length)
        {
            var newlineIdx = text.IndexOf('\n', pos);
            var lineEnd = newlineIdx == -1 ? text.Length : newlineIdx;
            var lineSpan = text.AsSpan(pos, lineEnd - pos);
            if (lineSpan.Length > 0 && lineSpan[^1] == '\r')
                lineSpan = lineSpan[..^1];

            lineNumber++;
            var lineText = lineSpan.ToString();

            if (lineText.Length <= lineLengthCap)
            {
                var result = grammar.TokenizeLine(lineText, ruleStack, PerLineTimeout);
                ruleStack = result.RuleStack;

                var spans = new List<SyntaxSpan>(result.Tokens.Length);
                foreach (var token in result.Tokens)
                {
                    var start = Math.Min(token.StartIndex, lineText.Length);
                    var end = Math.Min(token.EndIndex, lineText.Length);
                    if (end <= start)
                        continue;

                    // Prefer the most specific non-language-root scope so painters can distinguish
                    // keywords/strings/comments from the bare "source.cs" grammar root.
                    var scopeName = PickScope(token.Scopes);
                    spans.Add(new SyntaxSpan(start, end - start, scopeName));
                }

                lines.Add(new LineTokens(lineNumber, spans));
                tokenisedLines++;
            }
            else
            {
                // Above the per-line length cap: skip highlighting for this line but keep the rule
                // stack unaffected so subsequent lines are unaffected by the skip.
                lines.Add(new LineTokens(lineNumber, Array.Empty<SyntaxSpan>()));
            }

            pos = newlineIdx == -1 ? text.Length : newlineIdx + 1;
        }

        if (tokenisedLines > 0)
            CodeReviewrMeters.LinesTokenised.Add(tokenisedLines);

        return new FileSyntaxTokens(content, scope, lines);
    }

    private string? ResolveScope(FilePath path)
    {
        var ext = Path.GetExtension(path.Value);
        if (string.IsNullOrEmpty(ext))
            return null;

        try
        {
            return _registryOptions.GetScopeByExtension(ext);
        }
        catch
        {
            return null;
        }
    }

    private static string PickScope(IList<string> scopes)
    {
        if (scopes.Count == 0) return string.Empty;
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            var s = scopes[i];
            if (string.IsNullOrEmpty(s)) continue;
            // Skip grammar roots like "source.cs" / "source.js".
            if (s.StartsWith("source.", StringComparison.Ordinal) && s.IndexOf('.', 7) < 0)
                continue;
            return s;
        }

        return scopes[^1];
    }
}
