namespace LingosBotApp;

// Shapes of the JSON that lingos.pl renders into #app[data-props] and returns from its
// endpoints. Only the fields this bot needs are declared; unknown fields are ignored.

internal sealed class StudentProps
{
    public string Page { get; set; } = string.Empty;

    public DashboardProps? Dashboard { get; set; }

    public WordsetsProps? Wordsets { get; set; }

    public WordsetDetailProps? Wordset { get; set; }
}

internal sealed class DashboardProps
{
    public GroupProps? CurrentGroup { get; set; }

    public List<GroupProps> Groups { get; set; } = new();

    public List<ChallengeProps> Challenges { get; set; } = new();

    public bool LessonLimitReached { get; set; }
}

internal sealed class GroupProps
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

internal sealed class ChallengeProps
{
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int Prize { get; set; }

    public int Target { get; set; }

    /// <summary>One of <c>available</c>, <c>in_progress</c> or <c>completed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    public int? Progress { get; set; }
}

internal sealed class WordsetsProps
{
    public GroupProps? CurrentGroup { get; set; }

    public List<WordsetProps> Wordsets { get; set; } = new();

    public bool LessonLimitReached { get; set; }
}

internal sealed class WordsetProps
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;
}

internal sealed class WordsetDetailProps
{
    public GroupProps? Group { get; set; }

    public WordsetProps? Wordset { get; set; }

    public List<FlashcardProps> Flashcards { get; set; } = new();

    public bool LessonLimitReached { get; set; }
}

internal sealed class FlashcardProps
{
    public int Id { get; set; }

    /// <summary>The foreign-language side - what the bot has to type during a lesson.</summary>
    public string Word { get; set; } = string.Empty;

    /// <summary>The Polish side - what the lesson shows as the prompt.</summary>
    public string Translation { get; set; } = string.Empty;
}

// ---- /learning/... ---------------------------------------------------------

internal sealed class LearningProps
{
    /// <summary><c>ok</c> when a lesson started, <c>error</c> when the site refused to start one.</summary>
    public string Status { get; set; } = string.Empty;

    public long LessonId { get; set; }

    public string LeaveUrl { get; set; } = string.Empty;

    /// <summary>
    /// Why the lesson could not start: <c>limit-exceeded</c>, <c>no-premium-access</c>,
    /// <c>incorrect-group-selected</c>, <c>no-group-available</c>, <c>no-wordset-access</c>
    /// or <c>not-enough-words</c>.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    public string CtaUrl { get; set; } = string.Empty;

    public LearningStep? Step { get; set; }
}

internal sealed class LearningStep
{
    /// <summary>One of <c>exercise</c>, <c>finished</c>, <c>change_wordset</c> or <c>select_level</c>.</summary>
    public string State { get; set; } = string.Empty;

    public LearningProgress? Progress { get; set; }

    public LearningExercise? Exercise { get; set; }

    /// <summary>Where to go once the lesson is finished.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Where to go to carry a <c>change_wordset</c> step through to the next wordset.</summary>
    public string ContinueUrl { get; set; } = string.Empty;
}

internal sealed class LearningProgress
{
    public int Current { get; set; }

    public int Total { get; set; }
}

internal sealed class LearningExercise
{
    /// <summary><c>flashcard</c> asks for a translation; <c>new_teacher</c> and <c>new_premium</c> just present a word.</summary>
    public string Type { get; set; } = string.Empty;

    public long Identifier { get; set; }

    /// <summary>The Polish prompt on a <c>flashcard</c> exercise.</summary>
    public string Main { get; set; } = string.Empty;

    public string Additional { get; set; } = string.Empty;

    public string Word { get; set; } = string.Empty;

    public string Translation { get; set; } = string.Empty;
}

internal sealed class LearningAnswerResult
{
    public LearningStep? Step { get; set; }

    /// <summary>Null when the exercise did not grade an answer; otherwise the verdict on the one just sent.</summary>
    public LearningFeedback? Feedback { get; set; }
}

internal sealed class LearningFeedback
{
    /// <summary>Greater than zero when the answer was accepted.</summary>
    public int Result { get; set; }

    public string Correct { get; set; } = string.Empty;

    public List<string> AdditionalAnswers { get; set; } = new();

    public string Sentence { get; set; } = string.Empty;
}

internal sealed class LearningAnswerRequest
{
    public long Identifier { get; set; }

    public string Answer { get; set; } = string.Empty;

    public bool Skip { get; set; }
}

internal sealed class GroupSwitchRequest
{
    public int GroupId { get; set; }
}
