using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DmsProjeckt.Pages.Workflows
{
    public class SecurityModel : PageModel
    {
        [BindProperty]
        public string Code { get; set; }

        public string Message { get; set; }

        public IActionResult OnPost()
        {
            const string accessCode = "mp1999";

            if (Code == accessCode)
            {
                HttpContext.Session.SetString("AccessGranted", "true");
                return RedirectToPage("/Willkommen");
            }

            Message = "❌ Ungültiger Code!";
            return Page();
        }
    }
}
