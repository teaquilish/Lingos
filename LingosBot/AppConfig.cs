using System.Text.RegularExpressions;

namespace LingosBotApp;

internal sealed class AppConfig
{
    public string BaseUrl { get; } = "https://lingos.pl";

    public string LoginUrl { get; } = "https://lingos.pl/h/login";

    public string StudentDashboardUrl { get; } = "https://lingos.pl/student/dashboard";

    public string WordsetsUrl { get; } = "https://lingos.pl/student/wordsets";

    /// <summary>Path of one wordset's page; <c>{0}</c> is the wordset id.</summary>
    public string WordsetPathFormat { get; } = "/student/wordsets/{0}";

    /// <summary>Endpoint that answers one lesson exercise; <c>{0}</c> is the lesson id.</summary>
    public string LearningAnswerPathFormat { get; } = "/learning/answer/{0}";

    /// <summary>Endpoint that joins a challenge; <c>{0}</c> is the challenge id.</summary>
    public string ChallengeJoinPathFormat { get; } = "/student/challenges/{0}";

    /// <summary>Endpoint that selects the class (or course) the dashboard and lessons use.</summary>
    public string ContextSwitchPath { get; } = "/student/context";

    public string CredentialFilePath { get; } = Path.Combine(AppContext.BaseDirectory, "credentials.json");

    public string SecondaryCredentialFilePath { get; } = Path.Combine(AppContext.BaseDirectory, "credentials-2.json");

    public string? ChromeBinaryPath { get; } = Environment.GetEnvironmentVariable("LINGOS_CHROME_BINARY");

    public TimeSpan DefaultWaitTimeout { get; } = TimeSpan.FromSeconds(15);

    public TimeSpan ShortWaitTimeout { get; } = TimeSpan.FromSeconds(4);

    public TimeSpan LessonRestartReuseTimeout { get; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan PageLoadTimeout { get; } = TimeSpan.FromSeconds(60);

    public TimeSpan ScriptTimeout { get; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollingInterval { get; } = TimeSpan.FromMilliseconds(25);

    public int MinLessonCount { get; } = 1;

    public int LessonsPerClass { get; } = 5;

    /// <summary>
    /// Most answers one lesson may take before the bot assumes it is stuck. A lesson is about 20
    /// exercises, plus a card for every new word and every answer Lingos asks again.
    /// </summary>
    public int LessonPromptSafetyCap { get; } = 100;
}

internal static class TextNormalizer
{
    private const char NonBreakingSpace = (char)0x00A0;

    private static readonly Regex MultipleWhitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value.Replace(NonBreakingSpace, ' ').Trim();
        return MultipleWhitespace.Replace(sanitized, " ");
    }
}
