using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SpotifyRemote.Models;

namespace SpotifyRemote.Controllers;

public class HomeController : Controller
{
    public IActionResult Index(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error)
    {
        if (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(error))
            return Redirect($"/auth/callback?code={Uri.EscapeDataString(code ?? "")}&state={Uri.EscapeDataString(state ?? "")}&error={Uri.EscapeDataString(error ?? "")}");

        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
