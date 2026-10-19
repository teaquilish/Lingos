using OpenQA.Selenium;

namespace LingosBotApp;

/// <summary>
/// CSS selectors for the lingos.pl pages the bot clicks through. Everything else - classes,
/// wordsets, challenges and lessons - is read from the JSON the server renders into
/// <c>#app[data-props]</c>, so only logging in and moving around the site need selectors.
/// </summary>
internal static class Selectors
{
    public static SelectorDefinition CookieAcceptButton { get; } = new(
        nameof(CookieAcceptButton),
        "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll, #CybotCookiebotDialogBodyButtonAccept, #CybotCookiebotDialogBodyLevelButtonAccept");

    public static SelectorDefinition LoginEmailInput { get; } = new(
        nameof(LoginEmailInput), "form#login-form input[name='_username']");

    public static SelectorDefinition LoginPasswordInput { get; } = new(
        nameof(LoginPasswordInput), "form#login-form input[name='_password']");

    public static SelectorDefinition LoginSubmitButton { get; } = new(
        nameof(LoginSubmitButton), "#submit-login-button");

    /// <summary>Part of the signed-in shell; its presence means the login went through.</summary>
    public static SelectorDefinition AuthenticatedShellMarker { get; } = new(
        nameof(AuthenticatedShellMarker), "nav a[href='/student/dashboard']");

    public static SelectorDefinition LeftMenuZestawyButton { get; } = new(
        nameof(LeftMenuZestawyButton), "nav a[href='/student/wordsets']");

    /// <summary>The "Ucz sie" link on the dashboard. It is absent once the daily limit is spent.</summary>
    public static SelectorDefinition MainLearnButton { get; } = new(
        nameof(MainLearnButton), "main a[href^='/learning/start/']");
}

/// <summary>A named CSS selector; the name is what error messages show.</summary>
internal sealed record SelectorDefinition(string Name, string Value)
{
    public By ToBy() => By.CssSelector(Value);
}
