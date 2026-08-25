using DailyPosterGenerator.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DailyPosterGenerator.Controllers;

[Authorize]
public class LogsController : Controller
{
    private readonly IActivityLog _log;

    public LogsController(IActivityLog log)
    {
        _log = log;
    }

    public IActionResult Index()
    {
        return View(_log.GetRecent(200));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Clear()
    {
        _log.Clear();
        TempData["Success"] = "Logs cleared.";
        return RedirectToAction(nameof(Index));
    }
}
