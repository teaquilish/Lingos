using System.Diagnostics;
using OpenQA.Selenium;

namespace LingosBotApp;

internal sealed class LingosBot
{
    private readonly AppConfig _config;
    private readonly BrowserFactory _browserFactory;
    private readonly CredentialStore _credentialStore;
    private readonly Func<AppCredentials> _credentialPrompt;

    /// <summary>What the bot is doing right now, so an error can say where it happened.</summary>
    private string _step = "starting";

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

    public void Run(int lessonCount)
    {
        var initialCredentials = ResolveCredentials(_credentialStore, _credentialPrompt);
        var completed = 0;
        IWebDriver? driver = null;

        try
        {
            Console.WriteLine("Starting Google Chrome...");
            _step = "starting Chrome";
            driver = _browserFactory.Create(_config);
            var page = new PageAgent(driver, _config);

            var total = Stopwatch.StartNew();
            _step = "logging in";
            LoginWithRetry(new LoginService(page), initialCredentials, _credentialStore, _credentialPrompt);

            var collecting = Stopwatch.StartNew();
            _step = "collecting vocabulary";
            var vocabulary = new VocabularyCollector(page).CollectVocabulary();
            collecting.Stop();

            if (vocabulary.Count == 0)
            {
                throw new InvalidOperationException(
                    "No vocabulary was collected from Zestawy. The messages above say what went wrong with each wordset.");
            }

            var answerCount = vocabulary.Sum(pair => pair.Value.Count);
            Console.WriteLine(
                $"Collected {vocabulary.Count} unique Polish prompts with {answerCount} stored answer candidates in {collecting.Elapsed:mm\\:ss}.");

            var lessonRunner = new LessonRunner(page, vocabulary);
            var challengeRunner = new ChallengeRunner(page);

            for (var lesson = 1; lesson <= lessonCount; lesson++)
            {
                _step = $"checking challenges before lesson {lesson}";
                EnsureChallengeSelected(challengeRunner);
                var elapsed = Stopwatch.StartNew();
                _step = $"running lesson {lesson}";
                lessonRunner.RunLesson(lesson);
                elapsed.Stop();
                Console.WriteLine($"Lesson {lesson} elapsed time: {elapsed.Elapsed:mm\\:ss}.");
                completed++;
            }

            total.Stop();
            Console.WriteLine($"Total run time: {total.Elapsed:mm\\:ss}.");
            Console.WriteLine("All requested lessons were completed.");
        }
        catch (LessonLimitReachedException ex)
        {
            Console.WriteLine($"{ex.Message} Completed lessons in this run: {completed}.");
        }
        catch (Exception ex) when (driver is not null)
        {
            Console.WriteLine($"Failed while {_step}.");
            SaveDiagnostics(driver, ex, _step);
            throw;
        }
        finally
        {
            CloseBrowser(driver, "Closing Chrome...");
        }
    }

    public void RunUsual()
    {
        Console.WriteLine("=== Usual mode: your account, all classes ===");
        RunAllClassesForAccount(_credentialStore, _credentialPrompt, "your account");
    }

    public void RunModified(CredentialStore secondaryStore, Func<AppCredentials> secondaryPrompt)
    {
        Console.WriteLine("=== Modified mode: account 1 of 2 ===");
        RunAllClassesForAccount(_credentialStore, _credentialPrompt, "account 1");
        Console.WriteLine();
        Console.WriteLine("=== Modified mode: account 2 of 2 ===");
        RunAllClassesForAccount(secondaryStore, secondaryPrompt, "account 2");
        Console.WriteLine();
        Console.WriteLine("Modified mode finished (both accounts).");
    }

    private void RunAllClassesForAccount(CredentialStore store, Func<AppCredentials> prompt, string label)
    {
        var credentials = ResolveCredentials(store, prompt);
        IWebDriver? driver = null;

        try
        {
            Console.WriteLine($"Starting Google Chrome for {label}...");
            _step = $"starting Chrome for {label}";
            driver = _browserFactory.Create(_config);
            var page = new PageAgent(driver, _config);

            _step = $"logging in to {label}";
            LoginWithRetry(new LoginService(page), credentials, store, prompt);

            _step = "reading the class list";
            var classSwitcher = new ClassSwitcher(page);
            var classes = classSwitcher.ReadClasses();

            if (classes.Count == 0)
            {
                Console.WriteLine("Lingos listed no classes for this account (it may be on a premium course) - "
                    + "running lessons on whatever is selected.");
                RunLessonsOnCurrentClass(page, _config.LessonsPerClass, "current class");
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
                    RunLessonsOnCurrentClass(page, _config.LessonsPerClass, lessonClass.Name);
                }
            }

            Console.WriteLine($"Finished all classes for {label}.");
        }
        catch (LessonLimitReachedException ex)
        {
            Console.WriteLine($"{ex.Message} Stopping {label}.");
        }
        catch (Exception ex) when (driver is not null)
        {
            Console.WriteLine($"Error during {label} while {_step}: {ex.Message}");
            SaveDiagnostics(driver, ex, _step);
        }
        finally
        {
            CloseBrowser(driver, $"Closing Chrome for {label}...");
        }
    }

    private void RunLessonsOnCurrentClass(PageAgent page, int lessonCount, string className)
    {
        _step = $"collecting vocabulary for '{className}'";
        var vocabulary = new VocabularyCollector(page).CollectVocabulary();
        if (vocabulary.Count == 0)
        {
            Console.WriteLine($"No vocabulary found for '{className}' - skipping it. The messages above say why.");
            return;
        }

        var lessonRunner = new LessonRunner(page, vocabulary);
        var challengeRunner = new ChallengeRunner(page);

        for (var lesson = 1; lesson <= lessonCount; lesson++)
        {
            _step = $"checking challenges before lesson {lesson} on '{className}'";
            EnsureChallengeSelected(challengeRunner);
            _step = $"running lesson {lesson} on '{className}'";
            lessonRunner.RunLesson(lesson);
            Console.WriteLine($"  [{className}] lesson {lesson}/{lessonCount} done.");
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

    private static void CloseBrowser(IWebDriver? driver, string message)
    {
        if (driver is null)
        {
            return;
        }

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

    private static void LoginWithRetry(
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
                return;
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
