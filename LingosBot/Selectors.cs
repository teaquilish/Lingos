using OpenQA.Selenium;

namespace LingosBotApp;

/// <summary>
/// CSS selectors for the lingos.pl student app. The bot reads most of its state from the JSON
/// the server renders into <c>#app[data-props]</c>; these selectors cover logging in, moving
/// around the site, and the DOM fallbacks used when that JSON cannot be read.
/// </summary>
internal static class Selectors
{
    public static SelectorDefinition CookieAcceptButton { get; } = SelectorDefinition.CssRequired(
        nameof(CookieAcceptButton),
        "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll, #CybotCookiebotDialogBodyButtonAccept, #CybotCookiebotDialogBodyLevelButtonAccept");

    public static SelectorDefinition LoginEmailInput { get; } = SelectorDefinition.CssRequired(
        nameof(LoginEmailInput), "form#login-form input[name='_username']");

    public static SelectorDefinition LoginPasswordInput { get; } = SelectorDefinition.CssRequired(
        nameof(LoginPasswordInput), "form#login-form input[name='_password']");

    public static SelectorDefinition LoginSubmitButton { get; } = SelectorDefinition.CssRequired(
        nameof(LoginSubmitButton), "#submit-login-button");

    /// <summary>Part of the signed-in shell; its presence means the login went through.</summary>
    public static SelectorDefinition AuthenticatedShellMarker { get; } = SelectorDefinition.CssOptional(
        nameof(AuthenticatedShellMarker), "nav a[href='/student/dashboard']");

    public static SelectorDefinition LeftMenuZestawyButton { get; } = SelectorDefinition.CssRequired(
        nameof(LeftMenuZestawyButton), "nav a[href='/student/wordsets']");

    public static SelectorDefinition SetCardContainer { get; } = SelectorDefinition.CssRequired(
        nameof(SetCardContainer), "main li:has(> div a[href^='/student/wordsets/'])");

    public static SelectorDefinition SetCardPreviewButton { get; } = SelectorDefinition.CssRequired(
        nameof(SetCardPreviewButton), "a[href^='/student/wordsets/']");

    public static SelectorDefinition PreviewVocabularyRow { get; } = SelectorDefinition.CssRequired(
        nameof(PreviewVocabularyRow), "main li > div.grid");

    public static SelectorDefinition PreviewForeignWordCell { get; } = SelectorDefinition.CssRequired(
        nameof(PreviewForeignWordCell), "p:first-of-type");

    public static SelectorDefinition PreviewPolishWordCell { get; } = SelectorDefinition.CssRequired(
        nameof(PreviewPolishWordCell), "p:last-of-type");

    /// <summary>The "Ucz sie" link on the dashboard. It is absent once the daily limit is spent.</summary>
    public static SelectorDefinition MainLearnButton { get; } = SelectorDefinition.CssRequired(
        nameof(MainLearnButton), "main a[href^='/learning/start/']");

    public static SelectorDefinition LessonPrompt { get; } = SelectorDefinition.CssRequired(
        nameof(LessonPrompt), "main p.text-2xl strong");

    public static SelectorDefinition LessonAnswerInput { get; } = SelectorDefinition.CssRequired(
        nameof(LessonAnswerInput), "#learning-answer");

    /// <summary>The "POPRAWNA ODPOWIEDZ" banner shown after an accepted answer.</summary>
    public static SelectorDefinition LessonFeedbackMarker { get; } = SelectorDefinition.CssRequired(
        nameof(LessonFeedbackMarker), "main p.text-brand svg.lucide-check");

    /// <summary>The "BLEDNA ODPOWIEDZ" banner shown after a rejected answer.</summary>
    public static SelectorDefinition LessonIncorrectAnswerMarker { get; } = SelectorDefinition.CssRequired(
        nameof(LessonIncorrectAnswerMarker), "main p.text-red-700");

    /// <summary>The "Dalej [Enter]" button, which submits an answer and also steps past feedback.</summary>
    public static SelectorDefinition LessonContinueButton { get; } = SelectorDefinition.CssRequired(
        nameof(LessonContinueButton), "main button.bg-brand.text-white");

    public static SelectorDefinition LessonFinishedMarker { get; } = SelectorDefinition.CssOptional(
        nameof(LessonFinishedMarker), "TODO: the app redirects away from /learning/ when a lesson ends");
}

internal enum SelectorStrategy
{
    Css,
    XPath
}

internal sealed class SelectorDefinition
{
    private SelectorDefinition(string name, SelectorStrategy strategy, string value, bool optional)
    {
        Name = name;
        Strategy = strategy;
        Value = value;
        Optional = optional;
    }

    public string Name { get; }

    public SelectorStrategy Strategy { get; }

    public string Value { get; }

    public bool Optional { get; }

    public bool IsPlaceholder => Value.StartsWith("TODO", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Value) && !IsPlaceholder;

    public static SelectorDefinition CssRequired(string name, string value) => new(name, SelectorStrategy.Css, value, optional: false);

    public static SelectorDefinition CssOptional(string name, string value) => new(name, SelectorStrategy.Css, value, optional: true);

    public static SelectorDefinition XPathRequired(string name, string value) => new(name, SelectorStrategy.XPath, value, optional: false);

    public static SelectorDefinition XPathOptional(string name, string value) => new(name, SelectorStrategy.XPath, value, optional: true);

    public By ToBy()
    {
        if (!IsConfigured)
        {
            var mode = Optional ? "optional" : "required";
            throw new InvalidOperationException(
                $"The {mode} selector '{Name}' in Selectors.cs still contains a TODO placeholder. Replace it before the bot can use that part of the site.");
        }

        return Strategy switch
        {
            SelectorStrategy.Css => By.CssSelector(Value),
            SelectorStrategy.XPath => By.XPath(Value),
            _ => throw new InvalidOperationException($"Unsupported selector strategy '{Strategy}'.")
        };
    }

    public bool TryToBy(out By? by)
    {
        if (!IsConfigured)
        {
            by = null;
            return false;
        }

        by = Strategy switch
        {
            SelectorStrategy.Css => By.CssSelector(Value),
            SelectorStrategy.XPath => By.XPath(Value),
            _ => throw new InvalidOperationException($"Unsupported selector strategy '{Strategy}'.")
        };

        return true;
    }
}
