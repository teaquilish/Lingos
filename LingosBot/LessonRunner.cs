using System.Globalization;
using OpenQA.Selenium;

namespace LingosBotApp;

/// <summary>
/// Runs one lesson at a time: open the dashboard, start a lesson, then answer each prompt from
/// the collected vocabulary through the site's own answer endpoint.
/// </summary>
internal sealed class LessonRunner
{
    private const string StateExercise = "exercise";
    private const string StateFinished = "finished";
    private const string StateChangeWordset = "change_wordset";
    private const string StateSelectLevel = "select_level";
    private const string TypeFlashcard = "flashcard";
    private const string TypeNewTeacher = "new_teacher";
    private const string TypeNewPremium = "new_premium";
    private const string StatusOk = "ok";
    private const string ReasonLimitExceeded = "limit-exceeded";

    private readonly PageAgent _page;
    private readonly AppConfig _config;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _vocabulary;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _reverseVocabulary;

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
        PlayLesson(lessonNumber, props.LessonId, props.Step!);
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
            throw new LessonLimitReachedException();
        }
    }

    private LearningProps StartLesson()
    {
        Console.WriteLine("Starting a lesson from the main page...");
        var lastProblem = "Lingos did not open a lesson.";
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

                lastProblem = $"the lesson page reported status '{props.Status}' but no first exercise.";
            }
            catch (WebDriverTimeoutException ex) when (attempt < 3)
            {
                lastProblem = ex.Message;
            }

            Console.WriteLine($"Lingos did not open a lesson on that click ({lastProblem}) Retrying...");
            _page.GoTo(_config.StudentDashboardUrl);
            _page.WaitForDocumentReady();
            ThrowIfDailyLimitReached();
        }

        throw new WebDriverTimeoutException($"Could not enter a lesson after 3 tries. Last problem: {lastProblem}");
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
            throw new LessonLimitReachedException();
        }

        if (!string.IsNullOrWhiteSpace(props.Reason))
        {
            throw new InvalidOperationException(
                $"Lingos refused to start a lesson: {DescribeRefusal(props.Reason)} ('{props.Reason}').");
        }
    }

    /// <summary>The reasons the lesson page can give for refusing to start, as the site itself explains them.</summary>
    private static string DescribeRefusal(string reason) => reason switch
    {
        "no-premium-access" => "this needs a Lingos Premium account",
        "incorrect-group-selected" => "the selected class is not available to this account",
        "no-group-available" => "this account is not in any class",
        "no-wordset-access" => "this account has no access to the selected wordset",
        "not-enough-words" => "the teacher has assigned fewer than 20 words to this class",
        _ => "a reason the bot does not know yet"
    };

    // ---- playing the lesson -----------------------------------------------

    /// <summary>
    /// Plays the lesson through <c>POST /learning/answer/{lessonId}</c>, which is what the page's
    /// own script does. Throws with the reason when Lingos answers with something unexpected.
    /// </summary>
    private void PlayLesson(int lessonNumber, long lessonId, LearningStep step)
    {
        var answerUrl = AnswerUrl(lessonId);

        var attempts = 0;
        var newWordCards = 0;
        var rejectedAnswers = 0;
        var currentIdentifier = 0L;
        var candidateIndex = 0;

        while (attempts < _config.LessonPromptSafetyCap)
        {
            switch (step.State)
            {
                case StateFinished:
                    Console.WriteLine($"Lesson {lessonNumber} completed after {attempts} prompts.");
                    return;

                case StateChangeWordset:
                {
                    if (string.IsNullOrWhiteSpace(step.ContinueUrl))
                    {
                        throw new InvalidOperationException(
                            "Lingos asked to move on to another wordset but gave no address to continue at.");
                    }

                    Console.WriteLine("Lingos asked to carry the lesson on into the next wordset. Continuing...");
                    _page.GoTo(ToAbsoluteUrl(step.ContinueUrl));

                    var continued = ReadLearningProps();
                    ThrowIfLessonBlocked(continued);
                    if (!string.Equals(continued.Status, StatusOk, StringComparison.Ordinal) || continued.Step is null)
                    {
                        throw new InvalidOperationException($"The next wordset reported status '{continued.Status}' but no exercise.");
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
                    throw new InvalidOperationException(
                        $"Lingos returned an unfamiliar lesson step ('{step.State}'), so it has probably changed how lessons work.");
            }

            var exercise = step.Exercise
                ?? throw new InvalidOperationException("Lingos returned an exercise step without the exercise itself.");

            if (exercise.Identifier != currentIdentifier)
            {
                currentIdentifier = exercise.Identifier;
                candidateIndex = 0;
            }

            attempts++;
            if (!string.Equals(exercise.Type, TypeFlashcard, StringComparison.Ordinal))
            {
                newWordCards++;
            }

            var request = BuildAnswerRequest(exercise, lessonNumber, attempts, candidateIndex, out var promptText);

            var response = _page.PostJson<LearningAnswerResult>(answerUrl, request);
            var result = response.Value
                ?? throw new InvalidOperationException($"The lesson answer request did not go through: {response.Failure}");
            if (result.Step is null)
            {
                throw new InvalidOperationException("Lingos accepted the answer but did not say what comes next.");
            }

            if (result.Feedback is { Result: <= 0 })
            {
                candidateIndex++;
                rejectedAnswers++;
                ReportRejectedAnswer(promptText, request.Answer, result.Feedback, result.Step, exercise.Identifier);
            }

            step = result.Step;
        }

        throw new InvalidOperationException(
            $"Lesson {lessonNumber} exceeded the safety cap of {_config.LessonPromptSafetyCap} prompts "
            + $"({newWordCards} new-word cards, {rejectedAnswers} rejected answers; Lingos last reported "
            + $"progress {step.Progress?.Current}/{step.Progress?.Total}, step '{step.State}', "
            + $"exercise '{step.Exercise?.Type}'). Stopping to avoid an infinite loop.");
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
            if (!string.Equals(exercise.Type, TypeNewTeacher, StringComparison.Ordinal)
                && !string.Equals(exercise.Type, TypeNewPremium, StringComparison.Ordinal))
            {
                Console.WriteLine($"Lingos sent an unfamiliar exercise type '{exercise.Type}'. Treating it as a new-word card.");
            }

            Console.WriteLine($"[Lesson {lessonNumber} · word {promptIndex}] new word: {TextNormalizer.Normalize(exercise.Translation)}");
            return new LearningAnswerRequest { Identifier = exercise.Identifier, Answer = string.Empty, Skip = false };
        }

        if (string.IsNullOrWhiteSpace(promptText))
        {
            throw new InvalidOperationException(
                $"Lingos sent a flashcard with an empty prompt (exercise {exercise.Identifier}), so the lesson data has probably changed.");
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

internal sealed class LessonLimitReachedException : Exception
{
    public LessonLimitReachedException()
        : base("Lingos reported that today's lesson limit has been reached for this account. The bot stopped cleanly.")
    {
    }
}
