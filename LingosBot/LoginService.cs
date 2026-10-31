using OpenQA.Selenium;

namespace LingosBotApp;

internal sealed class LoginService
{
    private readonly PageAgent _page;
    private readonly AppConfig _config;

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
        WaitOutMaintenance();
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
                $"Login did not go through: after submitting the form the browser stayed at {_page.Url}. Usually the email or "
                + "password is wrong; if they are right, Lingos may have changed its login page (see Selectors.cs).",
                ex);
        }
    }

    private void WaitForSuccessfulLogin()
        => _page.Wait().Until(driver => !_page.UrlContains("/h/login")
            || _page.TryFindVisible(Selectors.AuthenticatedShellMarker, out _));

    /// <summary>
    /// Reloads the login page until lingos.pl stops showing its "Under maintenance" page, so a
    /// scheduled run still goes through when it starts during a short maintenance window.
    /// </summary>
    private void WaitOutMaintenance()
    {
        var waited = TimeSpan.Zero;
        while (_page.IsMaintenancePage())
        {
            if (waited >= _config.MaintenanceWaitLimit)
            {
                throw new SiteUnavailableException(
                    $"lingos.pl is still showing its 'Under maintenance' page after {waited.TotalMinutes:0} minute(s). Try again later.");
            }

            Console.WriteLine($"lingos.pl is under maintenance. Checking again in {_config.MaintenanceRetryInterval.TotalMinutes:0} minute(s)...");
            Thread.Sleep(_config.MaintenanceRetryInterval);
            waited += _config.MaintenanceRetryInterval;
            _page.GoTo(_config.LoginUrl);
            _page.WaitForDocumentReady();
        }

        if (waited > TimeSpan.Zero)
        {
            Console.WriteLine("lingos.pl is back from maintenance.");
        }
    }

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

internal sealed class SiteUnavailableException : Exception
{
    public SiteUnavailableException(string message)
        : base(message)
    {
    }
}
