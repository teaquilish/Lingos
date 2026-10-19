using OpenQA.Selenium;

namespace LingosBotApp;

/// <summary>
/// Reads the classes on the account and switches the dashboard between them.
/// </summary>
internal sealed class ClassSwitcher
{
    private readonly PageAgent _page;
    private readonly AppConfig _config;

    public ClassSwitcher(IWebDriver driver, AppConfig config)
        : this(new PageAgent(driver, config))
    {
    }

    public ClassSwitcher(PageAgent page)
    {
        _page = page;
        _config = page.Config;
    }

    public IReadOnlyList<ClassInfo> ReadClasses()
    {
        if (!_page.UrlContains("/student/dashboard"))
        {
            _page.GoTo(_config.StudentDashboardUrl);
        }

        var groups = _page.ReadPropsWhenReady<StudentProps>("the dashboard").Dashboard?.Groups;
        return groups?
            .Select(group => new ClassInfo(group.Id, TextNormalizer.Normalize(group.Name)))
            .ToList() ?? new List<ClassInfo>();
    }

    public void SwitchTo(ClassInfo lessonClass)
    {
        var request = new ContextSwitchRequest { Kind = ContextSwitchRequest.KindGroup, Id = lessonClass.Id };
        var result = _page.PostJson<DashboardProps>(_config.ContextSwitchPath, request);
        if (result.Value is null)
        {
            throw new InvalidOperationException(
                $"Lingos did not accept switching to the class '{lessonClass.Name}' (id {lessonClass.Id}): {result.Failure}");
        }

        // The dashboard was updated in place by the request above; reload it so the page markup
        // (the "Ucz sie" link in particular) matches the class that is now selected.
        _page.GoTo(_config.StudentDashboardUrl);
        _page.WaitForDocumentReady();
    }
}

internal sealed record ClassInfo(int Id, string Name);
