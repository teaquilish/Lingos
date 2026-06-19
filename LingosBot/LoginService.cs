using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace LingosBotApp;

internal sealed class LoginService
{
    private readonly IWebDriver _driver;
    private readonly AppConfig _config;

    public LoginService(IWebDriver driver, AppConfig config)
    {
        _driver = driver;
        _config = config;
    }

    public void Login(AppCredentials credentials)
    {
        Console.WriteLine("Opening lingos.pl...");
        _driver.Navigate().GoToUrl($"{_config.BaseUrl}/h/login");
        WaitForDocumentReady();

        HandleCookieConsent();

        Console.WriteLine("Submitting login form...");

        var emailInput = WaitUntilVisible(Selectors.LoginEmailInput);
        emailInput.Clear();
        emailInput.SendKeys(credentials.Email);

        var passwordInput = WaitUntilVisible(Selectors.LoginPasswordInput);
        passwordInput.Clear();
        passwordInput.SendKeys(credentials.Password);

        var submitButton = WaitUntilClickable(Selectors.LoginSubmitButton);
        ClickElement(submitButton);

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
    {
        var loginPath = "/h/login";

        CreateWait().Until(driver =>
        {
            var currentUrl = driver.Url ?? string.Empty;
            if (!currentUrl.Contains(loginPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (Selectors.AuthenticatedShellMarker.TryToBy(out var authenticatedBy) &&
                authenticatedBy is not null &&
                TryFindVisible(driver, authenticatedBy, out _))
            {
                return true;
            }

            return false;
        });
    }

    private void HandleCookieConsent()
    {
        Console.WriteLine("Handling cookie consent...");

        try
        {
            var cookieButton = WaitUntilClickable(Selectors.CookieAcceptButton, _config.ShortWaitTimeout);
            ClickElement(cookieButton);
            WaitForDocumentReady();
            Console.WriteLine("Cookie consent accepted.");
        }
        catch (WebDriverTimeoutException)
        {
            Console.WriteLine("Cookie consent button was not visible within the short timeout. Continuing.");
        }
    }

    private IWebElement WaitUntilVisible(SelectorDefinition selector, TimeSpan? timeout = null)
    {
        var by = selector.ToBy();

        return CreateWait(timeout).Until(driver =>
        {
            try
            {
                var element = driver.FindElement(by);
                return element.Displayed ? element : null;
            }
            catch (NoSuchElementException)
            {
                return null;
            }
            catch (StaleElementReferenceException)
            {
                return null;
            }
        }) ?? throw new WebDriverTimeoutException($"Timed out waiting for selector '{selector.Name}' to become visible.");
    }

    private IWebElement WaitUntilClickable(SelectorDefinition selector, TimeSpan? timeout = null)
    {
        var by = selector.ToBy();

        return CreateWait(timeout).Until(driver =>
        {
            try
            {
                var element = driver.FindElement(by);
                return element.Displayed && element.Enabled ? element : null;
            }
            catch (NoSuchElementException)
            {
                return null;
            }
            catch (StaleElementReferenceException)
            {
                return null;
            }
        }) ?? throw new WebDriverTimeoutException($"Timed out waiting for selector '{selector.Name}' to become clickable.");
    }

    private void WaitForDocumentReady()
    {
        CreateWait().Until(driver =>
        {
            var state = ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState");
            return string.Equals(state?.ToString(), "complete", StringComparison.OrdinalIgnoreCase);
        });
    }

    private WebDriverWait CreateWait(TimeSpan? timeout = null)
    {
        var wait = new WebDriverWait(new SystemClock(), _driver, timeout ?? _config.DefaultWaitTimeout, _config.PollingInterval);
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));
        return wait;
    }

    private void ClickElement(IWebElement element)
    {
        ScrollIntoView(element);

        try
        {
            element.Click();
        }
        catch (ElementClickInterceptedException)
        {
            ((IJavaScriptExecutor)_driver).ExecuteScript("arguments[0].click();", element);
        }
    }

    private void ScrollIntoView(IWebElement element)
    {
        ((IJavaScriptExecutor)_driver).ExecuteScript(
            "arguments[0].scrollIntoView({block: 'center', inline: 'center'});",
            element);
    }

    private static bool TryFindVisible(ISearchContext context, By by, out IWebElement? element)
    {
        try
        {
            element = context.FindElement(by);
            return element.Displayed;
        }
        catch (NoSuchElementException)
        {
            element = null;
            return false;
        }
        catch (StaleElementReferenceException)
        {
            element = null;
            return false;
        }
    }
}

internal sealed class LoginFailedException : Exception
{
    public LoginFailedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
