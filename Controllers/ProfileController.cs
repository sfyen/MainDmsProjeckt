using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using DmsProjeckt.Data;
using DmsProjeckt.Service;

namespace DmsProjeckt.Controllers
{
    [Authorize]
    public class ProfileController : Controller
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly WebDavStorageService _webDav;

        public ProfileController(UserManager<ApplicationUser> userManager, WebDavStorageService webDav)
        {
            _userManager = userManager;
            _webDav = webDav;
        }

        [HttpGet("Profile/GetAvatar")]
        public async Task<IActionResult> GetAvatar()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            if (string.IsNullOrWhiteSpace(user.ProfilbildUrl))
            {
                return Redirect("/images/default-profile.png");
            }

            try 
            {
                var stream = await _webDav.DownloadStreamAsync(user.ProfilbildUrl);
                if (stream == null)
                {
                    return Redirect("/images/default-profile.png");
                }
                
                string contentType = "image/jpeg";
                if(user.ProfilbildUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) contentType = "image/png";
                if(user.ProfilbildUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) contentType = "image/gif";

                return File(stream, contentType);
            }
            catch
            {
                return Redirect("/images/default-profile.png");
            }
        }
    }
}
