using System.Security.Claims;
using DmsProjeckt.Data;
using DmsProjeckt.Service;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

public class StepDetailModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AuditLogService _auditLogService;
    private readonly WebDavStorageService _WebDav;
    private readonly ILogger<StepDetailModel> _logger;
    public StepDetailModel(ApplicationDbContext db, UserManager<ApplicationUser> userManager, AuditLogService auditLogService, WebDavStorageService WebDav, ILogger<StepDetailModel> logger)
    {
        _db = db;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _WebDav = WebDav;
        _logger = logger;
    }

    [BindProperty]
    public StepDetailViewModel VM { get; set; } = new();
    public List<ApplicationUser> BenutzerListe { get; set; } = new();


    [BindProperty]
    public string Kommentar { get; set; }

    [BindProperty]
    public int StepId { get; set; }
    public string CurrentUserId { get; set; }
    public int? OffenesKommentarStepId { get; set; }
    public async Task<IActionResult> OnGetAsync(int workflowId, int stepId)
    {
        // 🔹 Aktuellen Benutzer laden
        CurrentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        // 🔹 Benutzerliste laden
        var currentUser = await _userManager.GetUserAsync(User);

        BenutzerListe = await _userManager.Users
            .Where(u => u.AdminId == currentUser.AdminId && u.Id != currentUser.Id)
            .OrderBy(u => u.Vorname)
            .ToListAsync();
  



        // 🔹 Workflow + Steps laden
        var workflow = await _db.Workflows
            .Include(w => w.Steps.OrderBy(s => s.Order))
            .ThenInclude(s => s.AssignedToUser)
            .Include(w => w.CreatedByUser)
            .FirstOrDefaultAsync(w => w.Id == workflowId);

        if (workflow == null)
            return NotFound();

        // 🔹 Steps sortieren
        var steps = workflow.Steps.OrderBy(s => s.Order).ToList();
        var stepIds = steps.Select(s => s.Id).ToList();

        // 🔹 Workflow-Dokumente (nur Versionen, keine Originale)
        var alledokumente = await _db.Dokumente
            .Where(d => d.WorkflowId == workflowId && d.IsVersion)
            .ToListAsync();

        // 🔹 Step-Dokumente (mit StepId vorhanden)
        var dokumente = await _db.Dokumente
            .Where(d => d.WorkflowId == workflowId && d.StepId != null && stepIds.Contains((int)d.StepId))
            .ToListAsync();

        // ✅ Dictionary<int, List<Dokumente>> mit Null-Handling
        var dokDict = dokumente
            .GroupBy(d => d.StepId ?? 0) // Null = 0 = workflowweite Dokumente
            .ToDictionary(g => g.Key, g => g.ToList());

        // 🔹 Step-Kommentare inkl. Benutzer laden
        var kommentare = await _db.StepKommentare
            .Where(k => stepIds.Contains(k.StepId))
            .Include(k => k.User)
            .ToListAsync();

        // ✅ Dictionary<int, List<StepKommentar>>
        var kommDict = kommentare
            .GroupBy(k => k.StepId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 🔹 Abteilungsauswahl: Anzeigetext und wer erledigt hat ermitteln
        var stepAssignmentDisplay = new Dictionary<int, string>();
        var stepCompletedByUser = new Dictionary<int, ApplicationUser>();
        
        foreach (var step in steps)
        {
            // Prüfen ob Abteilung oder einzelner User
            if (step.UserIds != null && step.UserIds.Any())
            {
                // Abteilungszuweisung: Abteilungsname ermitteln
                var firstUserId = step.UserIds.First();
                var firstUser = await _db.Users.Include(u => u.Abteilung).FirstOrDefaultAsync(u => u.Id == firstUserId);
                if (firstUser?.Abteilung != null)
                {
                    stepAssignmentDisplay[step.Id] = $"{firstUser.Abteilung.Name} Abteilung 👥";
                }
                else
                {
                    stepAssignmentDisplay[step.Id] = $"{step.UserIds.Count} Benutzer 👥";
                }
                
                // Prüfen wer erledigt hat (erledigte Aufgabe finden)
                if (step.Completed)
                {
                    var completedAufgabe = await _db.Aufgaben
                        .Include(a => a.FuerUserNavigation)
                        .ThenInclude(u => u.Abteilung)
                        .FirstOrDefaultAsync(a => a.StepId == step.Id && a.Erledigt);
                    
                    if (completedAufgabe?.FuerUserNavigation != null)
                    {
                        stepCompletedByUser[step.Id] = completedAufgabe.FuerUserNavigation;
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(step.UserId))
            {
                // Einzelner Benutzer
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == step.UserId);
                if (user != null)
                {
                    stepAssignmentDisplay[step.Id] = $"{user.Vorname} {user.Nachname}";
                }
            }
        }

        // 🔹 ViewModel befüllen
        VM.Workflow = workflow;
        VM.Steps = steps;
        VM.StepDokumente = dokDict;
        VM.StepKommentare = kommDict;
        VM.AktuellerStepId = stepId;
        VM.AktuellerUserId = _userManager.GetUserId(User);
        VM.Dokumente = alledokumente;
        VM.StepAssignmentDisplay = stepAssignmentDisplay;
        VM.StepCompletedByUser = stepCompletedByUser;

        return Page();
    }

    // Kommentar speichern
    public async Task<IActionResult> OnPostAsync(int workflowId, int stepId)
    {
        if (string.IsNullOrWhiteSpace(Kommentar))
            return RedirectToPage(new { workflowId, stepId });
        OffenesKommentarStepId = stepId;
        var user = await _userManager.GetUserAsync(User);
        var kommentar = new StepKommentar
        {
            StepId = stepId,
            UserId = user.Id,
            UserName = user.Vorname + " " + user.Nachname,
            Text = Kommentar,
            CreatedAt = DateTime.UtcNow
        };
        _db.StepKommentare.Add(kommentar);
        await _db.SaveChangesAsync();

        return RedirectToPage(new { workflowId, stepId });
    }

    // Step erledigen
    public async Task<IActionResult> OnPostErledigenAsync(int workflowId, int stepId)
    {
        var step = await _db.Steps.FindAsync(stepId);
        if (step == null) return NotFound();
        var workflow = await _db.Workflows.FindAsync(workflowId);
        var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        
        // 🔹 Prüfen ob User berechtigt ist (entweder step.UserId ODER in step.UserIds)
        bool isAuthorized = (step.UserId == currentUserId) || 
                           (step.UserIds != null && step.UserIds.Contains(currentUserId));
        
        if (!isAuthorized) return Forbid();
        
        step.Completed = true;
        
        // Die zugehörige Aufgabe für DIESEN BENUTZER erledigen
        var aufgabe = await _db.Aufgaben
            .FirstOrDefaultAsync(a => a.StepId == stepId && a.FuerUser == currentUserId);
        
        if (aufgabe != null)
        {
            aufgabe.Erledigt = true;
        }

        // Optional: Nächste Aufgabe aktivieren
        var nextStep = await _db.Steps
            .Where(s => s.WorkflowId == step.WorkflowId && s.Order == step.Order + 1)
            .FirstOrDefaultAsync();

        if (nextStep != null && !nextStep.TaskCreated)
        {
            // Prüfen ob nächster Step Abteilung oder einzelner User ist
            if (nextStep.UserIds != null && nextStep.UserIds.Any())
            {
                // Abteilung: Aufgaben für alle Benutzer erstellen
                foreach (var userId in nextStep.UserIds)
                {
                    var neueAufgabe = new Aufgaben
                    {
                        Titel = nextStep.Kategorie,
                        Beschreibung = nextStep.Description,
                        FaelligBis = nextStep.DueDate ?? DateTime.Today.AddDays(3),
                        Prioritaet = 1,
                        VonUser = aufgabe?.VonUser ?? workflow.UserId,
                        FuerUser = userId,
                        Erledigt = false,
                        ErstelltAm = DateTime.Now,
                        StepId = nextStep.Id,
                        WorkflowId = workflow.Id,
                        Aktiv = true
                    };
                    _db.Aufgaben.Add(neueAufgabe);
                }
                nextStep.TaskCreated = true;
            }
            else if (!string.IsNullOrEmpty(nextStep.UserId))
            {
                // Einzelner Benutzer
                var neueAufgabe = new Aufgaben
                {
                    Titel = nextStep.Kategorie,
                    Beschreibung = nextStep.Description,
                    FaelligBis = nextStep.DueDate ?? DateTime.Today.AddDays(3),
                    Prioritaet = 1,
                    VonUser = aufgabe?.VonUser ?? workflow.UserId,
                    FuerUser = nextStep.UserId,
                    Erledigt = false,
                    ErstelltAm = DateTime.Now,
                    StepId = nextStep.Id,
                    WorkflowId = workflow.Id,
                    Aktiv = true
                };
                _db.Aufgaben.Add(neueAufgabe);
                nextStep.TaskCreated = true;
            }
        }
        else if (nextStep != null && nextStep.TaskCreated)
        {
            // Aufgaben für nächsten Step aktivieren
            var nextAufgaben = await _db.Aufgaben
                .Where(a => a.StepId == nextStep.Id && a.WorkflowId == nextStep.WorkflowId && !a.Aktiv && !a.Erledigt)
                .ToListAsync();

            foreach (var nextAufgabe in nextAufgaben)
            {
                nextAufgabe.Aktiv = true;
                _db.Update(nextAufgabe);
            }
        }
        
        // 🔹 Benachrichtigungen für nächsten Step
        var notificationType = await _db.NotificationTypes
            .FirstOrDefaultAsync(n => n.Name == "Workflowaufgabe");
        
        if (notificationType != null && nextStep != null)
        {
            // Liste der Benutzer für Benachrichtigung
            var usersToNotify = new List<string>();
            
            if (nextStep.UserIds != null && nextStep.UserIds.Any())
            {
                usersToNotify.AddRange(nextStep.UserIds);
            }
            else if (!string.IsNullOrEmpty(nextStep.UserId))
            {
                usersToNotify.Add(nextStep.UserId);
            }
            
            // Benachrichtigungen senden
            foreach (var userId in usersToNotify)
            {
                var setting = await _db.UserNotificationSettings
                    .FirstOrDefaultAsync(s => s.UserId == userId && s.NotificationTypeId == notificationType.Id);

                if (setting == null || setting.Enabled)
                {
                    var notification = new Notification
                    {
                        Title = "Neue Aufgabe zugewiesen",
                        Content = $"Du hast eine neue Aufgabe im Workflow \"{workflow.Title}\" erhalten.",
                        CreatedAt = DateTime.UtcNow,
                        NotificationTypeId = notificationType.Id,
                        ActionLink = $"/Workflows/StepDetail/{workflow.Id}/{nextStep.Id}"
                    };
                    _db.Notifications.Add(notification);
                    await _db.SaveChangesAsync();

                    var userNotification = new UserNotification
                    {
                        UserId = userId,
                        NotificationId = notification.Id,
                        IsRead = false,
                        ReceivedAt = DateTime.UtcNow
                    };
                    _db.UserNotifications.Add(userNotification);
                    await _db.SaveChangesAsync();
                }
            }
        }
        
        // 🔹 Alte Benachrichtigung als gelesen markieren
        var erstelltType = await _db.NotificationTypes.FirstOrDefaultAsync(n => n.Name == "Workflowaufgabe");
        if (erstelltType != null)
        {
            var aufgabenNotification = await _db.UserNotifications
                .Include(un => un.Notification)
                .Where(un =>
                    un.UserId == currentUserId &&
                    !un.IsRead &&
                    un.Notification.NotificationTypeId == erstelltType.Id)
                .OrderBy(un => un.ReceivedAt)
                .FirstOrDefaultAsync();

            if (aufgabenNotification != null)
            {
                aufgabenNotification.IsRead = true;
                await _db.SaveChangesAsync();
            }
        }
        
        // 🔹 Workflow-Ersteller benachrichtigen
        var notificationType2 = await _db.NotificationTypes
            .FirstOrDefaultAsync(n => n.Name == "Workflow erledigt");
        var setting2 = await _db.UserNotificationSettings
            .FirstOrDefaultAsync(s => s.UserId == workflow.UserId && s.NotificationTypeId == notificationType2.Id);
        
        if (setting2 == null || setting2.Enabled)
        {
            var notification = new Notification
            {
                Title = "Aufgabe erledigt",
                Content = $"Im von dir erstellten Workflow \"{workflow.Title}\" wurde Aufgabe {step.Order + 1} erledigt.",
                CreatedAt = DateTime.UtcNow,
                NotificationTypeId = notificationType2.Id
            };
            _db.Notifications.Add(notification);
            await _db.SaveChangesAsync();

            var userNotification = new UserNotification
            {
                UserId = workflow.UserId,
                NotificationId = notification.Id,
                IsRead = false,
                ReceivedAt = DateTime.UtcNow
            };
            _db.UserNotifications.Add(userNotification);
            await _db.SaveChangesAsync();
        }
        
        await _db.SaveChangesAsync();
        
        // 🔹 Wenn letzter Step: Workflow-abgeschlossen-Benachrichtigung
        if (nextStep == null)
        {
            var notificationTypee = await _db.NotificationTypes
                .FirstOrDefaultAsync(n => n.Name == "Workflow done");
            var setting = await _db.UserNotificationSettings
                .FirstOrDefaultAsync(s => s.UserId == workflow.UserId && s.NotificationTypeId == notificationTypee.Id);
            
            if (setting == null || setting.Enabled)
            {
                var notification = new Notification
                {
                    Title = "Workflow abgeschlossen",
                    Content = $"Der Workflow \"{workflow.Title}\" wurde erfolgreich abgeschlossen.",
                    CreatedAt = DateTime.UtcNow,
                    NotificationTypeId = notificationTypee.Id
                };
                _db.Notifications.Add(notification);
                await _db.SaveChangesAsync();

                var userNotification = new UserNotification
                {
                    UserId = workflow.UserId,
                    NotificationId = notification.Id,
                    IsRead = false,
                    ReceivedAt = DateTime.UtcNow
                };
                _db.UserNotifications.Add(userNotification);
                await _db.SaveChangesAsync();
            }
        }
        
        // Nach erledigen weiterleiten
        return RedirectToPage("/Workflows/Index");
    }
    public async Task<IActionResult> OnPostUploadAsync(List<IFormFile> Dateien, int StepId)
    {
        var step = await _db.Steps
            .Include(s => s.Workflow)
            .FirstOrDefaultAsync(s => s.Id == StepId);

        if (step == null)
            return NotFound();

        var workflow = step.Workflow;
        var user = await _userManager.GetUserAsync(User);

        // 🔹 KundenId bestimmen
        int kundeId = await ErmittleKundenIdAsync(user);

        string abteilung = "allgemein";
        var dokumente = new List<Dokumente>();

        if (Dateien != null && Dateien.Any())
        {
            foreach (var datei in Dateien)
            {
                if (datei == null || datei.Length == 0)
                    continue;

                string kategorie = step.Kategorie?.ToLowerInvariant() ?? "workflow";
                string firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";

                try
                {
                    // ========== 1️⃣ ORIGINALDATEI ==========
                    string originalPath = $"dokumente/{firma}/{abteilung}/{kategorie}/{datei.FileName}";
                    string fullUrlOriginal = $"{_WebDav.BaseUrl.TrimEnd('/')}/{originalPath}"
                        .Replace("//", "/")
                        .Replace(":/", "://");

                    using (var stream = datei.OpenReadStream())
                    {
                        await _WebDav.UploadStreamAsync(stream, originalPath, datei.ContentType);
                    }

                    var original = new Dokumente
                    {
                        Id = Guid.NewGuid(),
                        Titel = Path.GetFileNameWithoutExtension(datei.FileName),
                        Dateiname = datei.FileName,
                        Dateipfad = fullUrlOriginal,
                        ObjectPath = originalPath,
                        HochgeladenAm = DateTime.UtcNow,
                        Kategorie = "Workflow",
                        ErkannteKategorie = kategorie,
                        KundeId = kundeId,
                        ApplicationUserId = user.Id,
                        WorkflowId = workflow.Id,
                        StepId = null, // Workflow-weit
                        IsVersion = false
                    };

                    _db.Dokumente.Add(original);
                    await _db.SaveChangesAsync();

                    // ========== 2️⃣ ERSTE VERSION ==========
                    string versionPath = $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/{datei.FileName}";
                    string fullUrlVersion = $"{_WebDav.BaseUrl.TrimEnd('/')}/{versionPath}"
                        .Replace("//", "/")
                        .Replace(":/", "://");

                    using (var stream = datei.OpenReadStream())
                    {
                        await _WebDav.UploadStreamAsync(stream, versionPath, datei.ContentType);
                    }

                    var version = new Dokumente
                    {
                        Id = Guid.NewGuid(),
                        Titel = Path.GetFileNameWithoutExtension(datei.FileName),
                        Dateiname = datei.FileName,
                        Dateipfad = fullUrlVersion,
                        ObjectPath = versionPath,
                        HochgeladenAm = DateTime.UtcNow,
                        Kategorie = "Workflow",
                        ErkannteKategorie = kategorie,
                        KundeId = kundeId,
                        ApplicationUserId = user.Id,
                        WorkflowId = workflow.Id,
                        StepId = null,
                        IsVersion = true,
                        OriginalId = original.Id
                    };

                    _db.Dokumente.Add(version);
                    dokumente.Add(original);
                    dokumente.Add(version);

                    await _db.SaveChangesAsync();

                    _logger.LogInformation("📄 Datei '{Name}' erfolgreich in WebDAV hochgeladen (Workflow {Workflow})", datei.FileName, workflow.Title);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Fehler beim Upload auf WebDAV für Datei {Name}", datei.FileName);
                    TempData["Error"] = $"❌ Upload zu WebDAV fehlgeschlagen: {ex.Message}";
                    return Page();
                }
            }
        }

        // ✅ Audit Log
        if (dokumente.Any())
        {
            await _auditLogService.LogActionOnlyAsync(
                $"Dokument(e) für Workflow \"{workflow.Title}\" hochgeladen",
                user.Id
            );
        }

        return RedirectToPage(new { workflowId = workflow.Id, stepId = step.Id });
    }

    private async Task<int> ErmittleKundenIdAsync(ApplicationUser user)
    {
        // 1. Prüfen, ob der User selbst eine KundenId hat
        var kundeBenutzer = await _db.KundeBenutzer
            .FirstOrDefaultAsync(k => k.ApplicationUserId == user.Id);

        if (kundeBenutzer != null)
        {
            return kundeBenutzer.KundenId;
        }

        // 2. Falls nicht → über CreatedByAdminId den Admin suchen
        if (string.IsNullOrEmpty(user.CreatedByAdminId))
            throw new Exception("❌ Weder KundenId noch CreatedByAdminId beim User gesetzt.");

        var adminKunde = await _db.KundeBenutzer
            .FirstOrDefaultAsync(k => k.ApplicationUserId == user.CreatedByAdminId);

        if (adminKunde == null)
            throw new Exception("❌ Admin hat keine KundenId.");

        return adminKunde.KundenId;
    }


    // ViewModel
    public class StepDetailViewModel
    {
        public Workflow Workflow { get; set; }
        public List<Step> Steps { get; set; } = new();
        public Dictionary<int, List<Dokumente>> StepDokumente { get; set; } = new();
        public Dictionary<int, List<StepKommentar>> StepKommentare { get; set; } = new();
        public int AktuellerStepId { get; set; }
        public string AktuellerUserId { get; set; }
        public List<Dokumente> Dokumente { get; set; }
        
        // Neue Properties für Abteilungsanzeige
        public Dictionary<int, string> StepAssignmentDisplay { get; set; } = new(); // StepId -> Anzeigetext
        public Dictionary<int, ApplicationUser> StepCompletedByUser { get; set; } = new(); // StepId -> Benutzer der erledigt hat
    }
}
