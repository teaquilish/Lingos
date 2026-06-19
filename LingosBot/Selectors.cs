using OpenQA.Selenium;

namespace LingosBotApp;

internal static class Selectors
{
    // Verified from https://lingos.pl/h/login on 2026-03-21 where possible.
    // The remaining TODO values are post-login selectors that still need to be inspected manually.
    public static SelectorDefinition CookieAcceptButton { get; } = SelectorDefinition.CssRequired(
        "CookieAcceptButton",
        "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll, #CybotCookiebotDialogBodyButtonAccept, #CybotCookiebotDialogBodyLevelButtonAccept");

    public static SelectorDefinition LoginEmailInput { get; } = SelectorDefinition.CssRequired(
        "LoginEmailInput",
        "form#login-form input[name='login']");

    public static SelectorDefinition LoginPasswordInput { get; } = SelectorDefinition.CssRequired(
        "LoginPasswordInput",
        "form#login-form input[name='password']");

    public static SelectorDefinition LoginSubmitButton { get; } = SelectorDefinition.CssRequired(
        "LoginSubmitButton",
        "#submit-login-button");

    public static SelectorDefinition AuthenticatedShellMarker { get; } = SelectorDefinition.CssOptional(
        "AuthenticatedShellMarker",
        "TODO: optional selector visible only after a successful login");

    public static SelectorDefinition LeftMenuZestawyButton { get; } = SelectorDefinition.CssRequired(
        "LeftMenuZestawyButton",
        "#main-menu-list a[href='/student-confirmed/wordsets'], a#menu-small-item-icon-1[href='/student-confirmed/wordsets']");

    public static SelectorDefinition ZestawyPageMarker { get; } = SelectorDefinition.CssOptional(
        "ZestawyPageMarker",
        "TODO: optional selector that confirms the Zestawy page is open");

    public static SelectorDefinition SetCardContainer { get; } = SelectorDefinition.CssRequired(
        "SetCardContainer",
        ".card.rounded-3.p-3.p-sm-4.nav");

    public static SelectorDefinition SetCardPreviewButton { get; } = SelectorDefinition.CssRequired(
        "SetCardPreviewButton",
        "a.btn[href*='/student-confirmed/wordset/']");

    public static SelectorDefinition SetPreviewContainer { get; } = SelectorDefinition.CssOptional(
        "SetPreviewContainer",
        "TODO: optional selector for a preview container if Lingos switches from page navigation to a modal");

    public static SelectorDefinition PreviewVocabularyRow { get; } = SelectorDefinition.CssRequired(
        "PreviewVocabularyRow",
        ".card.rounded-3.p-3.nav.text-dark");

    public static SelectorDefinition PreviewForeignWordCell { get; } = SelectorDefinition.CssRequired(
        "PreviewForeignWordCell",
        ".flashcard-border-end");

    public static SelectorDefinition PreviewPolishWordCell { get; } = SelectorDefinition.CssRequired(
        "PreviewPolishWordCell",
        ".flashcard-border-start");

    public static SelectorDefinition PreviewCloseButton { get; } = SelectorDefinition.CssOptional(
        "PreviewCloseButton",
        "TODO: optional selector for a preview close/back button when Podgląd opens a modal");

    public static SelectorDefinition MainPageMarker { get; } = SelectorDefinition.CssRequired(
        "MainPageMarker",
        "a[href='/student-confirmed/group'].active, #main-menu-list a[href='/student-confirmed/group']");

    public static SelectorDefinition MainLearnButton { get; } = SelectorDefinition.CssRequired(
        "MainLearnButton",
        "a.btn.btn-primary[href^='/s/lesson/']");

    public static SelectorDefinition LessonPrompt { get; } = SelectorDefinition.CssRequired(
        "LessonPrompt",
        "#flashcard_main_text");

    public static SelectorDefinition LessonAnswerInput { get; } = SelectorDefinition.CssRequired(
        "LessonAnswerInput",
        "#flashcard_answer_input");

    public static SelectorDefinition LessonFeedbackMarker { get; } = SelectorDefinition.CssRequired(
        "LessonFeedbackMarker",
        "#flashcard_error_div");

    public static SelectorDefinition LessonContinueButton { get; } = SelectorDefinition.CssRequired(
        "LessonContinueButton",
        "#enterBtn");

    public static SelectorDefinition LessonProgressCounter { get; } = SelectorDefinition.CssRequired(
        "LessonProgressCounter",
        "#progress_counter");

    public static SelectorDefinition LessonLimitOfferButton { get; } = SelectorDefinition.CssRequired(
        "LessonLimitOfferButton",
        "#loaded_btn a[href='/student-confirmed/premium-buy']");

    public static SelectorDefinition LessonIncorrectAnswerMarker { get; } = SelectorDefinition.CssRequired(
        "LessonIncorrectAnswerMarker",
        "#flashcard_error_text");

    public static SelectorDefinition LessonFinishedMarker { get; } = SelectorDefinition.CssOptional(
        "LessonFinishedMarker",
        "TODO: optional selector that appears when the lesson is finished");

    public static SelectorDefinition ZestawyNextPageButton { get; } = SelectorDefinition.CssOptional(
        "ZestawyNextPageButton",
        "TODO: optional selector for the next-page button on the Zestawy page");

    // --- Wyzwania (challenges) ------------------------------------------
    // The challenges live in a Bootstrap modal (#wyzwaniaModal) that is
    // server-rendered into the dashboard page, so the cards can be read
    // directly without opening the modal. Each card is a .bg-secondary block
    // holding the title, "Nagroda: X pkt." and either a join link
    // (a[href*='/students/challenge/']) or a "Gratulacje!" completion notice.
    public static SelectorDefinition ChallengeCard { get; } = SelectorDefinition.CssRequired(
        "ChallengeCard",
        "#wyzwaniaModal .bg-secondary");

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
