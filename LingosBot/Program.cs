using System.Text;

namespace LingosBotApp;

internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("LingosBot");
        Console.WriteLine("Automates lessons on lingos.pl using Selenium and Google Chrome.");
        Console.WriteLine();

        var config = new AppConfig();
        var credentialStore = new CredentialStore(config);
        var browserFactory = new BrowserFactory();

        // First run: if no login is stored yet, set one up before showing the menu.
        EnsureCredentialsExist(credentialStore);

        return RunMainMenu(config, credentialStore, browserFactory);
    }

    private static int RunMainMenu(AppConfig config, CredentialStore credentialStore, BrowserFactory browserFactory)
    {
        while (true)
        {
            credentialStore.TryLoad(out var current);

            Console.WriteLine();
            Console.WriteLine("=== Main menu ===");
            Console.WriteLine(current is not null ? $"Signed in as: {current.Email}" : "No account set yet.");
            Console.WriteLine($"  [{config.MinLessonCount}+]   Run lessons (auto-picks Wyzwania challenges)");
            Console.WriteLine("  [R]    Change email / password");
            Console.WriteLine("  [Q]    Quit");
            Console.Write("> ");

            var input = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.Equals(input, "Q", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Goodbye.");
                return 0;
            }

            if (string.Equals(input, "R", StringComparison.OrdinalIgnoreCase))
            {
                ChangeCredentialsMenu(credentialStore);
                continue;
            }

            if (int.TryParse(input, out var lessonCount) &&
                lessonCount >= config.MinLessonCount)
            {
                RunBotAction(config, credentialStore, browserFactory, bot => bot.Run(lessonCount));
                continue;
            }

            Console.WriteLine(
                $"Sorry, I didn't get that. Enter a number ({config.MinLessonCount} or more), or C, R, or Q.");
        }
    }

    private static void RunBotAction(
        AppConfig config,
        CredentialStore credentialStore,
        BrowserFactory browserFactory,
        Action<LingosBot> action)
    {
        var bot = new LingosBot(config, browserFactory, credentialStore, () => PromptForCredentials(allowBack: false)!);

        try
        {
            action(bot);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"Something went wrong: {ex.Message}");
            Console.WriteLine("If no browser window was visible, check the 'diagnostics' folder for a saved screenshot.");
        }
    }

    // --- First-run setup -------------------------------------------------

    private static void EnsureCredentialsExist(CredentialStore credentialStore)
    {
        if (credentialStore.TryLoad(out var existing) && existing is not null)
        {
            return;
        }

        Console.WriteLine("Welcome! No login is saved yet, so let's set up your lingos.pl account.");
        var credentials = PromptForCredentials(allowBack: false)!;
        credentialStore.Save(credentials);
        Console.WriteLine($"Saved. You're set up as {credentials.Email}.");
    }

    // --- Change credentials (R) -----------------------------------------

    private static void ChangeCredentialsMenu(CredentialStore credentialStore)
    {
        while (true)
        {
            credentialStore.TryLoad(out var current);

            Console.WriteLine();
            Console.WriteLine("--- Change login ---");
            Console.WriteLine(current is not null ? $"Current email: {current.Email}" : "No login is saved yet.");
            Console.WriteLine("  [E]  Enter a new email & password");
            Console.WriteLine("  [B]  Back to the main menu (keep current login)");
            Console.Write("> ");

            var choice = (Console.ReadLine() ?? string.Empty).Trim();

            if (string.Equals(choice, "B", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("No changes made.");
                return;
            }

            if (string.Equals(choice, "E", StringComparison.OrdinalIgnoreCase))
            {
                var updated = PromptForCredentials(allowBack: true);
                if (updated is null)
                {
                    Console.WriteLine("Cancelled - your login was not changed.");
                    return;
                }

                credentialStore.Save(updated);
                Console.WriteLine($"Saved. You're now signed in as {updated.Email}.");
                return;
            }

            Console.WriteLine("Please press E or B.");
        }
    }

    // --- Shared prompts --------------------------------------------------

    private static AppCredentials? PromptForCredentials(bool allowBack)
    {
        Console.WriteLine();
        Console.WriteLine("Enter your lingos.pl credentials.");
        if (allowBack)
        {
            Console.WriteLine("(Type B and press Enter at the email prompt to go back without changing anything.)");
        }

        string email;
        while (true)
        {
            Console.Write("Email: ");
            var input = Console.ReadLine();
            if (input is null)
            {
                throw new InvalidOperationException(
                    "The app could not read the email from standard input. Run it interactively in a terminal.");
            }

            var trimmed = input.Trim();
            if (allowBack && string.Equals(trimmed, "B", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                email = trimmed;
                break;
            }

            Console.WriteLine("Email cannot be empty.");
        }

        string password;
        while (true)
        {
            Console.Write("Password: ");
            password = ReadPassword();
            if (!string.IsNullOrEmpty(password))
            {
                break;
            }

            Console.WriteLine("Password cannot be empty.");
        }

        Console.WriteLine();
        return new AppCredentials(email, password);
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? throw new InvalidOperationException(
                "The app could not read the password from standard input. Run it interactively in a terminal.");
        }

        var builder = new StringBuilder();

        while (true)
        {
            var keyInfo = Console.ReadKey(intercept: true);

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (!char.IsControl(keyInfo.KeyChar))
            {
                builder.Append(keyInfo.KeyChar);
            }
        }

        return builder.ToString();
    }
}
