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
        if (_page.PostJson<StudentProps>(_config.GroupSwitchPath, new GroupSwitchRequest { GroupId = lessonClass.Id }) is null)
        {
            throw new InvalidOperationException($"Lingos did not accept switching to the class '{lessonClass.Name}'.");
        }

        // The dashboard was updated in place by the request above; reload it so the page markup
        // (the "Ucz sie" link in particular) matches the class that is now selected.
        _page.GoTo(_config.StudentDashboardUrl);
        _page.WaitForDocumentReady();
    }
}

internal sealed record ClassInfo(int Id, string Name);
