using DreamGrid.Launcher.Core.Logs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

public sealed class LogsModel(LogReader logs) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Source { get; set; }
    [BindProperty(SupportsGet = true)] public string? Level { get; set; }

    public IReadOnlyList<string> Sources { get; private set; } = [];
    public LogTail Tail { get; private set; } = new("", [], false);

    public void OnGet()
    {
        Sources = logs.Sources();
        if (Source is null || !Sources.Contains(Source)) Source = Sources[0];
        if (Level is not ("DEBUG" or "INFO" or "WARN" or "ERROR")) Level = "";
        Tail = logs.Tail(Source, 200, Level == "" ? null : Level);
    }
}
