using System.Text.Json;
using System.Text.RegularExpressions;
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

    private const string DescribePageScript = """
        const el = document.getElementById('app');
        return JSON.stringify({ hasApp: el !== null, props: el ? el.getAttribute('data-props') : null, title: document.title || '' });
        """;

    private const string PostJsonScript = """
        const url = arguments[0];
        const body = arguments[1];
        const callback = arguments[arguments.length - 1];
        const init = { method: 'POST', headers: { 'Accept': 'application/json' }, credentials: 'same-origin' };
        if (body !== null) { init.headers['Content-Type'] = 'application/json'; init.body = body; }
        fetch(url, init)
            .then(r => r.text().then(t => callback(JSON.stringify(
                { ok: r.ok, status: r.status, redirectedTo: r.redirected ? r.url : '', body: t }))))
            .catch(e => callback(JSON.stringify({ ok: false, status: 0, redirectedTo: '', body: String(e) })));
        """;

    private const string FetchPropsScript = """
        const urls = arguments[0];
        const callback = arguments[arguments.length - 1];
        const one = url => fetch(url, { headers: { 'Accept': 'text/html' }, credentials: 'same-origin' })
            .then(r => r.text().then(html => {
                const result = { ok: r.ok, status: r.status, redirectedTo: r.redirected ? r.url : '', missingApp: false, body: '' };
                if (!r.ok) { result.body = html.slice(0, 2000); return result; }
                const el = new DOMParser().parseFromString(html, 'text/html').getElementById('app');
                result.missingApp = el === null;
                result.body = el ? (el.getAttribute('data-props') || '') : '';
                return result;
            }))
            .catch(e => ({ ok: false, status: 0, redirectedTo: '', missingApp: false, body: String(e) }));
        Promise.all(urls.map(one)).then(r => callback(JSON.stringify(r))).catch(e => callback(JSON.stringify(String(e))));
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
        var wait = Wait(timeout);
        try
        {
            return wait.Until(_ => ReadProps<T>())!;
        }
        catch (WebDriverTimeoutException ex)
        {
            throw new WebDriverTimeoutException(
                $"Timed out after {Seconds(wait.Timeout)} reading the page data for {what} at {Url}: {DescribePageDataProblem<T>()}", ex);
        }
    }

    /// <summary>
    /// Explains why <see cref="ReadProps{T}"/> found nothing usable on the open page, in words that
    /// can go straight into an error message.
    /// </summary>
    public string DescribePageDataProblem<T>() where T : class
    {
        if (IsLoginUrl(Url))
        {
            return "Lingos showed its login page instead, so the session has expired or the login did not go through.";
        }

        PageSnapshot? page;
        try
        {
            page = JsonSerializer.Deserialize<PageSnapshot>(_js.ExecuteScript(DescribePageScript) as string ?? "null", Json);
        }
        catch (Exception ex) when (ex is WebDriverException or JsonException)
        {
            return $"the page could not be inspected ({ex.Message}).";
        }

        if (page is null || !page.HasApp)
        {
            return $"the page has no #app element (page title: '{page?.Title}'). Lingos may have changed how it delivers "
                + "its page data, or this is an error page.";
        }

        if (string.IsNullOrWhiteSpace(page.Props))
        {
            return "the #app element has no data-props attribute. Lingos may have changed how it delivers its page data.";
        }

        try
        {
            return JsonSerializer.Deserialize<T>(page.Props, Json) is null
                ? "the page data is empty."
                : "the page data looks fine now; it was probably still loading.";
        }
        catch (JsonException ex)
        {
            return $"the page data no longer matches what the bot expects, so Lingos has probably changed it: {ex.Message}";
        }
    }

    /// <summary>
    /// Downloads several pages in parallel from inside the browser and returns only their
    /// <c>data-props</c> payloads - far cheaper than navigating to each page and booting its UI.
    /// </summary>
    public List<EndpointResult<T>> FetchProps<T>(IReadOnlyList<string> urls) where T : class
    {
        var results = new List<EndpointResult<T>>(urls.Count);
        if (urls.Count == 0)
        {
            return results;
        }

        var raw = _js.ExecuteAsyncScript(FetchPropsScript, new List<string>(urls))?.ToString() ?? string.Empty;
        List<HttpResult>? responses;
        try
        {
            responses = JsonSerializer.Deserialize<List<HttpResult>>(raw, Json);
        }
        catch (JsonException)
        {
            responses = null;
        }

        for (var i = 0; i < urls.Count; i++)
        {
            var response = responses is not null && i < responses.Count ? responses[i] : null;
            results.Add(response is { Ok: true, MissingApp: true } && !IsLoginUrl(response.RedirectedTo)
                ? EndpointResult<T>.Failed($"GET {urls[i]} returned a page without an #app element. Lingos may have changed that page.")
                : ParseResponse<T>($"GET {urls[i]}", response, raw));
        }

        return results;
    }

    // ---- the site's own JSON endpoints ------------------------------------

    /// <summary>
    /// POSTs to a same-origin endpoint from inside the page, the same way the site's own scripts
    /// do. On failure the result says what came back instead of the expected JSON.
    /// </summary>
    public EndpointResult<T> PostJson<T>(string url, object? payload) where T : class
    {
        var body = payload is null ? null : JsonSerializer.Serialize(payload, Json);
        var raw = _js.ExecuteAsyncScript(PostJsonScript, url, body)?.ToString() ?? string.Empty;

        HttpResult? response;
        try
        {
            response = JsonSerializer.Deserialize<HttpResult>(raw, Json);
        }
        catch (JsonException)
        {
            response = null;
        }

        return ParseResponse<T>($"POST {url}", response, raw);
    }

    private static EndpointResult<T> ParseResponse<T>(string request, HttpResult? response, string raw) where T : class
    {
        if (response is null)
        {
            return EndpointResult<T>.Failed($"{request} returned nothing the bot could read ({Excerpt(raw)}).");
        }

        if (response.Status == 0)
        {
            return EndpointResult<T>.Failed($"{request} could not reach lingos.pl ({response.Body}). Check the internet connection.");
        }

        if (IsLoginUrl(response.RedirectedTo))
        {
            return EndpointResult<T>.Failed(
                $"Lingos answered {request} with its login page, so the session has expired or the login did not go through.");
        }

        if (!response.Ok)
        {
            return EndpointResult<T>.Failed($"{request} was refused with HTTP {response.Status} ({DescribeStatus(response.Status)}). "
                + $"Lingos said: {Excerpt(response.Body)}");
        }

        try
        {
            var value = string.IsNullOrWhiteSpace(response.Body) ? null : JsonSerializer.Deserialize<T>(response.Body, Json);
            return value is null
                ? EndpointResult<T>.Failed($"{request} succeeded but returned no data.")
                : EndpointResult<T>.Success(value);
        }
        catch (JsonException ex)
        {
            return EndpointResult<T>.Failed($"{request} returned data the bot does not understand, so Lingos has probably "
                + $"changed it ({ex.Message}). Lingos said: {Excerpt(response.Body)}");
        }
    }

    private static string DescribeStatus(int status) => status switch
    {
        400 or 422 => "Lingos rejected what the bot sent; the request format has probably changed",
        401 or 403 => "not allowed; the session may have expired",
        404 => "not found; Lingos has probably moved or removed this endpoint, or the item no longer exists",
        405 => "this endpoint no longer accepts this kind of request",
        409 => "the lesson state is out of date",
        429 => "too many requests; Lingos is rate limiting",
        >= 500 => "Lingos had a server error; try again later",
        _ => "unexpected response"
    };

    /// <summary>A short, single-line sample of a response body: the page title for HTML, else the start of the text.</summary>
    private static string Excerpt(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "(empty response)";
        }

        var title = Regex.Match(body, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var text = title.Success ? $"HTML page '{title.Groups[1].Value}'" : Regex.Replace(body, @"<[^>]+>", " ");
        text = TextNormalizer.Normalize(text);
        return text.Length <= 200 ? text : text[..200] + "...";
    }

    private static bool IsLoginUrl(string? url) => url?.Contains("/h/login", StringComparison.OrdinalIgnoreCase) == true;

    private static string Seconds(TimeSpan timeout) => $"{timeout.TotalSeconds:0.#}s";

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

        public string RedirectedTo { get; set; } = string.Empty;

        public bool MissingApp { get; set; }

        public string Body { get; set; } = string.Empty;
    }

    private sealed class PageSnapshot
    {
        public bool HasApp { get; set; }

        public string? Props { get; set; }

        public string Title { get; set; } = string.Empty;
    }

    // ---- element access ---------------------------------------------------

    public bool TryFindVisible(SelectorDefinition selector, out IWebElement? element)
    {
        try
        {
            element = _driver.FindElement(selector.ToBy());
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
        var wait = Wait(timeout);
        try
        {
            return wait.Until(driver =>
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
            })!;
        }
        catch (WebDriverTimeoutException ex)
        {
            throw new WebDriverTimeoutException(
                $"Timed out after {Seconds(wait.Timeout)} waiting for {selector.Name} ({selector.Value}) at {Url}: "
                + DescribeElementProblem(by, requireEnabled), ex);
        }
    }

    /// <summary>Says whether a selector that timed out is missing, hidden or disabled on the open page.</summary>
    private string DescribeElementProblem(By by, bool requireEnabled)
    {
        if (IsLoginUrl(Url))
        {
            return "Lingos showed its login page instead, so the session has expired or the login did not go through.";
        }

        try
        {
            var matches = _driver.FindElements(by);
            if (matches.Count == 0)
            {
                return "nothing on the page matches it. Lingos has probably changed this part of its layout (see Selectors.cs).";
            }

            var visible = matches.Where(element => element.Displayed).ToList();
            if (visible.Count == 0)
            {
                return $"{matches.Count} element(s) match, but none is visible; something may be covering it or it is hidden.";
            }

            return requireEnabled && !visible.Any(element => element.Enabled)
                ? "it is on the page but disabled."
                : "it is on the page now, so it probably appeared just after the wait ended.";
        }
        catch (WebDriverException ex)
        {
            return $"the page could not be inspected ({ex.Message}).";
        }
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

    private void ScrollIntoView(IWebElement element)
        => _js.ExecuteScript("arguments[0].scrollIntoView({block: 'center', inline: 'center'});", element);

    // ---- waiting ----------------------------------------------------------

    public void WaitForDocumentReady()
    {
        var wait = Wait();
        try
        {
            wait.Until(_ => string.Equals(
                _js.ExecuteScript("return document.readyState;")?.ToString(), "complete", StringComparison.OrdinalIgnoreCase));
        }
        catch (WebDriverTimeoutException ex)
        {
            throw new WebDriverTimeoutException(
                $"Timed out after {Seconds(wait.Timeout)} waiting for {Url} to finish loading. Lingos may be slow or down.", ex);
        }
    }

    public void WaitForUrlContains(string fragment, TimeSpan? timeout = null)
    {
        var wait = Wait(timeout);
        try
        {
            wait.Until(_ => UrlContains(fragment));
        }
        catch (WebDriverTimeoutException ex)
        {
            throw new WebDriverTimeoutException(
                $"Timed out after {Seconds(wait.Timeout)} waiting for the address to contain '{fragment}'; the browser is still at {Url}.", ex);
        }
    }

    public WebDriverWait Wait(TimeSpan? timeout = null)
    {
        var wait = new WebDriverWait(new SystemClock(), _driver, timeout ?? _config.DefaultWaitTimeout, _config.PollingInterval);
        wait.IgnoreExceptionTypes(typeof(NoSuchElementException), typeof(StaleElementReferenceException));
        return wait;
    }
}

/// <summary>
/// What came back from one of the site's endpoints: the parsed value, or a one-line explanation
/// of what went wrong that can go straight into an error message.
/// </summary>
internal sealed record EndpointResult<T>(T? Value, string Failure) where T : class
{
    public static EndpointResult<T> Success(T value) => new(value, string.Empty);

    public static EndpointResult<T> Failed(string failure) => new(null, failure);
}
