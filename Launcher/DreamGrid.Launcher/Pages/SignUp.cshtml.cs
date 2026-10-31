using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DreamGrid.Launcher.Pages;

/// <summary>
/// Where "Create an account" leads. Open to anyone and, like the other guest pages, not there on a Region Server or a Connected
/// Server (<c>GuestSideOnly</c>). Until sign-up is built it only says that the grid isn't open for sign-ups.
/// </summary>
public sealed class SignUpModel : PageModel
{
    public void OnGet()
    {
    }
}
