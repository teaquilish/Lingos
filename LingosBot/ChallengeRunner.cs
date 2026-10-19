using System.Globalization;
using OpenQA.Selenium;

namespace LingosBotApp;

/// <summary>
/// Reads the challenges shown on the student dashboard and joins one of them.
/// </summary>
internal sealed class ChallengeRunner
{
    private readonly PageAgent _page;
    private readonly AppConfig _config;

    public ChallengeRunner(IWebDriver driver, AppConfig config)
        : this(new PageAgent(driver, config))
    {
    }

    public ChallengeRunner(PageAgent page)
    {
        _page = page;
        _config = page.Config;
    }

    public ChallengeSnapshot ReadChallenges()
    {
        if (!_page.UrlContains("/student/dashboard"))
        {
            _page.GoTo(_config.StudentDashboardUrl);
        }

        var challenges = _page.ReadPropsWhenReady<StudentProps>("the dashboard").Dashboard?.Challenges;
        var infos = challenges?
            .Select(challenge => new ChallengeInfo(
                challenge.Id,
                TextNormalizer.Normalize(challenge.Title),
                challenge.Prize,
                challenge.Status))
            .ToList() ?? new List<ChallengeInfo>();

        return new ChallengeSnapshot(infos);
    }

    public void Join(ChallengeInfo challenge)
    {
        var url = string.Format(CultureInfo.InvariantCulture, _config.ChallengeJoinPathFormat, challenge.Id);
        var result = _page.PostJson<DashboardProps>(url, null);
        if (result.Value is null)
        {
            throw new InvalidOperationException($"Lingos did not accept joining the challenge '{challenge.Title}': {result.Failure}");
        }
    }
}

/// <summary>One challenge from the dashboard. <paramref name="Status"/> is the site's own value.</summary>
internal sealed record ChallengeInfo(int Id, string Title, int Points, string Status)
{
    public const string StatusAvailable = "available";
    public const string StatusInProgress = "in_progress";
    public const string StatusCompleted = "completed";

    public bool IsAvailable => string.Equals(Status, StatusAvailable, StringComparison.Ordinal);

    public bool IsActive => string.Equals(Status, StatusInProgress, StringComparison.Ordinal);

    public bool Completed => string.Equals(Status, StatusCompleted, StringComparison.Ordinal);
}

internal sealed class ChallengeSnapshot
{
    public ChallengeSnapshot(IReadOnlyList<ChallengeInfo> challenges)
    {
        Challenges = challenges;
    }

    public IReadOnlyList<ChallengeInfo> Challenges { get; }

    public ChallengeInfo? Active => Challenges.FirstOrDefault(challenge => challenge.IsActive);

    public bool HasActive => Active is not null;

    public ChallengeInfo? BestAvailable => Challenges
        .Where(challenge => challenge.IsAvailable)
        .OrderByDescending(challenge => challenge.Points)
        .FirstOrDefault();
}
