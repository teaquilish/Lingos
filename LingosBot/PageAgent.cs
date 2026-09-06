using System.Text.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace LingosBotApp;

/// <summary>
/// Shared Selenium plumbing: waits, clicks, and the two ways this bot talks to the Lingos
/// single-page app - reading the server-rendered <c>#app[data-props]</c> payload, and calling
/// the site's own JSON endpoints from inside the page so the session cookies apply.
/// </summary>
internal sealed class PageAgent
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string ReadPropsScript = """
        const el = document.getElementById('app');
        return el ? el.getAttribute('data-props') : null;
        """;

    private const string PostJsonScript = """
        const url = arguments[0];
        const body = arguments[1];
        const callback = arguments[arguments.length - 1];
        const init = { method: 'POST', headers: { 'Accept': 'application/json' }, credentials: 'same-origin' };
        if (body !== null) { init.headers['Content-Type'] = 'application/json'; init.body = body; }
        fetch(url, init)
            .then(r => r.text().then(t => callback(JSON.stringify({ ok: r.ok, status: r.status, body: t }))))
            .catch(e => callback(JSON.stringify({ ok: false, status: 0, body: String(e) })));
        """;

    private const string FetchPropsScript = """
        const urls = arguments[0];
        const callback = arguments[arguments.length - 1];
        const one = url => fetch(url, { headers: { 'Accept': 'text/html' }, credentials: 'same-origin' })
            .then(r => (r.ok ? r.text() : null))
            .then(html => {
                if (html === null) { return null; }
                const el = new DOMParser().parseFromString(html, 'text/html').getElementById('app');
                return el ? el.getAttribute('data-props') : null;
            })
            .catch(() => null);
        Promise.all(urls.map(one)).then(r => callback(JSON.stringify(r))).catch(() => callback('[]'));
        """;

    private readonly IWebDriver _driver;
    private readonly AppConfig _config;
    private readonly IJavaScriptExecutor _js;

    public PageAgent(IWebDriver driver, AppConfig config)
    {
        _driver = driver;
        _config = config;
        _js = (IJavaScriptExecutor)driver;
    }

    public IWebDriver Driver => _driver;

    public AppConfig Config => _config;

    public string Url => _driver.Url ?? string.Empty;

    public bool UrlContains(string fragment) => Url.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    public void GoTo(string url) => _driver.Navigate().GoToUrl(url);

    // ---- page props -------------------------------------------------------

    /// <summary>Reads the JSON the server rendered into <c>#app[data-props]</c> for the open page.</summary>
    public T? ReadProps<T>() where T : class => Deserialize<T>(_js.ExecuteScript(ReadPropsScript) as string);

    /// <summary>
    /// Waits for <c>#app[data-props]</c> and deserializes it. The attribute is part of the server
    /// response, so after a navigation this resolves as soon as the document starts parsing.
    /// </summary>
    public T ReadPropsWhenReady<T>(string what, TimeSpan? timeout = null) where T : class
    {
        var props = Wait(timeout).Until(_ => ReadProps<T>());
        return props ?? throw new WebDriverTimeoutException($"Timed out reading the page data for {what} at {Url}.");
    }

    /// <summary>
    /// Downloads several pages in parallel from inside the browser and returns only their
    /// <c>data-props</c> payloads - far cheaper than navigating to each page and booting its UI.
    /// </summary>
    public List<T?> FetchProps<T>(IReadOnlyList<string> urls) where T : class
    {
        var results = new List<T?>(urls.Count);
        if (urls.Count == 0)
        {
            return results;
        }

        var raw = _js.ExecuteAsyncScript(FetchPropsScript, new List<string>(urls))?.ToString() ?? "[]";
        var payloads = Deserialize<List<string?>>(raw) ?? new List<string?>();
        foreach (var payload in payloads)
        {
            results.Add(Deserialize<T>(payload));
        }

        return results;
    }

    // ---- the site's own JSON endpoints ------------------------------------

    /// <summary>
    /// POSTs to a same-origin endpoint from inside the page, the same way the site's own scripts
    /// do. Returns null when the request failed or the response was not the expected JSON.
    /// </summary>
    public T? PostJson<T>(string url, object? payload) where T : class
    {
        var body = payload is null ? null : JsonSerializer.Serialize(payload, Json);
        var raw = _js.ExecuteAsyncScript(PostJsonScript, url, body)?.ToString() ?? string.Empty;

        var result = Deserialize<HttpResult>(raw);
        return result is { Ok: true } ? Deserialize<T>(result.Body) : null;
    }

    private static T? Deserialize<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class HttpResult
    {
        public bool Ok { get; set; }

        public int Status { get; set; }

        public string Body { get; set; } = string.Empty;
    }

    // ---- element access ---------------------------------------------------

    public bool TryFindVisible(SelectorDefinition selector, out IWebElement? element)
    {
        element = null;
        return selector.TryToBy(out var by) && by is not null && TryFindVisible(by, out element);
    }

    public bool TryFindVisible(By by, out IWebElement? element)
    {
        try
        {
            element = _driver.FindElement(by);
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

    public IWebElement WaitUntilVisible(SelectorDefinition selector, TimeSpan? timeout = null)
        => WaitForElement(selector, timeout, requireEnabled: false);

    public IWebElement WaitUntilClickable(SelectorDefinition selector, TimeSpan? timeout = null)
        => WaitForElement(selector, timeout, requireEnabled: true);

    public bool TryWaitUntilClickable(SelectorDefinition selector, TimeSpan timeout, out IWebElement? element)
    {
        try
        {
            element = WaitUntilClickable(selector, timeout);
            return true;
        }
        catch (WebDriverTimeoutException)
        {
            element = null;
            return false;
        }
    }

    private IWebElement WaitForElement(SelectorDefinition selector, TimeSpan? timeout, bool requireEnabled)
    {
        var by = selector.ToBy();
        var found = Wait(timeout).Until(driver =>
        {
            try
            {
                var element = driver.FindElement(by);
                return element.Displayed && (!requireEnabled || element.Enabled) ? element : null;
            }
            catch (NoSuchElementException)
            {
                return null;
            }
            catch (StaleElementReferenceException)
            {
                return null;
            }
        });

        return found ?? throw new WebDriverTimeoutException($"Timed out waiting for selector '{selector.Name}'.");
    }

    public IReadOnlyList<IWebElement> WaitUntilElementsCountAtLeast(SelectorDefinition selector, int minimumCount)
    {
        var by = selector.ToBy();
        var found = Wait().Until(driver =>
        {
            try
            {
                var visible = driver.FindElements(by).Where(element => element.Displayed).ToList();
                return visible.Count >= minimumCount ? visible : null;
            }
            catch (StaleElementReferenceException)
            {
                return null;
            }
        });

        return found ?? throw new WebDriverTimeoutException(
            $"Timed out waiting for at least {minimumCount} element(s) for selector '{selector.Name}'.");
    }

    public void Click(IWebElement element)
    {
        ScrollIntoView(element);
        try
        {
            element.Click();
        }
        catch (ElementClickInterceptedException)
        {
            _js.ExecuteScript("arguments[0].click();", element);
        }
    }

    public void ScrollIntoView(IWebElement element)
        => _js.ExecuteScript("arguments[0].scrollIntoView({block: 'center', inline: 'center'});", element);

    // ---- waiting ----------------------------------------------------------

    public void WaitForDocumentReady()
        => Wait().Until(_ => string.Equals(
            _js.ExecuteScript("return document.readyState;")?.ToString(), "complete", StringComparison.OrdinalIgnoreCase));

    public void WaitForUrlContains(string fragment, TimeSpan? timeout = null)
        => Wait(timeout).Until(_ => UrlContains(fragment));

    public WebDriverWait Wait(TimeSpan? timeout = null)
    {
        var wait = new WebDriverWait(new SystemClock(), _driver, timeout ?? _config.DefaultWaitTimeout, _config.PollingInterval);
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));
        return wait;
    }

    public object? ExecuteScript(string script, params object[] args) => _js.ExecuteScript(script, args);
}
