using System.Globalization;

namespace LingosBotApp;

/// <summary>
/// Reads every wordset assigned to the current class and builds the Polish prompt -> foreign
/// answer lookup the lesson runner uses. The whole set is re-read on every run.
/// </summary>
internal sealed class VocabularyCollector
{
    private readonly PageAgent _page;
    private readonly AppConfig _config;

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
        CollectFromPageData(builder);

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

    /// <summary>
    /// Reads the wordset list from the page data, then downloads every wordset page in parallel
    /// and takes the flashcards straight out of each one's JSON payload.
    /// </summary>
    private void CollectFromPageData(VocabularyBuilder builder)
    {
        var props = _page.ReadPropsWhenReady<StudentProps>("the Zestawy page");
        var wordsets = props.Wordsets?.Wordsets ?? throw new InvalidOperationException(
            $"The Zestawy page data has no wordset list (page '{props.Page}'). The account may be on a premium course, "
            + "or Lingos has changed the Zestawy page.");

        if (wordsets.Count == 0)
        {
            Console.WriteLine("This class has no wordsets.");
            return;
        }

        Console.WriteLine($"Found {wordsets.Count} wordset(s) to process.");

        var urls = wordsets
            .Select(wordset => string.Format(CultureInfo.InvariantCulture, _config.WordsetPathFormat, wordset.Id))
            .ToList();

        var pages = _page.FetchProps<StudentProps>(urls);

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

        if (loaded == 0)
        {
            throw new InvalidOperationException(
                $"None of the {wordsets.Count} wordset(s) could be downloaded; the lines above say why for each one.");
        }
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
