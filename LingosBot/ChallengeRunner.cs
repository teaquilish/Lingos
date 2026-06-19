using System.Text.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace LingosBotApp;

internal sealed class ChallengeRunner
{
    private readonly IWebDriver _driver;
    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ChallengeRunner(IWebDriver driver, AppConfig config)
    {
        _driver = driver;
        _config = config;
    }

    // Reads the Wyzwania modal from the dashboard. Also saves the page each
    // time to diagnostics\wyzwania-latest.html so an in-progress challenge can
    // be inspected later (the "active" state could not be observed up front).
    public ChallengeSnapshot ReadChallenges()
    {
        _driver.Navigate().GoToUrl(_config.StudentDashboardUrl);
        WaitForDocumentReady();
        SaveSnapshot("wyzwania-latest");

        var raw = ((IJavaScriptExecutor)_driver).ExecuteScript(ParseChallengesScript, Selectors.ChallengeCard.Value)?
            .ToString() ?? "[]";

        var payload = JsonSerializer.Deserialize<List<ChallengePayload>>(raw, _jsonOptions) ?? [];

        var challenges = payload
            .Select(item => new ChallengeInfo(
                TextNormalizer.Normalize(item.Title),
                item.Points,
                item.JoinUrl.Trim(),
                item.Completed))
            .ToList();

        return new ChallengeSnapshot(challenges);
    }

    public void Join(ChallengeInfo challenge)
    {
        _driver.Navigate().GoToUrl(challenge.JoinUrl);
        WaitForDocumentReady();

        // Capture exactly what taking a challenge lands on, so we can confirm
        // whether the click enrolls or just opens a confirmation page.
        SaveSnapshot("challenge-landing");
    }

    private const string ParseChallengesScript =
        """
        const cards = Array.from(document.querySelectorAll(arguments[0]));
        return JSON.stringify(cards.map(card => {
            const text = (card.textContent || '').replace(/\s+/g, ' ').trim();
            const titleEl = card.querySelector('h5');
            const join = card.querySelector("a[href*='/students/challenge/']");
            const points = text.match(/Nagroda:\s*(\d+)\s*pkt/i);
            return {
                title: titleEl ? titleEl.textContent.replace(/\s+/g, ' ').trim() : '',
                points: points ? parseInt(points[1], 10) : 0,
                joinUrl: join ? join.href : '',
                completed: /Gratulacje/i.test(text)
            };
        }));
        """;

    private void SaveSnapshot(string name)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, $"{name}.html"), _driver.PageSource);
        }
        catch
        {
            // Best effort - never let a debug dump break the run.
        }
    }

    private void WaitForDocumentReady()
    {
        new WebDriverWait(new SystemClock(), _driver, _config.DefaultWaitTimeout, _config.PollingInterval)
            .Until(driver => string.Equals(
                ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState")?.ToString(),
                "complete",
                StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ChallengePayload
    {
        public string Title { get; set; } = string.Empty;

        public int Points { get; set; }

        public string JoinUrl { get; set; } = string.Empty;

        public bool Completed { get; set; }
    }
}

internal sealed record ChallengeInfo(string Title, int Points, string JoinUrl, bool Completed)
{
    // A challenge we can still pick has a join link and is not finished.
    public bool IsAvailable => !Completed && !string.IsNullOrWhiteSpace(JoinUrl);

    // In-progress: already taken (no join link) but not yet completed.
    public bool IsActive => !Completed && string.IsNullOrWhiteSpace(JoinUrl);
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
