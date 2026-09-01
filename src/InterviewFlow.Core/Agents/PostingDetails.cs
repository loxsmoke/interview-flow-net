namespace InterviewFlow.Core.Agents;

/// <summary>
/// A posting recovered from a structured source (docs/05 §5.7). Workday CXS and
/// schema.org JSON-LD both name the role and the employer, so Setup can fill
/// Company/Position from a fetched URL instead of making the user retype them.
/// <paramref name="Title"/> and <paramref name="Company"/> are "" when the
/// source didn't carry them.
/// </summary>
public sealed record PostingDetails(string Text, string Title = "", string Company = "")
{
    public static readonly PostingDetails Empty = new("");

    public bool IsEmpty => Text.Length == 0;
}
