using System.Diagnostics;
using OpenQA.Selenium;

namespace LingosBotApp;

internal sealed class LingosBot
{
    private readonly AppConfig _config;
    private readonly BrowserFactory _browserFactory;
    private readonly CredentialStore _credentialStore;
    private readonly Func<AppCredentials> _credentialPrompt;

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
            driver = _browserFactory.Create(_config);
            var page = new PageAgent(driver, _config);

            var total = Stopwatch.StartNew();
            LoginWithRetry(new LoginService(page), initialCredentials, _credentialStore, _credentialPrompt);

            var collecting = Stopwatch.StartNew();
            var vocabulary = new VocabularyCollector(page).CollectVocabulary();
            collecting.Stop();

            if (vocabulary.Count == 0)
            {
                throw new InvalidOperationException("No vocabulary was collected from Zestawy. Verify the selectors in Selectors.cs.");
            }

            var answerCount = vocabulary.Sum(pair => pair.Value.Count);
            Console.WriteLine(
                $"Collected {vocabulary.Count} unique Polish prompts with {answerCount} stored answer candidates in {collecting.Elapsed:mm\\:ss}.");

            var lessonRunner = new LessonRunner(page, vocabulary);
            var challengeRunner = new ChallengeRunner(page);

            for (var lesson = 1; lesson <= lessonCount; lesson++)
            {
                EnsureChallengeSelected(challengeRunner);
                var elapsed = Stopwatch.StartNew();
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
            SaveDiagnostics(driver, ex);
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
            driver = _browserFactory.Create(_config);
            var page = new PageAgent(driver, _config);

            LoginWithRetry(new LoginService(page), credentials, store, prompt);

            var classSwitcher = new ClassSwitcher(page);
            var classes = classSwitcher.ReadClasses();

            if (classes.Count == 0)
            {
                Console.WriteLine("No class switcher found - running lessons on the current class only.");
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
            SaveDiagnostics(driver, ex);
            Console.WriteLine($"Error during {label}: {ex.Message}");
        }
        finally
        {
            CloseBrowser(driver, $"Closing Chrome for {label}...");
        }
    }

    private void RunLessonsOnCurrentClass(PageAgent page, int lessonCount, string className)
    {
        var vocabulary = new VocabularyCollector(page).CollectVocabulary();
        if (vocabulary.Count == 0)
        {
            Console.WriteLine($"No vocabulary found for '{className}' - skipping it.");
            return;
        }

        var lessonRunner = new LessonRunner(page, vocabulary);
        var challengeRunner = new ChallengeRunner(page);

        for (var lesson = 1; lesson <= lessonCount; lesson++)
        {
            EnsureChallengeSelected(challengeRunner);
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

    private static void SaveDiagnostics(IWebDriver driver, Exception exception)
    {
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "diagnostics");
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            File.WriteAllText(Path.Combine(folder, $"page-{stamp}.html"), driver.PageSource);
            if (driver is ITakesScreenshot screenshotTaker)
            {
                screenshotTaker.GetScreenshot().SaveAsFile(Path.Combine(folder, $"page-{stamp}.png"));
            }

            File.WriteAllText(
                Path.Combine(folder, $"error-{stamp}.txt"),
                $"Timestamp: {DateTime.Now:O}{Environment.NewLine}Url: {driver.Url}{Environment.NewLine}Error: {exception}");

            Console.WriteLine($"Diagnostics saved to: {folder}");
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
