using System.Text.RegularExpressions;

namespace CodeReviewr.Core;

/// <summary>Extracts a ticket/issue key from a Git branch name using a configurable regex.</summary>
public static class TicketFromBranch
{
    /// <summary>
    /// Default pattern matching keys like <c>SMITH-123</c> in branch names such as
    /// <c>bugfix/SMITH-123/3</c>. Capture group 1 is the ticket; if unnamed, the first group is used.
    /// </summary>
    public const string DefaultRegex = @"(?i)(?:^|/)([A-Z][A-Z0-9]+-\d+)(?:/|$)";

    /// <summary>
    /// Tries to extract a ticket from <paramref name="branchName"/> using <paramref name="pattern"/>.
    /// Returns false when the pattern is empty/invalid or no match is found.
    /// </summary>
    public static bool TryExtract(string? branchName, string? pattern, out string ticket, out string? error)
    {
        ticket = "";
        error = null;

        if (string.IsNullOrWhiteSpace(branchName))
        {
            error = "No current branch.";
            return false;
        }

        var regexPattern = string.IsNullOrWhiteSpace(pattern) ? DefaultRegex : pattern.Trim();
        Regex regex;
        try
        {
            regex = new Regex(regexPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            error = $"Invalid ticket regex: {ex.Message}";
            return false;
        }

        Match match;
        try
        {
            match = regex.Match(branchName);
        }
        catch (RegexMatchTimeoutException)
        {
            error = "Ticket regex timed out.";
            return false;
        }

        if (!match.Success)
        {
            error = "No ticket found in branch name.";
            return false;
        }

        if (match.Groups.Count > 1 && match.Groups[1].Success)
            ticket = match.Groups[1].Value;
        else
            ticket = match.Value;

        if (string.IsNullOrWhiteSpace(ticket))
        {
            error = "No ticket found in branch name.";
            ticket = "";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Prepends <paramref name="ticket"/> to <paramref name="message"/> when not already present.
    /// </summary>
    public static string PrependTicket(string? message, string ticket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);
        var existing = message ?? "";
        if (existing.Contains(ticket, StringComparison.OrdinalIgnoreCase))
            return existing;

        if (string.IsNullOrWhiteSpace(existing))
            return ticket;

        return $"{ticket} {existing.TrimStart()}";
    }
}
