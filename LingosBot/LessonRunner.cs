using System.Globalization;
using OpenQA.Selenium;

namespace LingosBotApp;

/// <summary>
/// Runs one lesson at a time: open the dashboard, start a lesson, then answer each prompt from
/// the collected vocabulary. Prompts are answered through the site's own answer endpoint, with
/// a fallback that types into the page when that endpoint cannot be used.
/// </summary>
internal sealed class LessonRunner
{
    private const string StateExercise = "exercise";
    private const string StateFinished = "finished";
    private const string StateChangeWordset = "change_wordset";
    private const string StateSelectLevel = "select_level";
    private const string TypeFlashcard = "flashcard";
    private const string StatusOk = "ok";
    private const string ReasonLimitExceeded = "limit-exceeded";

    private const string PopulateAnswerScript = """
        const input = arguments[0];
        const value = arguments[1];
        const descriptor =
            Object.getOwnPropertyDescriptor(Object.getPrototypeOf(input), 'value') ||
            Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
        if (!descriptor || typeof descriptor.set !== 'function') { return false; }
        input.focus();
        descriptor.set.call(input, '');
        input.dispatchEvent(new Event('input', { bubbles: true }));
        descriptor.set.call(input, value);
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
        """;

    private readonly PageAgent _page;
    private readonly AppConfig _config;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _vocabulary;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _reverseVocabulary;

    public LessonRunner(IWebDriver driver, AppConfig config, IReadOnlyDictionary<string, IReadOnlyList<string>> vocabulary)
        : this(new PageAgent(driver, config), vocabulary)
    {
    }

    public LessonRunner(PageAgent page, IReadOnlyDictionary<string, IReadOnlyList<string>> vocabulary)
    {
        _page = page;
        _config = page.Config;
        _vocabulary = vocabulary;
        _reverseVocabulary = BuildReverseVocabulary(vocabulary);
    }

    public void RunLesson(int lessonNumber)
    {
        Console.WriteLine($"Preparing lesson {lessonNumber}...");
        OpenMainPage();

        var props = StartLesson();
        if (!RunLessonViaAnswerEndpoint(lessonNumber, props))
        {
            Console.WriteLine("Falling back to driving the lesson through the page itself.");
            RunLessonInPage(lessonNumber);
        }
    }

    // ---- getting into a lesson --------------------------------------------

    private void OpenMainPage()
    {
        var inLesson = _page.UrlContains("/learning/");
        if (!inLesson
            && (_page.TryFindVisible(Selectors.MainLearnButton, out var reusable) && reusable is { Enabled: true }
                || _page.TryWaitUntilClickable(Selectors.MainLearnButton, _config.LessonRestartReuseTimeout, out reusable) && reusable is not null))
        {
            return;
        }

        _page.GoTo(_config.StudentDashboardUrl);
        _page.WaitForDocumentReady();
        ThrowIfDailyLimitReached();
        _page.WaitUntilClickable(Selectors.MainLearnButton);
    }

    /// <summary>
    /// Once the daily limit is spent the dashboard simply drops the "Ucz sie" link, so check the
    /// page data for the flag first and stop cleanly instead of waiting for a button that is gone.
    /// </summary>
    private void ThrowIfDailyLimitReached()
    {
        var props = _page.ReadProps<StudentProps>();
        if (props?.Dashboard?.LessonLimitReached == true || props?.Wordsets?.LessonLimitReached == true)
        {
            throw new LessonLimitReachedException(
                "Lingos reported that today's lesson limit has been reached for this account. The bot stopped cleanly.");
        }
    }

    private LearningProps StartLesson()
    {
        Console.WriteLine("Starting a lesson from the main page...");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                _page.Click(_page.WaitUntilClickable(Selectors.MainLearnButton));
                _page.WaitForUrlContains("/learning/", _config.ShortWaitTimeout);

                var props = ReadLearningProps();
                ThrowIfLessonBlocked(props);
                if (string.Equals(props.Status, StatusOk, StringComparison.Ordinal) && props.Step is not null)
                {
                    return props;
                }
            }
            catch (WebDriverTimeoutException) when (attempt < 3)
            {
            }

            Console.WriteLine("Lingos did not open a lesson on that click. Retrying...");
            _page.GoTo(_config.StudentDashboardUrl);
            _page.WaitForDocumentReady();
            ThrowIfDailyLimitReached();
        }

        throw new WebDriverTimeoutException(
            "Timed out while entering the lesson. Lingos did not show a prompt, continue step, or lesson-limit message.");
    }

    private LearningProps ReadLearningProps() => _page.ReadPropsWhenReady<LearningProps>("the lesson");

    private static void ThrowIfLessonBlocked(LearningProps props)
    {
        if (string.Equals(props.Status, StatusOk, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(props.Reason, ReasonLimitExceeded, StringComparison.Ordinal))
        {
            throw new LessonLimitReachedException(
                "Lingos reported that today's lesson limit has been reached for this account. The bot stopped cleanly.");
        }

        if (!string.IsNullOrWhiteSpace(props.Reason))
        {
            throw new InvalidOperationException($"Lingos refused to start a lesson ({props.Reason}).");
        }
    }

    // ---- fast path: the site's own answer endpoint ------------------------

    /// <summary>
    /// Plays the lesson through <c>POST /learning/answer/{lessonId}</c>, which is what the page's
    /// own script does. Returns false if the lesson has to be finished through the page instead.
    /// </summary>
    private bool RunLessonViaAnswerEndpoint(int lessonNumber, LearningProps props)
    {
        if (!string.Equals(props.Status, StatusOk, StringComparison.Ordinal) || props.Step is null)
        {
            return false;
        }

        var answerUrl = AnswerUrl(props.LessonId);
        var step = props.Step;

        var attempts = 0;
        var currentIdentifier = 0L;
        var candidateIndex = 0;

        while (attempts < _config.LessonPromptSafetyCap)
        {
            switch (step.State)
            {
                case StateFinished:
                    Console.WriteLine($"Lesson {lessonNumber} completed after {attempts} prompts.");
                    return true;

                case StateChangeWordset:
                {
                    if (string.IsNullOrWhiteSpace(step.ContinueUrl))
                    {
                        return false;
                    }

                    Console.WriteLine("Lingos asked to carry the lesson on into the next wordset. Continuing...");
                    _page.GoTo(ToAbsoluteUrl(step.ContinueUrl));

                    var continued = ReadLearningProps();
                    ThrowIfLessonBlocked(continued);
                    if (!string.Equals(continued.Status, StatusOk, StringComparison.Ordinal) || continued.Step is null)
                    {
                        return false;
                    }

                    answerUrl = AnswerUrl(continued.LessonId);
                    step = continued.Step;
                    continue;
                }

                case StateSelectLevel:
                    throw new InvalidOperationException(
                        "Lingos wants you to choose your language level before it will start a lesson. "
                        + "Pick it once yourself on lingos.pl, then run the bot again.");

                case StateExercise:
                    break;

                default:
                    Console.WriteLine($"Lingos returned an unfamiliar lesson step ('{step.State}').");
                    return false;
            }

            var exercise = step.Exercise;
            if (exercise is null)
            {
                return false;
            }

            if (exercise.Identifier != currentIdentifier)
            {
                currentIdentifier = exercise.Identifier;
                candidateIndex = 0;
            }

            attempts++;
            var request = BuildAnswerRequest(exercise, lessonNumber, attempts, candidateIndex, out var promptText);

            var result = _page.PostJson<LearningAnswerResult>(answerUrl, request);
            if (result?.Step is null)
            {
                Console.WriteLine("The lesson answer request did not go through.");
                return false;
            }

            if (result.Feedback is { Result: <= 0 })
            {
                candidateIndex++;
                ReportRejectedAnswer(promptText, request.Answer, result.Feedback, result.Step, exercise.Identifier);
            }

            step = result.Step;
        }

        throw new InvalidOperationException(
            $"Lesson {lessonNumber} exceeded the safety cap of {_config.LessonPromptSafetyCap} prompts. Stopping to avoid an infinite loop.");
    }

    private LearningAnswerRequest BuildAnswerRequest(
        LearningExercise exercise,
        int lessonNumber,
        int promptIndex,
        int candidateIndex,
        out string promptText)
    {
        promptText = TextNormalizer.Normalize(exercise.Main);

        // "new_teacher" and "new_premium" only present a word; they are acknowledged, not answered.
        if (!string.Equals(exercise.Type, TypeFlashcard, StringComparison.Ordinal))
        {
            Console.WriteLine($"[Lesson {lessonNumber} · word {promptIndex}] new word: {TextNormalizer.Normalize(exercise.Translation)}");
            return new LearningAnswerRequest { Identifier = exercise.Identifier, Answer = string.Empty, Skip = false };
        }

        if (string.IsNullOrWhiteSpace(promptText))
        {
            throw new InvalidOperationException("The lesson prompt text was empty. Verify the lesson prompt selector in Selectors.cs.");
        }

        var candidates = ResolveCandidateAnswers(promptText);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No collected translation exists for lesson prompt '{promptText}'. The bot stopped safely without guessing.");
        }

        if (candidateIndex >= candidates.Count)
        {
            throw new InvalidOperationException($"All {candidates.Count} stored answers were rejected for lesson prompt '{promptText}'.");
        }

        var answer = candidates[candidateIndex];
        Console.WriteLine(candidates.Count > 1 || candidateIndex > 0
            ? $"[Lesson {lessonNumber} · word {promptIndex}] {promptText} -> {answer}  (option {candidateIndex + 1}/{candidates.Count})"
            : $"[Lesson {lessonNumber} · word {promptIndex}] {promptText} -> {answer}");

        return new LearningAnswerRequest { Identifier = exercise.Identifier, Answer = answer, Skip = false };
    }

    private static void ReportRejectedAnswer(
        string promptText,
        string answer,
        LearningFeedback feedback,
        LearningStep nextStep,
        long identifier)
    {
        if (nextStep.Exercise?.Identifier == identifier)
        {
            Console.WriteLine($"Answer '{answer}' was rejected for '{promptText}'. Trying the next stored translation for this exact prompt only.");
            return;
        }

        var correct = TextNormalizer.Normalize(feedback.Correct);
        Console.WriteLine(string.IsNullOrWhiteSpace(correct)
            ? $"Answer '{answer}' was rejected for '{promptText}'. Lingos moved on to the next word."
            : $"Answer '{answer}' was rejected for '{promptText}' (Lingos wanted '{correct}'). Moving on to the next word.");
    }

    private string AnswerUrl(long lessonId)
        => string.Format(CultureInfo.InvariantCulture, _config.LearningAnswerPathFormat, lessonId);

    private string ToAbsoluteUrl(string urlOrPath)
        => Uri.TryCreate(urlOrPath, UriKind.Absolute, out var absolute)
            ? absolute.ToString()
            : new Uri(new Uri(_config.BaseUrl), urlOrPath).ToString();

    // ---- fallback: drive the lesson through the page ----------------------

    private void RunLessonInPage(int lessonNumber)
    {
        var prompts = 0;
        while (prompts < _config.LessonPromptSafetyCap)
        {
            if (IsLessonFinished())
            {
                Console.WriteLine($"Lesson {lessonNumber} completed after {prompts} prompts.");
                return;
            }

            var state = WaitForStepOrLessonFinished();
            if (state.Kind == LessonStepKind.Finished)
            {
                Console.WriteLine($"Lesson {lessonNumber} completed after {prompts} prompts.");
                return;
            }

            if (state.Kind == LessonStepKind.ContinueOnly)
            {
                ContinueInformationalStep();
                continue;
            }

            prompts++;
            var promptText = TextNormalizer.Normalize(state.PromptElement!.Text);
            if (string.IsNullOrWhiteSpace(promptText))
            {
                throw new InvalidOperationException("The lesson prompt text was empty. Verify the lesson prompt selector in Selectors.cs.");
            }

            var candidates = ResolveCandidateAnswers(promptText);
            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No collected translation exists for lesson prompt '{promptText}'. The bot stopped safely without guessing.");
            }

            if (TryAnswerPromptWithCandidates(promptText, candidates, lessonNumber, prompts))
            {
                Console.WriteLine($"Lesson {lessonNumber} completed.");
                return;
            }
        }

        throw new InvalidOperationException(
            $"Lesson {lessonNumber} exceeded the safety cap of {_config.LessonPromptSafetyCap} prompts. Stopping to avoid an infinite loop.");
    }

    private bool TryAnswerPromptWithCandidates(
        string promptText,
        IReadOnlyList<string> candidateAnswers,
        int lessonNumber,
        int promptIndex)
    {
        for (var i = 0; i < candidateAnswers.Count; i++)
        {
            var answer = candidateAnswers[i];
            Console.WriteLine(candidateAnswers.Count > 1 || i > 0
                ? $"[Lesson {lessonNumber} · word {promptIndex}] {promptText} -> {answer}  (option {i + 1}/{candidateAnswers.Count})"
                : $"[Lesson {lessonNumber} · word {promptIndex}] {promptText} -> {answer}");

            var outcome = SubmitAnswerInPage(answer);
            if (outcome == LessonAnswerOutcome.Accepted)
            {
                return ContinueAfterAnswer(promptText);
            }

            if (outcome == LessonAnswerOutcome.Finished)
            {
                return true;
            }

            if (i == candidateAnswers.Count - 1)
            {
                throw new InvalidOperationException($"All {candidateAnswers.Count} stored answers were rejected for lesson prompt '{promptText}'.");
            }

            Console.WriteLine($"Answer '{answer}' was rejected for '{promptText}'. Trying the next stored translation for this exact prompt only.");
        }

        return false;
    }

    private LessonAnswerOutcome SubmitAnswerInPage(string answer)
    {
        var answerInput = _page.WaitUntilClickable(Selectors.LessonAnswerInput);
        if (!TryPopulateAnswerInput(answerInput, answer))
        {
            _page.ScrollIntoView(answerInput);
            answerInput.SendKeys(Keys.Control + "a");
            answerInput.SendKeys(Keys.Delete);
            answerInput.SendKeys(answer);
        }

        if (!TryClickContinueButton())
        {
            answerInput.SendKeys(Keys.Enter);
        }

        return WaitForAnswerOutcome();
    }

    /// <summary>
    /// Sets the value through the native setter and raises the events the page's framework
    /// listens for, so a controlled input actually registers the text.
    /// </summary>
    private bool TryPopulateAnswerInput(IWebElement answerInput, string answer)
    {
        try
        {
            return _page.ExecuteScript(PopulateAnswerScript, answerInput, answer) is true;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private bool TryClickContinueButton()
    {
        try
        {
            if (!_page.TryFindVisible(Selectors.LessonContinueButton, out var button) || button is not { Enabled: true })
            {
                return false;
            }

            _page.Click(button);
            return true;
        }
        catch (WebDriverException)
        {
            return false;
        }
    }

    private LessonAnswerOutcome WaitForAnswerOutcome()
    {
        var correctBy = Selectors.LessonFeedbackMarker.ToBy();
        var incorrectBy = Selectors.LessonIncorrectAnswerMarker.ToBy();

        var outcome = _page.Wait().Until<LessonAnswerOutcome?>(driver =>
        {
            if (IsLessonFinished())
            {
                return LessonAnswerOutcome.Finished;
            }

            if (_page.TryFindVisible(incorrectBy, out _))
            {
                return LessonAnswerOutcome.Rejected;
            }

            return _page.TryFindVisible(correctBy, out _) ? LessonAnswerOutcome.Accepted : null;
        });

        return outcome ?? throw new WebDriverTimeoutException("Timed out waiting for the lesson to accept or reject the submitted answer.");
    }

    private bool IsLessonFinished()
        => !_page.UrlContains("/learning/") || _page.TryFindVisible(Selectors.LessonFinishedMarker, out _);

    private bool ContinueAfterAnswer(string previousPromptText)
    {
        if (IsLessonFinished())
        {
            return true;
        }

        _page.Click(_page.WaitUntilClickable(Selectors.LessonContinueButton));
        _page.Wait().Until(_ =>
        {
            if (IsLessonFinished())
            {
                return true;
            }

            var state = ReadCurrentLessonStep();
            return state.Kind == LessonStepKind.Prompt
                && state.PromptElement is not null
                && !string.Equals(TextNormalizer.Normalize(state.PromptElement.Text), previousPromptText, StringComparison.OrdinalIgnoreCase);
        });

        return IsLessonFinished();
    }

    private void ContinueInformationalStep()
    {
        if (IsLessonFinished())
        {
            return;
        }

        _page.Click(_page.WaitUntilClickable(Selectors.LessonContinueButton));
        _page.Wait().Until(_ => IsLessonFinished() || ReadCurrentLessonStep().Kind != LessonStepKind.ContinueOnly);
    }

    private LessonStepState WaitForStepOrLessonFinished()
    {
        var state = _page.Wait().Until(_ =>
        {
            var current = ReadCurrentLessonStep();
            return current.Kind != LessonStepKind.Waiting ? current : null;
        });

        return state ?? new LessonStepState(LessonStepKind.Finished, null);
    }

    private LessonStepState ReadCurrentLessonStep()
    {
        if (IsLessonFinished())
        {
            return new LessonStepState(LessonStepKind.Finished, null);
        }

        var hasPrompt = _page.TryFindVisible(Selectors.LessonPrompt, out var prompt) && prompt is not null;
        var hasInput = _page.TryFindVisible(Selectors.LessonAnswerInput, out _);
        var hasContinue = _page.TryFindVisible(Selectors.LessonContinueButton, out _);

        if (hasPrompt && hasInput)
        {
            return new LessonStepState(LessonStepKind.Prompt, prompt);
        }

        return hasContinue && !hasInput
            ? new LessonStepState(LessonStepKind.ContinueOnly, null)
            : new LessonStepState(LessonStepKind.Waiting, null);
    }

    // ---- vocabulary lookup ------------------------------------------------

    private IReadOnlyList<string> ResolveCandidateAnswers(string prompt)
    {
        if (_vocabulary.TryGetValue(prompt, out var answers))
        {
            return answers;
        }

        if (_reverseVocabulary.TryGetValue(prompt, out var reversed))
        {
            Console.WriteLine($"Prompt '{prompt}' matched the reverse vocabulary lookup.");
            return reversed;
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildReverseVocabulary(
        IReadOnlyDictionary<string, IReadOnlyList<string>> vocabulary)
    {
        var reverse = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (prompt, answers) in vocabulary)
        {
            foreach (var answer in answers)
            {
                var key = TextNormalizer.Normalize(answer);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!reverse.TryGetValue(key, out var prompts))
                {
                    prompts = new List<string>(1);
                    reverse[key] = prompts;
                    seen[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                if (seen[key].Add(prompt))
                {
                    prompts.Add(prompt);
                }
            }
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(reverse.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, prompts) in reverse)
        {
            result[key] = prompts.AsReadOnly();
        }

        return result;
    }
}

internal enum LessonAnswerOutcome
{
    Accepted,
    Rejected,
    Finished
}

internal enum LessonStepKind
{
    Waiting,
    Prompt,
    ContinueOnly,
    Blocked,
    Finished
}

internal sealed record LessonStepState(LessonStepKind Kind, IWebElement? PromptElement);

internal sealed class LessonLimitReachedException : Exception
{
    public LessonLimitReachedException(string message)
        : base(message)
    {
    }
}
