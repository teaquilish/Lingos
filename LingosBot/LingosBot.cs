using System.Diagnostics;
using OpenQA.Selenium;

namespace LingosBotApp;

internal sealed class LingosBot
{
    private const string CurrentClassName = "current class";

    private readonly AppConfig _config;
    private readonly BrowserFactory _browserFactory;
    private readonly CredentialStore _credentialStore;
    private readonly Func<AppCredentials> _credentialPrompt;

    /// <summary>What the bot is doing right now, so an error can say where it happened.</summary>
    private string _step = "starting";

    /// <summary>Lessons finished for the account being run, reported when the daily limit stops it.</summary>
    private int _completedLessons;

    public LingosBot(
        AppConfig config,
        BrowserFactory browserFactory,
        CredentialStore credentialStore,
        Func<AppCredentials> credentialPrompt)
    {
        _config = config;
        _browserFactory = browserFactory;
        _credentialStore = credentialStore;
        _credentialPrompt = credentialPrompt;
    }

    /// <summary>The <c>[number]</c> menu option: that many lessons on the class that is already selected.</summary>
    public void Run(int lessonCount)
    {
        RunAccount(_credentialStore, _credentialPrompt, "your account", (page, _) =>
        {
            RunLessons(page, lessonCount, CurrentClassName);
            Console.WriteLine("All requested lessons were completed.");
        });
    }

    public void RunUsual()
    {
        Console.WriteLine("=== Usual mode: your account, all classes ===");
        RunEveryClassForAccount(_credentialStore, _credentialPrompt, "your account");
    }

    public void RunModified(CredentialStore secondaryStore, Func<AppCredentials> secondaryPrompt)
    {
        Console.WriteLine("=== Modified mode: account 1 of 2 ===");
        RunEveryClassForAccount(_credentialStore, _credentialPrompt, "account 1");
        Console.WriteLine();
        Console.WriteLine("=== Modified mode: account 2 of 2 ===");
        RunEveryClassForAccount(secondaryStore, secondaryPrompt, "account 2");
        Console.WriteLine();
        Console.WriteLine("Modified mode finished (both accounts).");
    }

    private void RunEveryClassForAccount(CredentialStore store, Func<AppCredentials> prompt, string label)
        => RunAccount(store, prompt, label, (page, credentials) => RunEveryClass(page, credentials, label));

    /// <summary>
    /// Starts Chrome, signs in to one account and hands the signed-in page to <paramref name="work"/>.
    /// Every menu option goes through here, so they all stop on the daily limit, report errors and
    /// save diagnostics the same way. An error ends this account only, so Modified mode still moves
    /// on to the second account.
    /// </summary>
    private void RunAccount(
        CredentialStore store,
        Func<AppCredentials> prompt,
        string label,
        Action<PageAgent, AppCredentials> work)
    {
        var credentials = ResolveCredentials(store, prompt);
        var total = Stopwatch.StartNew();
        _completedLessons = 0;
        IWebDriver? driver = null;

        try
        {
            Console.WriteLine($"Starting Google Chrome for {label}...");
            _step = $"starting Chrome for {label}";
            driver = _browserFactory.Create(_config);
            var page = new PageAgent(driver, _config);

            _step = $"logging in to {label}";
            credentials = LoginWithRetry(new LoginService(page), credentials, store, prompt);

            work(page, credentials);
        }
        catch (LessonLimitReachedException ex)
        {
            Console.WriteLine($"{ex.Message} Stopping {label} after {_completedLessons} lesson(s) in this run.");
        }
        catch (Exception ex) when (driver is not null)
        {
            Console.WriteLine($"Error during {label} while {_step}: {ex.Message}");
            SaveDiagnostics(driver, ex, _step);
        }
        finally
        {
            if (driver is not null)
            {
                CloseBrowser(driver, $"Closing Chrome for {label}...");
                Console.WriteLine($"Total run time for {label}: {total.Elapsed:mm\\:ss}.");
            }
        }
    }

    private void RunEveryClass(PageAgent page, AppCredentials credentials, string label)
    {
        _step = "reading the class list";
        var classSwitcher = new ClassSwitcher(page);
        var classes = classSwitcher.ReadClasses();

        if (classes.Count == 0)
        {
            Console.WriteLine("Lingos listed no classes for this account (it may be on a premium course) - "
                + "running lessons on whatever is selected.");
            RunLessons(page, _config.LessonsPerClass, CurrentClassName);
        }
        else
        {
            Console.WriteLine($"Found {classes.Count} class(es) for {credentials.Email}.");
            var index = 0;
            foreach (var lessonClass in classes)
            {
                index++;
                Console.WriteLine();
                Console.WriteLine($"--- Class {index}/{classes.Count}: {lessonClass.Name} ---");
                _step = $"switching to class '{lessonClass.Name}'";
                classSwitcher.SwitchTo(lessonClass);
                RunLessons(page, _config.LessonsPerClass, lessonClass.Name);
            }
        }

        Console.WriteLine($"Finished all classes for {label}.");
    }

    /// <summary>Collects the selected class's vocabulary, then runs <paramref name="lessonCount"/> lessons on it.</summary>
    private void RunLessons(PageAgent page, int lessonCount, string className)
    {
        _step = $"collecting vocabulary for '{className}'";
        var collecting = Stopwatch.StartNew();
        var vocabulary = new VocabularyCollector(page).CollectVocabulary();
        if (vocabulary.Count == 0)
        {
            Console.WriteLine($"No vocabulary found for '{className}' - skipping it.");
            return;
        }

        var answerCount = vocabulary.Sum(pair => pair.Value.Count);
        Console.WriteLine(
            $"Collected {vocabulary.Count} unique Polish prompts with {answerCount} stored answer candidates in {collecting.Elapsed:mm\\:ss}.");

        var lessonRunner = new LessonRunner(page, vocabulary);
        var challengeRunner = new ChallengeRunner(page);

        for (var lesson = 1; lesson <= lessonCount; lesson++)
        {
            _step = $"checking challenges before lesson {lesson} on '{className}'";
            EnsureChallengeSelected(challengeRunner);

            _step = $"running lesson {lesson} on '{className}'";
            var elapsed = Stopwatch.StartNew();
            lessonRunner.RunLesson(lesson);
            _completedLessons++;
            Console.WriteLine($"  [{className}] lesson {lesson}/{lessonCount} done in {elapsed.Elapsed:mm\\:ss}.");
        }
    }

    private static void EnsureChallengeSelected(ChallengeRunner challengeRunner)
    {
        try
        {
            var snapshot = challengeRunner.ReadChallenges();
            if (snapshot.HasActive)
            {
                Console.WriteLine($"Challenge in progress: '{snapshot.Active!.Title}' - this lesson counts toward it.");
                return;
            }

            var best = snapshot.BestAvailable;
            if (best is not null)
            {
                Console.WriteLine($"Picking challenge '{best.Title}' (worth {best.Points} pkt)...");
                challengeRunner.Join(best);
            }
            else
            {
                Console.WriteLine("No challenge available to pick right now - running a normal lesson.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not check challenges ({ex.Message}). Continuing with the lesson anyway.");
        }
    }

    private static void CloseBrowser(IWebDriver driver, string message)
    {
        Console.WriteLine(message);
        try
        {
            driver.Quit();
            driver.Dispose();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Chrome cleanup reported an error: {ex.Message}");
        }
    }

    private static void SaveDiagnostics(IWebDriver driver, Exception exception, string step)
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var errorFile = Path.Combine(folder, $"error-{stamp}.txt");

            // The error file goes first: it is the most useful part, and the page or screenshot can fail.
            File.WriteAllText(
                errorFile,
                $"Timestamp: {DateTime.Now:O}{Environment.NewLine}Step: {step}{Environment.NewLine}"
                + $"Url: {driver.Url}{Environment.NewLine}Error: {exception}");

            File.WriteAllText(Path.Combine(folder, $"page-{stamp}.html"), driver.PageSource);
            if (driver is ITakesScreenshot screenshotTaker)
            {
                screenshotTaker.GetScreenshot().SaveAsFile(Path.Combine(folder, $"page-{stamp}.png"));
            }

            Console.WriteLine($"Diagnostics (error, page and screenshot) saved to: {errorFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not save diagnostics: {ex.Message}");
        }
    }

    private static AppCredentials ResolveCredentials(CredentialStore store, Func<AppCredentials> prompt)
    {
        if (store.TryLoad(out var credentials) && credentials is not null)
        {
            Console.WriteLine($"Loaded saved credentials for {credentials.Email}.");
            return credentials;
        }

        Console.WriteLine("No saved credentials were found.");
        return prompt();
    }

    /// <summary>Logs in, asking once for new credentials if the saved ones fail. Returns the ones that worked.</summary>
    private static AppCredentials LoginWithRetry(
        LoginService loginService,
        AppCredentials initialCredentials,
        CredentialStore store,
        Func<AppCredentials> prompt)
    {
        var credentials = initialCredentials;
        var retried = false;

        while (true)
        {
            try
            {
                loginService.Login(credentials);
                store.Save(credentials);
                return credentials;
            }
            catch (LoginFailedException ex) when (!retried)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine("Please enter the credentials again. They will be saved after a successful login.");
                credentials = prompt();
                retried = true;
            }
            catch (LoginFailedException)
            {
                store.Delete();
                throw;
            }
        }
    }
}
