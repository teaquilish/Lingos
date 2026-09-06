using OpenQA.Selenium;

namespace LingosBotApp;

internal sealed class LoginService
{
    private readonly PageAgent _page;
    private readonly AppConfig _config;

    public LoginService(IWebDriver driver, AppConfig config)
        : this(new PageAgent(driver, config))
    {
    }

    public LoginService(PageAgent page)
    {
        _page = page;
        _config = page.Config;
    }

    public void Login(AppCredentials credentials)
    {
        Console.WriteLine("Opening lingos.pl...");
        _page.GoTo(_config.LoginUrl);
        _page.WaitForDocumentReady();
        HandleCookieConsent();

        Console.WriteLine("Submitting login form...");
        var email = _page.WaitUntilVisible(Selectors.LoginEmailInput);
        email.Clear();
        email.SendKeys(credentials.Email);

        var password = _page.WaitUntilVisible(Selectors.LoginPasswordInput);
        password.Clear();
        password.SendKeys(credentials.Password);

        _page.Click(_page.WaitUntilClickable(Selectors.LoginSubmitButton));

        try
        {
            WaitForSuccessfulLogin();
            Console.WriteLine("Login succeeded.");
        }
        catch (WebDriverTimeoutException ex)
        {
            throw new LoginFailedException(
                "Login failed. Check your credentials and update the login selectors in Selectors.cs if the website layout changed.",
                ex);
        }
    }

    private void WaitForSuccessfulLogin()
        => _page.Wait().Until(driver => !_page.UrlContains("/h/login")
            || _page.TryFindVisible(Selectors.AuthenticatedShellMarker, out _));

    private void HandleCookieConsent()
    {
        Console.WriteLine("Handling cookie consent...");
        if (_page.TryWaitUntilClickable(Selectors.CookieAcceptButton, _config.ShortWaitTimeout, out var accept) && accept is not null)
        {
            _page.Click(accept);
            _page.WaitForDocumentReady();
            Console.WriteLine("Cookie consent accepted.");
            return;
        }

        Console.WriteLine("Cookie consent button was not visible within the short timeout. Continuing.");
    }
}

internal sealed class LoginFailedException : Exception
{
    public LoginFailedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
