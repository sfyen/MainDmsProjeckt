using DmsProjeckt.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using DmsProjeckt.Service; // Für FirebaseStorageService

public class EinstellungenModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ApplicationDbContext _context;
    private readonly WebDavStorageService _WebDav;
    private readonly EmailService _emailService;
    private readonly ILogger<EinstellungenModel> _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<EinstellungenModel>();
    public EinstellungenModel(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext context,
        WebDavStorageService WebDav,
        EmailService emailService)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _context = context;
        _WebDav = WebDav;
        _emailService = emailService;
    }

    [BindProperty]
    public SettingsViewModel Settings { get; set; }
    public List<NotificationTypeViewModel> NotificationTypes { get; set; } = new();
    [BindProperty]
    public IFormFile ProfileImage { get; set; } // Für den Upload
    
    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return NotFound();
        var notificationTypes = await _context.NotificationTypes.ToListAsync();
        var userNotifications = await _context.UserNotificationSettings
            .Include(x => x.NotificationType)
            .Where(n => n.UserId == user.Id)
            .ToListAsync();
        string hash = user.PasswordHash;
        // Mapping von Entities auf ViewModels:
        Settings = new SettingsViewModel
        {
            
            ProfilImageUrl = !string.IsNullOrWhiteSpace(user.ProfilbildUrl)
    ? user.ProfilbildUrl
    : "/images/default-profile.png",

            FirstName = user.Vorname,
            LastName = user.Nachname,
            BirthDate = user.Geburtsdatum,
            PhoneNumber = user.PhoneNumber,
            Email = user.Email,
            NotificationTypes = notificationTypes
                .Select(nt => new NotificationTypeViewModel
                {
                    Id = nt.Id,
                    Name = nt.Name,
                    Enabled = userNotifications.Any(un => un.NotificationTypeId == nt.Id)
                ? userNotifications.Any(un => un.NotificationTypeId == nt.Id && un.Enabled)
                : true
                    
                }).ToList()
        };
        var dueSetting = userNotifications
    .FirstOrDefault(n => n.NotificationType.Name == "Due" || n.NotificationType.Name == "Due email");
        if (dueSetting != null && dueSetting.AdvanceMinutes.HasValue)
            Settings.TaskDueWarningMinutes = dueSetting.AdvanceMinutes.Value;

        var dueWFSetting = userNotifications
            .FirstOrDefault(n => n.NotificationType.Name == "DueWF" || n.NotificationType.Name == "DueWFEmail");
        if (dueWFSetting != null && dueWFSetting.AdvanceMinutes.HasValue)
            Settings.WorkflowTaskDueWarningMinutes = dueWFSetting.AdvanceMinutes.Value;



        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return NotFound();

        // 🔄 Grunddaten aktualisieren
        user.Vorname = Settings.FirstName;
        user.Nachname = Settings.LastName;
        user.Email = Settings.Email;
        user.PhoneNumber = Settings.PhoneNumber;
        user.Geburtsdatum = Settings.BirthDate;

        // 📧 E-Mail ändern
        if (user.Email != Settings.Email)
        {
            var setEmailResult = await _userManager.SetEmailAsync(user, Settings.Email);
            if (!setEmailResult.Succeeded)
            {
                ModelState.AddModelError("", "E-Mail konnte nicht geändert werden.");
                return Page();
            }
        }

        // 🔑 Passwort ändern
        if (!string.IsNullOrEmpty(Settings.NewPassword))
        {
            if (string.IsNullOrEmpty(Settings.CurrentPassword))
            {
                ModelState.AddModelError("Settings.CurrentPassword", "Bitte geben Sie Ihr aktuelles Passwort ein.");
                return Page();
            }

            var passwordCheck = await _userManager.CheckPasswordAsync(user, Settings.CurrentPassword);
            if (!passwordCheck)
            {
                ModelState.AddModelError("Settings.CurrentPassword", "Das aktuelle Passwort ist falsch.");
                return Page();
            }

            var changeResult = await _userManager.ChangePasswordAsync(user, Settings.CurrentPassword, Settings.NewPassword);
            if (!changeResult.Succeeded)
            {
                foreach (var error in changeResult.Errors)
                    ModelState.AddModelError("", error.Description);
                return Page();
            }

            await _signInManager.RefreshSignInAsync(user);
        }

        // 🧹 Profilbild löschen
        if (Settings.DeleteProfilImage)
        {
            if (!string.IsNullOrEmpty(user.ProfilbildUrl))
            {
                try
                {
                    var relativePath = user.ProfilbildUrl.Replace(_WebDav.BaseUrl, "").Trim('/');
                    await _WebDav.DeleteFileAsync(relativePath);
                    _logger.LogInformation("🗑️ Altes Profilbild gelöscht: {Path}", relativePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("⚠️ Fehler beim Löschen des Profilbilds: {Msg}", ex.Message);
                }
            }
            user.ProfilbildUrl = "";
        }

        // 🖼️ Neues Profilbild hochladen (via WebDAV)
        if (ProfileImage != null && ProfileImage.Length > 0)
        {
            var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif" };
            if (!allowedTypes.Contains(ProfileImage.ContentType))
            {
                ModelState.AddModelError("", "Nur JPG, PNG oder GIF erlaubt.");
                return Page();
            }

            var firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";
            var fileName = $"{Guid.NewGuid()}_{Path.GetFileName(ProfileImage.FileName)}";
            var objectPath = $"profilbilder/{firma}/{fileName}";

            using var stream = ProfileImage.OpenReadStream();
            await _WebDav.UploadStreamAsync(stream, objectPath, ProfileImage.ContentType);

            var imageUrl = $"{_WebDav.BaseUrl.TrimEnd('/')}/{objectPath}".Replace("//", "/").Replace(":/", "://");
            user.ProfilbildUrl = imageUrl;

            _logger.LogInformation("📸 Neues Profilbild hochgeladen: {Url}", imageUrl);
        }

        // 🔔 Benachrichtigungseinstellungen aktualisieren
        var userNotifications = await _context.UserNotificationSettings
            .Where(n => n.UserId == user.Id)
            .Include(x => x.NotificationType)
            .ToListAsync();

        foreach (var notifVm in Settings.NotificationTypes)
        {
            var notifSetting = userNotifications
                .FirstOrDefault(n => n.NotificationTypeId == notifVm.Id);

            if (notifSetting != null)
            {
                notifSetting.Enabled = notifVm.Enabled;
            }
            else
            {
                _context.UserNotificationSettings.Add(new UserNotificationSetting
                {
                    UserId = user.Id,
                    NotificationTypeId = notifVm.Id,
                    Enabled = notifVm.Enabled
                });
            }
        }

        // ⏰ Optional : Erinnerungszeiten
        var due = userNotifications.FirstOrDefault(x => x.NotificationType.Name == "Due" || x.NotificationType.Name == "Due email");
        if (due != null) due.AdvanceMinutes = Settings.TaskDueWarningMinutes;

        var dueWf = userNotifications.FirstOrDefault(x => x.NotificationType.Name == "DueWF" || x.NotificationType.Name == "DueWFEmail");
        if (dueWf != null) dueWf.AdvanceMinutes = Settings.WorkflowTaskDueWarningMinutes;

        await _context.SaveChangesAsync();
        await _userManager.UpdateAsync(user);

        TempData["Success"] = "Profil erfolgreich aktualisiert.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostChangePasswordAsync([FromBody] ChangePasswordRequest req)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
            return new JsonResult(new { success = false, error = "Nicht angemeldet" }) { StatusCode = 401 };

        // Validiere das Passwort nochmal
        if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < 6)
            return new JsonResult(new { success = false, error = "Passwort zu kurz" }) { StatusCode = 400 };

        if (req.NewPassword != req.ConfirmPassword)
            return new JsonResult(new { success = false, error = "Passwörter stimmen nicht überein" }) { StatusCode = 400 };

        // Hier könntest du ggf. weitere Prüfungen machen (z.B. Passwortregeln)
        var changeResult = await _userManager.RemovePasswordAsync(user);
        if (!changeResult.Succeeded)
            return new JsonResult(new { success = false, error = "Fehler beim Entfernen des alten Passworts" }) { StatusCode = 400 };

        var addPwResult = await _userManager.AddPasswordAsync(user, req.NewPassword);
        if (!addPwResult.Succeeded)
            return new JsonResult(new { success = false, error = "Fehler beim Speichern des neuen Passworts" }) { StatusCode = 400 };

        await _signInManager.RefreshSignInAsync(user);
        string subject = "Passwort geändert";
        string body = $@"
            <p>Hallo {user.Vorname}</p>,
            <p>Ihr Passwort wurde soeben geändert</p>
            <p>Ihr Team!</p>";

        await _emailService.SendEmailAsync(user.Email, subject, body);

        return new JsonResult(new { success = true });
    }

    public class ChangePasswordRequest
    {
        public string NewPassword { get; set; }
        public string ConfirmPassword { get; set; }
    }

    public class SettingsViewModel
    {
        
       

       public string ProfilImageUrl { get; set; } 
        public string Email { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
       
        public string PhoneNumber { get; set; }
        public DateTime? BirthDate { get; set; }

        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; }

        [DataType(DataType.Password)]
        [MinLength(6, ErrorMessage = "Das neue Passwort muss mindestens 6 Zeichen lang sein.")]
        public string NewPassword { get; set; }

        [DataType(DataType.Password)]
        [Compare("NewPassword", ErrorMessage = "Die Passwörter stimmen nicht überein.")]
        public string ConfirmPassword { get; set; }

        public int TaskDueWarningMinutes { get; set; } = 60;
        public int WorkflowTaskDueWarningMinutes { get; set; } = 60;

        public List<NotificationTypeViewModel> NotificationTypes { get; set; } = new();
        public bool DeleteProfilImage { get; set; } = false;
    }
    public class NotificationTypeViewModel
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

}

