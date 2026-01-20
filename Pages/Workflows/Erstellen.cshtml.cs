using DmsProjeckt.Data;

using DmsProjeckt.Service;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Pages.Workflows
{
    public class ErstellenModel : PageModel
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly WebDavStorageService _WebDav;
        private readonly AuditLogService _auditLogService;
        private readonly ILogger<ErstellenModel> _logger = 
            LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<ErstellenModel>();
        public ErstellenModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, AuditLogService auditLogService, WebDavStorageService WebDav)
        {
            _context = context;
            _userManager = userManager;
            _WebDav = WebDav;
            _auditLogService = auditLogService;
        }

        [BindProperty]
        public Workflow Workflow { get; set; }

        [BindProperty]
        public List<Step> Steps { get; set; } = new();

        public List<SelectListItem> UserOptions { get; set; }

        public async Task OnGetAsync()
        {
            var currentUser = await _userManager.GetUserAsync(User);

            UserOptions = await _context.Users
                .Where(u => u.Id != currentUser.Id)
                .Select(u => new SelectListItem
                {
                    Value = u.Id,
                    Text = $"{u.Vorname} {u.Nachname}"
                })
                .ToListAsync();


        }

        public async Task<IActionResult> OnPostAsync(List<IFormFile> Dateien)
        {
            Console.WriteLine("⚙️ OnPostAsync (WebDAV Version) gestartet");

            // 🔹 Benutzeroptionen laden
            UserOptions = await _context.Users
                .Select(u => new SelectListItem { Value = u.Id, Text = u.Email })
                .ToListAsync();

            Workflow.CreatedAt = DateTime.UtcNow;
            Workflow.LastModified = DateTime.UtcNow;
            Workflow.UserId = _userManager.GetUserId(User);

            // 🔹 Steps an Workflow anhängen
            for (int i = 0; i < Steps.Count; i++)
            {
                Steps[i].Order = i;
                Steps[i].Workflow = Workflow;
            }

            _context.Workflows.Add(Workflow);
            _context.Steps.AddRange(Steps);
            await _context.SaveChangesAsync(); // IDs verfügbar

            var workflowDateien = new List<Dokumente>();
            var user = await _userManager.GetUserAsync(User);
            var abteilung = "allgemein";
            string firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";

            if (Dateien != null && Dateien.Any())
            {
                var kundeBenutzer = await _context.KundeBenutzer
                    .FirstOrDefaultAsync(k => k.ApplicationUserId == Workflow.UserId);

                foreach (var datei in Dateien)
                {
                    if (datei == null || datei.Length == 0)
                    {
                        TempData["Error"] = "❌ Leere Datei.";
                        return Page();
                    }

                    try
                    {
                        // ========== 🔹 ORIGINALDATEI ==========
                        var kategorie = "workflow";
                        var originalPath = $"dokumente/{firma}/{abteilung}/{kategorie}/{datei.FileName}";
                        var fullUrlOriginal = $"{_WebDav.BaseUrl.TrimEnd('/')}/{originalPath}"
                            .Replace("//", "/").Replace(":/", "://");

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
                            ErkannteKategorie = "workflow",
                            KundeId = kundeBenutzer?.KundenId ?? 0,
                            ApplicationUserId = Workflow.UserId,
                            WorkflowId = Workflow.Id,
                            IsVersion = false
                        };

                        _context.Dokumente.Add(original);
                        await _context.SaveChangesAsync();

                        // ========== 🔹 ERSTE VERSION ==========
                        string versionPath = $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/{datei.FileName}";
                        var fullUrlVersion = $"{_WebDav.BaseUrl.TrimEnd('/')}/{versionPath}"
                            .Replace("//", "/").Replace(":/", "://");

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
                            ErkannteKategorie = "workflow",
                            KundeId = kundeBenutzer?.KundenId ?? 0,
                            ApplicationUserId = Workflow.UserId,
                            WorkflowId = Workflow.Id,
                            IsVersion = true,
                            OriginalId = original.Id
                        };

                        _context.Dokumente.Add(version);
                        workflowDateien.Add(original);
                        workflowDateien.Add(version);

                        await _context.SaveChangesAsync();

                        _logger.LogInformation("📁 Datei & Version hochgeladen (WebDAV): {Name}", datei.FileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Fehler beim Upload auf WebDAV für Datei {File}", datei.FileName);
                        TempData["Error"] = $"❌ Upload zu WebDAV fehlgeschlagen: {ex.Message}";
                        return Page();
                    }
                }
            }

            // 🔹 Aufgaben für jeden Step mit UserId oder UserIds (Abteilung)
            var aufgabenListe = new List<Aufgaben>();

            foreach (var step in Steps)
            {
                try
                {
                    int prio = step.Prioritaet > 0 ? step.Prioritaet : 1;
                    // 🔹 Prüfen ob Abteilung (UserIds gefüllt) oder einzelner User (UserId gefüllt)
                    if (step.UserIds != null && step.UserIds.Any())
                    {
                        // Abteilung: Mehrere Benutzer
                        foreach (var userId in step.UserIds)
                        {
                            var aufgabe = new Aufgaben
                            {
                                Titel = step.Kategorie,
                                Beschreibung = step.Description,
                                FaelligBis = step.DueDate ?? DateTime.Today.AddDays(3),
                                Prioritaet = prio,
                                VonUser = Workflow.UserId,
                                FuerUser = userId,
                                Erledigt = false,
                                Aktiv = (step.Order == 0),
                                ErstelltAm = DateTime.UtcNow,
                                StepId = step.Id,
                                WorkflowId = Workflow.Id
                            };
                            aufgabenListe.Add(aufgabe);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(step.UserId))
                    {
                        // Einzelner Benutzer
                        var aufgabe = new Aufgaben
                        {
                            Titel = step.Kategorie,
                            Beschreibung = step.Description,
                            FaelligBis = step.DueDate ?? DateTime.Today.AddDays(3),
                            Prioritaet = prio,
                            VonUser = Workflow.UserId,
                            FuerUser = step.UserId,
                            Erledigt = false,
                            Aktiv = (step.Order == 0),
                            ErstelltAm = DateTime.UtcNow,
                            StepId = step.Id,
                            WorkflowId = Workflow.Id
                        };
                        aufgabenListe.Add(aufgabe);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Fehler beim Erstellen der Aufgaben für Step {step.Description}");
                }
            }

            _context.Aufgaben.AddRange(aufgabenListe);
            await _context.SaveChangesAsync();

            // 🔹 Für jede Aufgabe einen Kalendereintrag erzeugen
            foreach (var NeueAufgabe in aufgabenListe)
            {
                try
                {
                    user = await _userManager.FindByIdAsync(NeueAufgabe.FuerUser);
                    if (user == null)
                        continue;

                    var calendarEvent = new CalendarEvent
                    {
                        Title = $"📋 Aufgabe: {NeueAufgabe.Titel}",
                        Description = NeueAufgabe.Beschreibung ?? "",
                        StartDate = NeueAufgabe.FaelligBis.Date,
                        EndDate = NeueAufgabe.FaelligBis.Date,
                        StartTime = NeueAufgabe.FaelligBis.ToString("HH:mm"),
                        EndTime = NeueAufgabe.FaelligBis.AddHours(1).ToString("HH:mm"),
                        EventType = "task",
                        Color = "#77dd77", // Grün für Aufgaben
                        AllDay = false,
                        CreatedById = Workflow.UserId,
                        UserId = user.Id,
                        RelatedAufgabeId = NeueAufgabe.Id
                    };

                    _context.CalendarEvents.Add(calendarEvent);
                    await _context.SaveChangesAsync();

                    // 🔹 Teilnehmer hinzufügen (der zugewiesene Benutzer)
                    var participant = new CalendarEventParticipant
                    {
                        CalendarEventId = calendarEvent.Id,
                        UserId = NeueAufgabe.FuerUser,
                        Status = EventParticipationStatus.Accepted
                    };

                    _context.CalendarEventParticipants.Add(participant);
                    await _context.SaveChangesAsync();

                    // 🔹 Verknüpfung speichern
                    NeueAufgabe.CalendarEventId = calendarEvent.Id;
                    await _context.SaveChangesAsync();

                    _logger.LogInformation($"📅 Kalendereintrag erstellt für Benutzer {user.Email} – Aufgabe: {NeueAufgabe.Titel}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"❌ Fehler beim Erstellen des Kalendereintrags für Aufgabe {NeueAufgabe.Titel}");
                }
            }


            // 🔹 Markiere Steps mit Aufgaben als "TaskCreated"
            foreach (var aufgabe in aufgabenListe)
            {
                var step = Steps.FirstOrDefault(s => s.Id == aufgabe.StepId);
                if (step != null)
                    step.TaskCreated = true;
            }

            _context.Steps.UpdateRange(Steps);
            await _context.SaveChangesAsync();

            // 🔹 Audit Log
            if (workflowDateien.Any())
                await _auditLogService.LogActionOnlyAsync(
                    $"Workflow \"{Workflow.Title}\" ({Workflow.Id}) mit {workflowDateien.Count} Dokument(en) erstellt", Workflow.UserId);
            else
                await _auditLogService.LogActionOnlyAsync(
                    $"Workflow \"{Workflow.Title}\" ({Workflow.Id}) erstellt", Workflow.UserId);

            // 🔔 Erste Aufgabe (Order == 0) - Benachrichtigung senden
            var ersterStep = Steps.FirstOrDefault(s => s.Order == 0);
            if (ersterStep != null)
            {
                var notificationType = await _context.NotificationTypes
                    .FirstOrDefaultAsync(n => n.Name == "Workflowaufgabe");

                if (notificationType != null)
                {
                    // Liste der Benutzer, die benachrichtigt werden sollen
                    var userIdsToNotify = new List<string>();
                    
                    if (ersterStep.UserIds != null && ersterStep.UserIds.Any())
                    {
                        // Abteilungszuweisung - alle Benutzer benachrichtigen
                        userIdsToNotify.AddRange(ersterStep.UserIds);
                    }
                    else if (!string.IsNullOrWhiteSpace(ersterStep.UserId))
                    {
                        // Einzelner Benutzer
                        userIdsToNotify.Add(ersterStep.UserId);
                    }

                    // Benachrichtigungen erstellen
                    foreach (var userId in userIdsToNotify)
                    {
                        var setting = await _context.UserNotificationSettings
                            .FirstOrDefaultAsync(s => s.UserId == userId && s.NotificationTypeId == notificationType.Id);

                        if (setting == null || setting.Enabled)
                        {
                            var notification = new Notification
                            {
                                Title = "Neue Aufgabe zugewiesen",
                                Content = $"Du hast eine neue Aufgabe im Workflow \"{Workflow.Title}\" erhalten.",
                                CreatedAt = DateTime.UtcNow,
                                NotificationTypeId = notificationType.Id,
                                ActionLink = $"/Workflows/StepDetail/{ersterStep.WorkflowId}/{ersterStep.Id}"
                            };
                            _context.Notifications.Add(notification);
                            await _context.SaveChangesAsync();

                            var userNotification = new UserNotification
                            {
                                UserId = userId,
                                NotificationId = notification.Id,
                                IsRead = false,
                                ReceivedAt = DateTime.UtcNow
                            };
                            _context.UserNotifications.Add(userNotification);
                            await _context.SaveChangesAsync();
                        }
                    }
                }
            }

            TempData["Success"] = "✅ Workflow erfolgreich erstellt!";
            return RedirectToPage("Index");
        }

        public class UserOptionDto
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string ProfileImageUrl { get; set; }
            public string Abteilung { get; set; }
        }

        public class AbteilungDto
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        // 🔍 User-Suche
        public async Task<IActionResult> OnGetSearch(string term)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null)
                return new JsonResult(new { success = false });

            term = term?.Trim().ToLower() ?? "";

            // 🔹 Benutzer aus derselben Firma (mit Abteilung laden)
            var users = await _context.Users
                .Include(u => u.Abteilung)
                .Where(u => u.FirmenName == currentUser.FirmenName && u.Id != currentUser.Id)
                .Where(u => string.IsNullOrEmpty(term) ||
                            u.Vorname.ToLower().Contains(term) ||
                            u.Nachname.ToLower().Contains(term) ||
                            u.Email.ToLower().Contains(term))
                .Select(u => new
                {
                    id = u.Id,
                    name = u.Vorname + " " + u.Nachname,
                    profileImageUrl = string.IsNullOrEmpty(u.ProfilbildUrl)
                        ? "/images/default-profile.png"
                        : u.ProfilbildUrl,
                    abteilung = u.Abteilung != null ? u.Abteilung.Name : "" // 🏢 Hier hinzugefügt!
                })
                .Take(10)
                .ToListAsync();

            // 🔹 Abteilungen aus derselben Firma
            var abteilungen = await _context.Abteilungen
                .Where(a => string.IsNullOrEmpty(term) || a.Name.ToLower().Contains(term))
                .Select(a => new { id = a.Id, name = a.Name })
                .Take(10)
                .ToListAsync();

            return new JsonResult(new { success = true, users, abteilungen });
        }



        public async Task<JsonResult> OnGetUsersByAbteilungAsync(int abteilungId)
        {
            var users = await _context.Users
       .Where(u => u.AbteilungId == abteilungId)
       .Select(u => new
       {
           id = u.Id,
           name = u.Vorname + " " + u.Nachname,
           profileImageUrl = string.IsNullOrEmpty(u.ProfilbildUrl)
               ? "/images/default-profile.png"
               : u.ProfilbildUrl
       })
       .ToListAsync();

            return new JsonResult(new { success = true, users });
        }




    }
}
