using System.Globalization;
using System.Text.Json;
using LingosBotApp.Models;
using OpenQA.Selenium;

namespace LingosBotApp;

/// <summary>
/// Reads every wordset assigned to the current class and builds the Polish prompt -> foreign
/// answer lookup the lesson runner uses. The whole set is re-read on every run.
/// </summary>
internal sealed class VocabularyCollector
{
    private const string ReadWordsetCardsScript = """
        const cards = Array.from(document.querySelectorAll(arguments[0]));
        const buttonSelector = arguments[1];
        return JSON.stringify(cards.map(card => {
            const button = card.querySelector(buttonSelector);
            const linked = (button?.innerText || '').trim();
            const lines = (card.innerText || '').split(/\r?\n/).map(l => l.trim()).filter(Boolean);
            return { title: linked || lines[0] || (button?.href || ''), url: button?.href || '' };
        }).filter(item => item.url));
        """;

    private const string ReadVocabularyRowsScript = """
        const rows = Array.from(document.querySelectorAll(arguments[0]));
        const foreignSelector = arguments[1];
        const polishSelector = arguments[2];
        return JSON.stringify(rows.map(row => ({
            foreignWord: (row.querySelector(foreignSelector)?.innerText || '').trim(),
            polishTranslation: (row.querySelector(polishSelector)?.innerText || '').trim()
        })).filter(entry => entry.foreignWord && entry.polishTranslation));
        """;

    private readonly PageAgent _page;
    private readonly AppConfig _config;

    public VocabularyCollector(IWebDriver driver, AppConfig config)
        : this(new PageAgent(driver, config))
    {
    }

    public VocabularyCollector(PageAgent page)
    {
        _page = page;
        _config = page.Config;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> CollectVocabulary()
    {
        Console.WriteLine("Navigating to the Zestawy page...");
        OpenZestawyPage();

        var builder = new VocabularyBuilder();

        if (!TryCollectFromPageData(builder))
        {
            CollectByVisitingEachWordset(builder);
        }

        var result = builder.Build();
        Console.WriteLine($"Vocabulary collection finished. Stored {result.Count} unique Polish prompts.");
        return result;
    }

    private void OpenZestawyPage()
    {
        if (_page.TryWaitUntilClickable(Selectors.LeftMenuZestawyButton, _config.ShortWaitTimeout, out var menuLink) && menuLink is not null)
        {
            _page.Click(menuLink);
        }
        else
        {
            _page.GoTo(_config.WordsetsUrl);
        }

        _page.WaitForUrlContains("/student/wordsets");
    }

    // ---- fast path: the wordset pages carry their flashcards as JSON -------

    /// <summary>
    /// Reads the wordset list from the page data, then downloads every wordset page in parallel
    /// and takes the flashcards straight out of each one's JSON payload.
    /// </summary>
    private bool TryCollectFromPageData(VocabularyBuilder builder)
    {
        var props = _page.ReadProps<StudentProps>();
        var wordsets = props?.Wordsets?.Wordsets;
        if (wordsets is null || wordsets.Count == 0)
        {
            var reason = props is null ? _page.DescribePageDataProblem<StudentProps>()
                : props.Wordsets is null ? $"the page data has no wordset list (page '{props.Page}'). It may be a premium course, "
                    + "or Lingos has changed the Zestawy page."
                : "the class has no wordsets.";
            Console.WriteLine($"Could not read the wordset list from the page data: {reason} Falling back to reading the page itself.");
            return false;
        }

        Console.WriteLine($"Found {wordsets.Count} wordset(s) to process.");

        var urls = wordsets
            .Select(wordset => string.Format(CultureInfo.InvariantCulture, _config.WordsetPathFormat, wordset.Id))
            .ToList();

        List<EndpointResult<StudentProps>> pages;
        try
        {
            pages = _page.FetchProps<StudentProps>(urls);
        }
        catch (WebDriverException ex)
        {
            Console.WriteLine($"Fast wordset download failed: {ex.Message}. Falling back to opening each wordset.");
            return false;
        }

        var loaded = 0;
        for (var i = 0; i < wordsets.Count; i++)
        {
            var wordset = wordsets[i];
            var page = pages[i];
            var flashcards = page.Value?.Wordset?.Flashcards;
            if (flashcards is null)
            {
                var reason = page.Value is null ? page.Failure
                    : $"its page data has no flashcard list (page '{page.Value.Page}'), so Lingos has probably changed the wordset page.";
                Console.WriteLine($"Could not read wordset {i + 1}/{wordsets.Count}: {wordset.Name}. {reason}");
                continue;
            }

            foreach (var flashcard in flashcards)
            {
                builder.Add(flashcard.Translation, flashcard.Word);
            }

            loaded++;
            Console.WriteLine($"Collected {flashcards.Count} entries from {wordset.Name}.");
        }

        if (loaded != 0)
        {
            return true;
        }

        Console.WriteLine("No wordset could be downloaded. Falling back to opening each wordset.");
        return false;
    }

    // ---- fallback: navigate to each wordset and read the rendered rows -----

    private void CollectByVisitingEachWordset(VocabularyBuilder builder)
    {
        var wordsets = ReadWordsetDescriptorsFromPage();
        Console.WriteLine($"Found {wordsets.Count} wordset(s) to process.");

        for (var i = 0; i < wordsets.Count; i++)
        {
            var wordset = wordsets[i];
            Console.WriteLine($"Reading wordset {i + 1}/{wordsets.Count}: {wordset.Title}");

            _page.GoTo(wordset.Url);
            _page.WaitForUrlContains("/student/wordsets/");
            var entries = ReadVocabularyEntriesFromCurrentPage();

            foreach (var entry in entries)
            {
                builder.Add(entry.PolishTranslation, entry.ForeignWord);
            }

            Console.WriteLine($"Collected {entries.Count} entries from {wordset.Title}.");
        }
    }

    private List<WordsetDescriptor> ReadWordsetDescriptorsFromPage()
    {
        try
        {
            var raw = _page.ExecuteScript(
                ReadWordsetCardsScript,
                GetCssSelectorValue(Selectors.SetCardContainer),
                GetCssSelectorValue(Selectors.SetCardPreviewButton))?.ToString() ?? "[]";

            var payloads = JsonSerializer.Deserialize<List<WordsetDescriptorPayload>>(raw, PageAgent.Json)
                ?? new List<WordsetDescriptorPayload>();

            var descriptors = payloads
                .Where(payload => !string.IsNullOrWhiteSpace(payload.Url))
                .Select(payload => new WordsetDescriptor(TextNormalizer.Normalize(payload.Title), ToAbsoluteUrl(payload.Url)))
                .ToList();

            if (descriptors.Count > 0)
            {
                return Deduplicate(descriptors);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fast Zestawy scan failed: {ex.Message}. Falling back to Selenium element reads.");
        }

        return Deduplicate(ReadWordsetDescriptorsWithSelenium());
    }

    private List<WordsetDescriptor> ReadWordsetDescriptorsWithSelenium()
    {
        var descriptors = new List<WordsetDescriptor>();
        foreach (var card in _page.WaitUntilElementsCountAtLeast(Selectors.SetCardContainer, 1))
        {
            var link = FindChildElement(card, Selectors.SetCardPreviewButton);
            var href = link.GetDomAttribute("href") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var title = TextNormalizer.Normalize(link.Text);
            if (string.IsNullOrWhiteSpace(title))
            {
                var lines = card.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                title = TextNormalizer.Normalize(lines.Length > 0 ? lines[0] : href);
            }

            descriptors.Add(new WordsetDescriptor(title, ToAbsoluteUrl(href)));
        }

        return descriptors;
    }

    private static List<WordsetDescriptor> Deduplicate(List<WordsetDescriptor> descriptors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return descriptors.Where(descriptor => seen.Add(descriptor.Url)).ToList();
    }

    private List<VocabularyEntry> ReadVocabularyEntriesFromCurrentPage()
    {
        var flashcards = _page.ReadProps<StudentProps>()?.Wordset?.Flashcards;
        if (flashcards is { Count: > 0 })
        {
            return flashcards
                .Select(flashcard => new VocabularyEntry(
                    TextNormalizer.Normalize(flashcard.Word),
                    TextNormalizer.Normalize(flashcard.Translation)))
                .ToList();
        }

        _page.WaitUntilElementsCountAtLeast(Selectors.PreviewVocabularyRow, 1);
        try
        {
            var raw = _page.ExecuteScript(
                ReadVocabularyRowsScript,
                GetCssSelectorValue(Selectors.PreviewVocabularyRow),
                GetCssSelectorValue(Selectors.PreviewForeignWordCell),
                GetCssSelectorValue(Selectors.PreviewPolishWordCell))?.ToString() ?? "[]";

            var payloads = JsonSerializer.Deserialize<List<VocabularyEntryPayload>>(raw, PageAgent.Json)
                ?? new List<VocabularyEntryPayload>();

            var entries = payloads
                .Select(payload => new VocabularyEntry(
                    TextNormalizer.Normalize(payload.ForeignWord),
                    TextNormalizer.Normalize(payload.PolishTranslation)))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.ForeignWord) && !string.IsNullOrWhiteSpace(entry.PolishTranslation))
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
        var rows = _page.WaitUntilElementsCountAtLeast(Selectors.PreviewVocabularyRow, 1);
        var entries = new List<VocabularyEntry>(rows.Count);
        foreach (var row in rows)
        {
            var foreign = TextNormalizer.Normalize(FindChildElement(row, Selectors.PreviewForeignWordCell).Text);
            var polish = TextNormalizer.Normalize(FindChildElement(row, Selectors.PreviewPolishWordCell).Text);
            if (!string.IsNullOrWhiteSpace(foreign) && !string.IsNullOrWhiteSpace(polish))
            {
                entries.Add(new VocabularyEntry(foreign, polish));
            }
        }

        return entries;
    }

    private static IWebElement FindChildElement(ISearchContext parent, SelectorDefinition selector)
    {
        try
        {
            return parent.FindElement(selector.ToBy());
        }
        catch (NoSuchElementException ex)
        {
            throw new InvalidOperationException(
                $"Selector '{selector.Name}' did not match the expected child element. Verify Selectors.cs.", ex);
        }
    }

    private string ToAbsoluteUrl(string urlOrPath)
        => Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(new Uri(_config.BaseUrl), urlOrPath).ToString();

    private static string GetCssSelectorValue(SelectorDefinition selector)
    {
        if (!selector.IsConfigured)
        {
            selector.ToBy();
        }

        if (selector.Strategy != SelectorStrategy.Css)
        {
            throw new InvalidOperationException($"Selector '{selector.Name}' must be a CSS selector for the fast Zestawy reader.");
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

/// <summary>
/// Accumulates Polish prompt -> foreign answer pairs, keeping alternatives in the order they
/// were seen and discarding duplicates.
/// </summary>
internal sealed class VocabularyBuilder
{
    private readonly Dictionary<string, List<string>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _seen = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string polishTranslation, string foreignWord)
    {
        var prompt = TextNormalizer.Normalize(polishTranslation);
        var answer = TextNormalizer.Normalize(foreignWord);
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(answer))
        {
            return;
        }

        if (!_answers.TryGetValue(prompt, out var answers))
        {
            answers = new List<string>(1);
            _answers[prompt] = answers;
            _seen[prompt] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (_seen[prompt].Add(answer))
        {
            answers.Add(answer);
            if (answers.Count > 1)
            {
                Console.WriteLine($"Stored an alternative answer for '{prompt}': {string.Join(", ", answers)}");
            }
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Build()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(_answers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (prompt, answers) in _answers)
        {
            result[prompt] = answers.AsReadOnly();
        }

        return result;
    }
}
