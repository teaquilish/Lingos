using System.Text.Json;
using LingosBotApp.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;

namespace LingosBotApp;

internal sealed class VocabularyCollector
{
    private readonly IWebDriver _driver;
    private readonly AppConfig _config;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public VocabularyCollector(IWebDriver driver, AppConfig config)
    {
        _driver = driver;
        _config = config;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> CollectVocabulary()
    {
        Console.WriteLine("Navigating to the Zestawy page...");
        var zestawyButton = WaitUntilClickable(Selectors.LeftMenuZestawyButton);
        ClickElement(zestawyButton);
        WaitForUrlContains("/student-confirmed/wordsets");

        if (Selectors.ZestawyPageMarker.TryToBy(out _))
        {
            WaitUntilVisible(Selectors.ZestawyPageMarker);
        }

        var wordsets = CollectWordsetDescriptors();
        Console.WriteLine($"Found {wordsets.Count} wordset(s) to process.");

        var vocabulary = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < wordsets.Count; index++)
        {
            var wordset = wordsets[index];
            Console.WriteLine($"Reading wordset {index + 1}/{wordsets.Count}: {wordset.Title}");

            var entries = ScrapeWordsetByNavigation(wordset);
            AddEntries(vocabulary, entries, logAlternatives: true);
            Console.WriteLine($"Collected {entries.Count} entries from {wordset.Title}.");
        }

        var readOnlyVocabulary = ToReadOnlyVocabulary(vocabulary);
        Console.WriteLine($"Vocabulary collection finished. Stored {readOnlyVocabulary.Count} unique Polish prompts.");
        return readOnlyVocabulary;
    }

    private List<WordsetDescriptor> CollectWordsetDescriptors()
    {
        var descriptors = new List<WordsetDescriptor>();
        var pageNumber = 1;

        while (true)
        {
            Console.WriteLine($"Scanning Zestawy page {pageNumber}...");
            WaitUntilElementsCountAtLeast(Selectors.SetCardContainer, 1);
            var pageDescriptors = ReadWordsetDescriptorsFromPage();
            Console.WriteLine($"Found {pageDescriptors.Count} set blocks on this page.");

            descriptors.AddRange(pageDescriptors);

            if (!TryGoToNextPage(pageNumber + 1))
            {
                break;
            }

            pageNumber++;
        }

        return descriptors
            .GroupBy(descriptor => descriptor.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private List<WordsetDescriptor> ReadWordsetDescriptorsFromPage()
    {
        try
        {
            var rawResult = ((IJavaScriptExecutor)_driver).ExecuteScript(
                """
                const cards = Array.from(document.querySelectorAll(arguments[0]));
                const buttonSelector = arguments[1];

                return JSON.stringify(cards.map(card => {
                    const button = card.querySelector(buttonSelector);
                    const lines = (card.innerText || '')
                        .split(/\r?\n/)
                        .map(line => line.trim())
                        .filter(Boolean);

                    return {
                        title: lines.length > 0 ? lines[0] : (button?.href || ''),
                        url: button?.href || ''
                    };
                }).filter(item => item.url));
                """,
                GetCssSelectorValue(Selectors.SetCardContainer),
                GetCssSelectorValue(Selectors.SetCardPreviewButton));

            var result = rawResult?.ToString() ?? "[]";
            var pageDescriptors = JsonSerializer.Deserialize<List<WordsetDescriptorPayload>>(result, _jsonOptions) ?? [];

            return pageDescriptors
                .Where(item => !string.IsNullOrWhiteSpace(item.Url))
                .Select(item => new WordsetDescriptor(
                    TextNormalizer.Normalize(item.Title),
                    ToAbsoluteUrl(item.Url)))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fast Zestawy scan failed: {ex.Message}. Falling back to Selenium element reads.");
            return ReadWordsetDescriptorsWithSelenium();
        }
    }

    private List<WordsetDescriptor> ReadWordsetDescriptorsWithSelenium()
    {
        var descriptors = new List<WordsetDescriptor>();
        var cards = WaitUntilElementsCountAtLeast(Selectors.SetCardContainer, 1);

        foreach (var card in cards)
        {
            var previewButton = FindChildElement(card, Selectors.SetCardPreviewButton);
            var href = previewButton.GetDomAttribute("href") ?? string.Empty;
            var title = TextNormalizer.Normalize(
                card.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? href);

            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            descriptors.Add(new WordsetDescriptor(title, ToAbsoluteUrl(href)));
        }

        return descriptors;
    }

    private List<VocabularyEntry> ScrapeWordsetByNavigation(WordsetDescriptor wordset)
    {
        _driver.Navigate().GoToUrl(wordset.Url);
        WaitForUrlContains("/student-confirmed/wordset/");
        return ReadVocabularyEntriesFromCurrentPage();
    }

    private List<VocabularyEntry> ReadVocabularyEntriesFromCurrentPage()
    {
        WaitUntilElementsCountAtLeast(Selectors.PreviewVocabularyRow, 1);

        try
        {
            var rawResult = ((IJavaScriptExecutor)_driver).ExecuteScript(
                """
                const rows = Array.from(document.querySelectorAll(arguments[0]));
                const foreignSelector = arguments[1];
                const polishSelector = arguments[2];

                return JSON.stringify(rows.map(row => ({
                    foreignWord: (row.querySelector(foreignSelector)?.innerText || '').trim(),
                    polishTranslation: (row.querySelector(polishSelector)?.innerText || '').trim()
                })).filter(entry => entry.foreignWord && entry.polishTranslation));
                """,
                GetCssSelectorValue(Selectors.PreviewVocabularyRow),
                GetCssSelectorValue(Selectors.PreviewForeignWordCell),
                GetCssSelectorValue(Selectors.PreviewPolishWordCell));

            var result = rawResult?.ToString() ?? "[]";
            var payload = JsonSerializer.Deserialize<List<VocabularyEntryPayload>>(result, _jsonOptions) ?? [];

            var entries = payload
                .Select(entry => new VocabularyEntry(
                    TextNormalizer.Normalize(entry.ForeignWord),
                    TextNormalizer.Normalize(entry.PolishTranslation)))
                .Where(entry =>
                    !string.IsNullOrWhiteSpace(entry.ForeignWord) &&
                    !string.IsNullOrWhiteSpace(entry.PolishTranslation))
                .ToList();

            if (entries.Count > 0)
            {
                return entries;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fast wordset scrape failed: {ex.Message}. Falling back to Selenium element reads.");
        }

        return ScrapePreviewWithSelenium();
    }

    private List<VocabularyEntry> ScrapePreviewWithSelenium()
    {
        var rows = WaitUntilElementsCountAtLeast(Selectors.PreviewVocabularyRow, 1);
        var entries = new List<VocabularyEntry>(rows.Count);

        foreach (var row in rows)
        {
            var foreignCell = FindChildElement(row, Selectors.PreviewForeignWordCell);
            var polishCell = FindChildElement(row, Selectors.PreviewPolishWordCell);

            var foreignWord = TextNormalizer.Normalize(foreignCell.Text);
            var polishTranslation = TextNormalizer.Normalize(polishCell.Text);

            if (string.IsNullOrWhiteSpace(foreignWord) || string.IsNullOrWhiteSpace(polishTranslation))
            {
                continue;
            }

            entries.Add(new VocabularyEntry(foreignWord, polishTranslation));
        }

        return entries;
    }

    private void AddEntries(
        Dictionary<string, List<string>> vocabulary,
        IReadOnlyList<VocabularyEntry> entries,
        bool logAlternatives)
    {
        foreach (var entry in entries)
        {
            var polishKey = TextNormalizer.Normalize(entry.PolishTranslation);
            var foreignWord = TextNormalizer.Normalize(entry.ForeignWord);

            if (string.IsNullOrWhiteSpace(polishKey) || string.IsNullOrWhiteSpace(foreignWord))
            {
                continue;
            }

            if (!vocabulary.TryGetValue(polishKey, out var candidates))
            {
                candidates = [];
                vocabulary[polishKey] = candidates;
            }

            if (candidates.Any(candidate => string.Equals(candidate, foreignWord, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            candidates.Add(foreignWord);

            if (logAlternatives && candidates.Count > 1)
            {
                Console.WriteLine(
                    $"Stored an alternative answer for '{entry.PolishTranslation}': {string.Join(", ", candidates)}");
            }
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToReadOnlyVocabulary(
        Dictionary<string, List<string>> vocabulary)
    {
        var readOnlyVocabulary = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in vocabulary)
        {
            readOnlyVocabulary[pair.Key] = pair.Value.AsReadOnly();
        }

        return readOnlyVocabulary;
    }

    private bool TryGoToNextPage(int nextPageNumber)
    {
        if (!Selectors.ZestawyNextPageButton.TryToBy(out var nextPageBy) || nextPageBy is null)
        {
            Console.WriteLine("Optional Zestawy next-page selector is not configured. Assuming there is only one page.");
            return false;
        }

        try
        {
            var nextPageButton = CreateWait(_config.ShortWaitTimeout).Until(driver =>
            {
                try
                {
                    var element = driver.FindElement(nextPageBy);

                    if (!element.Displayed || !element.Enabled)
                    {
                        return null;
                    }

                    var disabledAttribute = element.GetDomAttribute("disabled");
                    var ariaDisabled = element.GetDomAttribute("aria-disabled");

                    return string.IsNullOrWhiteSpace(disabledAttribute) &&
                           !string.Equals(ariaDisabled, "true", StringComparison.OrdinalIgnoreCase)
                        ? element
                        : null;
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

            if (nextPageButton is null)
            {
                Console.WriteLine("No active next-page button was found for Zestawy.");
                return false;
            }

            Console.WriteLine($"Moving to Zestawy page {nextPageNumber}...");
            ClickElement(nextPageButton);
            WaitForDocumentReady();
            WaitUntilElementsCountAtLeast(Selectors.SetCardContainer, 1);
            return true;
        }
        catch (WebDriverTimeoutException)
        {
            Console.WriteLine("Reached the last Zestawy page.");
            return false;
        }
    }

    private IReadOnlyList<IWebElement> WaitUntilElementsCountAtLeast(SelectorDefinition selector, int minimumCount)
    {
        var by = selector.ToBy();

        return CreateWait().Until(driver =>
        {
            try
            {
                var elements = driver.FindElements(by)
                    .Where(element => element.Displayed)
                    .ToList();

                return elements.Count >= minimumCount ? elements : null;
            }
            catch (StaleElementReferenceException)
            {
                return null;
            }
        }) ?? throw new WebDriverTimeoutException(
            $"Timed out waiting for at least {minimumCount} element(s) for selector '{selector.Name}'.");
    }

    private IWebElement WaitUntilVisible(SelectorDefinition selector)
    {
        var by = selector.ToBy();

        return CreateWait().Until(driver =>
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

    private IWebElement WaitUntilClickable(SelectorDefinition selector)
    {
        var by = selector.ToBy();

        return CreateWait().Until(driver =>
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

    private IWebElement FindChildElement(ISearchContext parent, SelectorDefinition selector)
    {
        try
        {
            return parent.FindElement(selector.ToBy());
        }
        catch (NoSuchElementException ex)
        {
            throw new InvalidOperationException(
                $"Selector '{selector.Name}' did not match the expected child element. Verify Selectors.cs.",
                ex);
        }
    }

    private void WaitForDocumentReady()
    {
        CreateWait().Until(driver =>
        {
            var state = ((IJavaScriptExecutor)driver).ExecuteScript("return document.readyState");
            return string.Equals(state?.ToString(), "complete", StringComparison.OrdinalIgnoreCase);
        });
    }

    private void WaitForUrlContains(string pathFragment)
    {
        CreateWait().Until(driver =>
        {
            var currentUrl = driver.Url ?? string.Empty;
            return currentUrl.Contains(pathFragment, StringComparison.OrdinalIgnoreCase);
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

    private string ToAbsoluteUrl(string urlOrPath)
    {
        if (Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.ToString();
        }

        return new Uri(new Uri(_config.BaseUrl), urlOrPath).ToString();
    }

    private static string GetCssSelectorValue(SelectorDefinition selector)
    {
        if (!selector.IsConfigured)
        {
            selector.ToBy();
        }

        if (selector.Strategy != SelectorStrategy.Css)
        {
            throw new InvalidOperationException(
                $"Selector '{selector.Name}' must be a CSS selector for the fast Zestawy reader.");
        }

        return selector.Value;
    }

    private sealed record WordsetDescriptor(string Title, string Url);

    private sealed class WordsetDescriptorPayload
    {
        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;
    }

    private sealed class VocabularyEntryPayload
    {
        public string ForeignWord { get; set; } = string.Empty;

        public string PolishTranslation { get; set; } = string.Empty;
    }
}
