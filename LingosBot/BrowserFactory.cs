using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace LingosBotApp;

internal sealed class BrowserFactory
{
    public IWebDriver Create(AppConfig config)
    {
        var chromeService = ChromeDriverService.CreateDefaultService();
        chromeService.HideCommandPromptWindow = true;

        var chromeOptions = new ChromeOptions();
        chromeOptions.AddArgument("--headless=new");
        chromeOptions.AddArgument("--mute-audio");
        chromeOptions.AddArgument("--start-maximized");
        chromeOptions.AddArgument("--window-size=1600,1000");
        chromeOptions.AddArgument("--no-first-run");
        chromeOptions.AddArgument("--disable-default-apps");
        chromeOptions.AddArgument("--disable-background-networking");
        chromeOptions.AddArgument("--disable-background-timer-throttling");
        chromeOptions.AddArgument("--disable-backgrounding-occluded-windows");
        chromeOptions.AddArgument("--disable-component-update");
        chromeOptions.AddArgument("--disable-extensions");
        chromeOptions.AddArgument("--disable-features=Translate,OptimizationHints,MediaRouter,ChromeWhatsNewUI,AutofillServerCommunication");
        chromeOptions.AddArgument("--disable-notifications");
        chromeOptions.AddArgument("--disable-popup-blocking");
        chromeOptions.AddArgument("--disable-renderer-backgrounding");
        chromeOptions.AddArgument("--disable-search-engine-choice-screen");
        chromeOptions.AddArgument("--disable-sync");
        chromeOptions.AddArgument("--metrics-recording-only");
        chromeOptions.AddArgument("--lang=pl-PL");
        chromeOptions.AddUserProfilePreference("profile.default_content_setting_values.images", 2);
        chromeOptions.AddUserProfilePreference("credentials_enable_service", false);
        chromeOptions.AddUserProfilePreference("profile.password_manager_enabled", false);
        chromeOptions.PageLoadStrategy = PageLoadStrategy.Eager;

        if (!string.IsNullOrWhiteSpace(config.ChromeBinaryPath))
        {
            chromeOptions.BinaryLocation = config.ChromeBinaryPath;
            Console.WriteLine($"Using Chrome binary from LINGOS_CHROME_BINARY: {config.ChromeBinaryPath}");
        }

        var driver = new ChromeDriver(chromeService, chromeOptions);
        driver.Manage().Timeouts().PageLoad = config.PageLoadTimeout;
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;

        return driver;
    }
}
