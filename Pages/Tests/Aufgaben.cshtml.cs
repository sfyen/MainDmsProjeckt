using System;
using DmsProjeckt.Data;
using DmsProjeckt.Service;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Pages.Tests
{
    public class AufgabenModel : PageModel
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        [BindProperty]
        public Aufgaben NeueAufgabe { get; set; } = new();
        public List<Aufgaben> AlleAufgaben { get; set; } = new();
        public List<ApplicationUser> BenutzerListe { get; set; } = new();
        public List<Aufgaben> AufgabenVonMir { get; set; } = new();
        private readonly AuditLogService _auditLogService;
        private readonly EmailService _emailService;
        private readonly WebDavStorageService _WebDav;
        private readonly ILogger<AufgabenModel> _logger;
        public AufgabenModel(ApplicationDbContext context, UserManager<ApplicationUser> userManager, AuditLogService auditLogService, EmailService emailService, WebDavStorageService WebDav, ILogger<AufgabenModel> logger)
        {
            _context = context;
            _userManager = userManager;
            _auditLogService = auditLogService;
            _emailService = emailService;
            _WebDav = WebDav;
            _logger = logger;
        }
        public Dokumente VorgewaehltesDokument { get; set; }
        public Guid? VorgewaehltesDokumentId { get; set; }
        public async Task OnGetAsync(Guid? dokumentId, string? fromFileId, string? fileName, string? filePath)
        {
            var user = await _userManager.GetUserAsync(User);
            var kundenNr = user.AdminId;

            // 🔹 Benutzerliste (für Dropdown)
            BenutzerListe = await _userManager.Users
                .Where(u => u.AdminId == kundenNr && u.Id != user.Id)
                .ToListAsync();

            // 🔹 Aufgaben für mich
            AlleAufgaben = await _context.Aufgaben
                .Where(a => a.FuerUser != null && a.FuerUser == user.Id && a.Aktiv)
                .Include(a => a.VonUserNavigation)
                .Include(a => a.FuerUserNavigation)
                .Include(a => a.Dateien)
                .Include(a => a.Workflow)
                .ToListAsync();

            // 🔹 Aufgaben von mir
            AufgabenVonMir = await _context.Aufgaben
                .Where(a => a.VonUser == user.Id)
                .Include(a => a.VonUserNavigation)
                .Include(a => a.FuerUserNavigation)
                .Include(a => a.Dateien)
                .Include(a => a.Workflow)
                .ToListAsync();

            // 🔹 Wenn von einem bestehenden Dokument geöffnet
            if (dokumentId.HasValue)
            {
                VorgewaehltesDokument = await _context.Dokumente
                    .FirstOrDefaultAsync(d => d.Id == dokumentId.Value && d.KundeId == kundenNr);
                VorgewaehltesDokumentId = dokumentId.Value;
                ViewData["OpenAufgabeModal"] = true;
            }

            // 🔹 Wenn über Kontextmenü (📋 Aufgabe erstellen) geöffnet
            if (!string.IsNullOrEmpty(fromFileId))
            {
                ViewData["OpenAufgabeModal"] = true;
                ViewData["AttachedFileId"] = fromFileId;
                ViewData["AttachedFilePath"] = filePath;
                ViewData["AttachedFileName"] = fileName;
            }

            // 🔹 Standard-Fälligkeitsdatum
            NeueAufgabe.FaelligBis = DateTime.UtcNow;
        }

        [BindProperty]
        public DateTime FaelligDatum { get; set; }

        [BindProperty]
        public TimeSpan FaelligUhrzeit { get; set; }

        public async Task<IActionResult> OnPostErstellenAsync(List<IFormFile> Dateien, Guid? DokumentId)
        {
            Console.WriteLine("📌 OnPostErstellenAsync aufgerufen (WebDAV Version)");

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Page();
            }

            // 🧠 Aufgabe initialisieren
            NeueAufgabe.VonUser = user.Id;
            NeueAufgabe.UserId = user.Id;
            NeueAufgabe.Aktiv = true;
            NeueAufgabe.FaelligBis = FaelligDatum.Date + FaelligUhrzeit;
            if (NeueAufgabe.FaelligBis == default)
                NeueAufgabe.FaelligBis = DateTime.UtcNow;

            if (NeueAufgabe.Prioritaet == 0) // oder == null, je nach Datentyp
                NeueAufgabe.Prioritaet = 1;

            _context.Aufgaben.Add(NeueAufgabe);
            await _context.SaveChangesAsync();

            // 🔗 Dokument mit Aufgabe verknüpfen (falls vorhanden)
            if (DokumentId.HasValue)
            {
                var dokument = await _context.Dokumente.FirstOrDefaultAsync(d => d.Id == DokumentId.Value);
                if (dokument != null)
                {
                    dokument.AufgabeId = NeueAufgabe.Id;
                    await _context.SaveChangesAsync();
                }
            }

            // 📂 Uploads
            if (Dateien != null && Dateien.Any())
            {
                var kundeBenutzer = await _context.KundeBenutzer
                    .FirstOrDefaultAsync(k => k.ApplicationUserId == user.Id);

                string firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";
                string abteilung = "allgemein";
                string kategorie = "aufgabe";

                foreach (var datei in Dateien)
                {
                    if (datei == null || datei.Length == 0)
                    {
                        return Page();
                    }

                    string objectPathOriginal = $"dokumente/{firma}/{abteilung}/{kategorie}/{datei.FileName}";
                    string objectPathVersion = $"dokumente/{firma}/{abteilung}/versionen/{datei.FileName}";

                    string fullUrlOriginal = $"{_WebDav.BaseUrl.TrimEnd('/')}/{objectPathOriginal}".Replace("//", "/").Replace(":/", "://");
                    string fullUrlVersion = $"{_WebDav.BaseUrl.TrimEnd('/')}/{objectPathVersion}".Replace("//", "/").Replace(":/", "://");

                    try
                    {
                        // 1️⃣ Original-Datei hochladen
                        using var stream = datei.OpenReadStream();
                        await _WebDav.UploadStreamAsync(stream, objectPathOriginal, datei.ContentType);

                        // 💾 Original in DB speichern
                        var originalDoc = new Dokumente
                        {
                            Id = Guid.NewGuid(),
                            Titel = datei.FileName,
                            Dateiname = datei.FileName,
                            Dateipfad = fullUrlOriginal,
                            ObjectPath = objectPathOriginal,
                            HochgeladenAm = DateTime.UtcNow,
                            Kategorie = "aufgabe",
                            KundeId = kundeBenutzer?.KundenId ?? 0,
                            ApplicationUserId = user.Id,
                            AufgabeId = null,
                            OriginalId = null
                        };
                        _context.Dokumente.Add(originalDoc);
                        await _context.SaveChangesAsync();

                        // 2️⃣ Erste Version hochladen
                        using var stream2 = datei.OpenReadStream();
                        await _WebDav.UploadStreamAsync(stream2, objectPathVersion, datei.ContentType);

                        var ersteVersion = new Dokumente
                        {
                            Id = Guid.NewGuid(),
                            Titel = datei.FileName + "_v1",
                            Dateiname = datei.FileName,
                            Dateipfad = fullUrlVersion,
                            ObjectPath = objectPathVersion,
                            HochgeladenAm = DateTime.UtcNow,
                            Kategorie = "versionen",
                            KundeId = kundeBenutzer?.KundenId ?? 0,
                            ApplicationUserId = user.Id,
                            AufgabeId = NeueAufgabe.Id,
                            IsVersion = true,
                            OriginalId = originalDoc.Id
                        };
                        _context.Dokumente.Add(ersteVersion);
                        await _context.SaveChangesAsync();

                        _logger.LogInformation("📁 Datei und Version erfolgreich hochgeladen: {Name}", datei.FileName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Fehler beim Upload auf WebDAV für Datei {File}", datei.FileName);
                        return Page();
                    }
                }
            }

            // 📅 Kalendereintrag erstellen (nur für normale Aufgaben, nicht für Workflow-Aufgaben)
            if (!string.IsNullOrEmpty(NeueAufgabe.FuerUser))
            {
                Console.WriteLine($"📅 Erstelle Kalendereintrag für Aufgabe: {NeueAufgabe.Titel}");
                
                var calendarEvent = new CalendarEvent
                {
                    Title = $"📋 Aufgabe: {NeueAufgabe.Titel}",
                    Description = NeueAufgabe.Beschreibung ?? "",
                    StartDate = NeueAufgabe.FaelligBis.Date,
                    EndDate = NeueAufgabe.FaelligBis.Date,
                    StartTime = NeueAufgabe.FaelligBis.ToString("HH:mm"),
                    EndTime = NeueAufgabe.FaelligBis.AddHours(1).ToString("HH:mm"),
                    EventType = "task",
                    Color = "#77dd77", // Orange für Aufgaben
                    AllDay = false,
                    CreatedById = user.Id,
                    UserId = user.Id,
                    RelatedAufgabeId = NeueAufgabe.Id
                };
                
                _context.CalendarEvents.Add(calendarEvent);
                await _context.SaveChangesAsync();
                
                // Teilnehmer hinzufügen (der zugewiesene Benutzer)
                var participant = new CalendarEventParticipant
                {
                    CalendarEventId = calendarEvent.Id,
                    UserId = NeueAufgabe.FuerUser,
                    Status = EventParticipationStatus.Accepted
                };
                _context.CalendarEventParticipants.Add(participant);
                await _context.SaveChangesAsync();
                
                // Verknüpfung speichern
                NeueAufgabe.CalendarEventId = calendarEvent.Id;
                await _context.SaveChangesAsync();
                
                Console.WriteLine($"✅ Kalendereintrag erstellt: ID={calendarEvent.Id}");
            }

            // 🔔 Notification + Email
            var notifType = await _context.NotificationTypes.FirstOrDefaultAsync(n => n.Name == "Erstellt");
            var notifTypeEmail = await _context.NotificationTypes.FirstOrDefaultAsync(n => n.Name == "ErstelltEmail");

            if (notifType != null)
            {
                var setting = await _context.UserNotificationSettings
                    .FirstOrDefaultAsync(s => s.UserId == NeueAufgabe.FuerUser && s.NotificationTypeId == notifType.Id);

                if (setting == null || setting.Enabled)
                {
                    var notification = new Notification
                    {
                        Title = "Neue Aufgabe zugewiesen",
                        Content = "Du hast eine neue Aufgabe erhalten.",
                        CreatedAt = DateTime.UtcNow,
                        NotificationTypeId = notifType.Id,
                        ActionLink = "/Tests/Aufgaben"
                    };
                    _context.Notifications.Add(notification);
                    await _context.SaveChangesAsync();

                    var userNotification = new UserNotification
                    {
                        UserId = NeueAufgabe.FuerUser,
                        NotificationId = notification.Id,
                        IsRead = false,
                        ReceivedAt = DateTime.UtcNow
                    };
                    _context.UserNotifications.Add(userNotification);
                    await _context.SaveChangesAsync();
                }
            }

            if (notifTypeEmail != null)
            {
                var settingEmail = await _context.UserNotificationSettings
                    .FirstOrDefaultAsync(s => s.UserId == NeueAufgabe.FuerUser && s.NotificationTypeId == notifTypeEmail.Id);

                if (settingEmail == null || settingEmail.Enabled)
                {
                    var targetUser = await _context.Users.FindAsync(NeueAufgabe.FuerUser);
                    if (targetUser != null && !string.IsNullOrEmpty(targetUser.Email))
                    {
                        string subject = "Neue Aufgabe zugewiesen";
                        string body = $"Hallo {targetUser.Vorname},<br>du hast eine neue Aufgabe erhalten.<br><a href=\"https://localhost:7074/Tests/Aufgaben\">Zur Aufgabe</a>";
                        await _emailService.SendEmailAsync(targetUser.Email, subject, body);
                        _logger.LogInformation("📧 E-Mail-Benachrichtigung an {Mail} gesendet", targetUser.Email);
                    }
                }
            }

            await _auditLogService.LogActionOnlyAsync($"Aufgabe \"{NeueAufgabe.Titel}\" ({NeueAufgabe.Id}) erstellt", user.Id);

            return RedirectToPage();
        }




        public async Task<IActionResult> OnPostErledigt([FromForm] int id)
        {


            var userId = _userManager.GetUserId(User);
            var aufgabe = await _context.Aufgaben
                .Include(a => a.StepNavigation)
                .ThenInclude(s => s.Workflow)
                .FirstOrDefaultAsync(a => a.Id == id);
            Console.WriteLine("🔥 OnPostErledigt ausgelöst mit ID: " + id);

            Console.WriteLine($"👉 Erledigt-Handler aufgerufen mit id={id}");





            if (aufgabe == null)
            {
                Console.WriteLine("❌ Aufgabe nicht gefunden!");
                return new BadRequestResult();
            }

            if (aufgabe.FuerUser != userId)
            {
                Console.WriteLine("❌ Zugriff verweigert – nicht dein Task!");
                return new BadRequestResult();
            }

            aufgabe.Erledigt = true;
            if (aufgabe.StepId == null)
            {
                await _auditLogService.LogActionOnlyAsync($"Aufgabe \"{aufgabe.Titel}\" ({aufgabe.Id}) erledigt", userId);
                var notificationType = await _context.NotificationTypes
                    .FirstOrDefaultAsync(n => n.Name == "Erledigt");
                var setting = await _context.UserNotificationSettings
                .FirstOrDefaultAsync(s => s.UserId == aufgabe.VonUser && s.NotificationTypeId == notificationType.Id);

                var notificationTypeEmail = await _context.NotificationTypes
                    .FirstOrDefaultAsync(n => n.Name == "ErledigtEmail");
                var settingsEmail = await _context.UserNotificationSettings
                    .FirstOrDefaultAsync(s => s.UserId == aufgabe.VonUser && s.NotificationTypeId == notificationTypeEmail.Id);
                if (setting == null || setting.Enabled)
                {

                    var notification = new Notification
                    {
                        Title = "Aufgabe erledigt",
                        Content = "Eine von dir erstellte Aufgabe wurde erledigt.",
                        CreatedAt = DateTime.UtcNow,
                        NotificationTypeId = notificationType.Id,
                        ActionLink = "/Tests/Aufgaben"
                    };
                    _context.Notifications.Add(notification);
                    await _context.SaveChangesAsync();

                    var userNotification = new UserNotification
                    {
                        UserId = aufgabe.VonUser,
                        NotificationId = notification.Id,
                        IsRead = false,
                        ReceivedAt = DateTime.UtcNow
                    };
                    _context.UserNotifications.Add(userNotification);
                    await _context.SaveChangesAsync();
                }
                if(settingsEmail == null || settingsEmail.Enabled)
                {
                    var userTo = await _context.Users.FindAsync(aufgabe.VonUser);
                    string subject = "Aufgabe erledigt";
                    string body = $@"
                <p>Hallo {userTo.Vorname},</p>
                <p>Die Aufgabe <b>""{aufgabe.Titel}""</b> erledigt.</p>
                < p >< a href = 'Tests/Aufgaben' > Details ansehen </ a ></ p >
                < p > Viele Grüße,< br /> Dein Team </ p > ";

            await _emailService.SendEmailAsync(userTo.Email, subject, body);
                }
                var erstelltType = await _context.NotificationTypes.FirstOrDefaultAsync(n => n.Name == "Erstellt");

                if (erstelltType != null)
                {
                    // Finde alle "Neue Aufgabe zugewiesen"-Notifications für diese Aufgabe und diesen User, die noch nicht gelesen sind
                    var userNotifications = await _context.UserNotifications
                        .Include(un => un.Notification)
                        .Where(un =>
                            un.UserId == aufgabe.FuerUser &&
                            !un.IsRead &&
                            un.Notification.NotificationTypeId == erstelltType.Id)
                        .OrderByDescending(un => un.ReceivedAt)
                        .ToListAsync();

                    // Da du vermutlich pro Aufgabe/Benutzer eine Notification hast, reicht meist FirstOrDefault
                    var ungelesen = userNotifications.FirstOrDefault();
                    if (ungelesen != null)
                    {
                        ungelesen.IsRead = true;
                        await _context.SaveChangesAsync();
                    }
                }
                Console.WriteLine($"Aufgabe erledigt {aufgabe.Titel}");
            }
            else
            {
                await _auditLogService.LogActionOnlyAsync($"Schritt {aufgabe.StepNavigation.Order + 1} in Workflow \"{aufgabe.StepNavigation.Workflow.Title}\" ({aufgabe.StepNavigation.WorkflowId}) erledigt", aufgabe.FuerUser);
                Console.WriteLine("Log versucht");
            }
            // usw...
          

            if (aufgabe.StepNavigation != null)
            {
                var currentStep = aufgabe.StepNavigation;
                currentStep.Completed = true;

                var nextStep = await _context.Steps
                    .Where(s => s.WorkflowId == currentStep.WorkflowId &&
                                s.Order == currentStep.Order + 1)
                    .FirstOrDefaultAsync();

                if (nextStep != null && !nextStep.TaskCreated && !string.IsNullOrWhiteSpace(nextStep.UserId))
                {
                    var neueAufgabe = new Aufgaben
                    {
                        Titel = nextStep.Title,
                        Beschreibung = nextStep.Description,
                        FaelligBis = nextStep.DueDate ?? DateTime.Today.AddDays(3),
                        Prioritaet = 1,
                        VonUser = aufgabe.VonUser,
                        FuerUser = nextStep.UserId,
                        Erledigt = false,
                        ErstelltAm = DateTime.Now,
                        StepId = nextStep.Id
                    };
                    var notificationType = await _context.NotificationTypes
         .FirstOrDefaultAsync(n => n.Name == "Workflowaufgabe");
                    if (notificationType == null)
                    {
                        Console.WriteLine("❌ NotificationType 'Workflowaufgabe' fehlt!");
                    }
                    else
                    {
                        var setting = await _context.UserNotificationSettings
                            .FirstOrDefaultAsync(s => s.UserId == neueAufgabe.FuerUser && s.NotificationTypeId == notificationType.Id);

                        if (setting == null || setting.Enabled)
                        {
                            var notification = new Notification
                            {
                                Title = "Neue Aufgabe zugewiesen",
                                Content = $"Du hast eine neue Aufgabe im Workflow \"{currentStep.Workflow.Title}\" erhalten.",
                                CreatedAt = DateTime.UtcNow,
                                NotificationTypeId = notificationType.Id
                            };
                            _context.Notifications.Add(notification);
                            await _context.SaveChangesAsync();

                            var userNotification = new UserNotification
                            {
                                UserId = nextStep.UserId,
                                NotificationId = notification.Id,
                                IsRead = false,
                                ReceivedAt = DateTime.UtcNow
                            };
                            _context.UserNotifications.Add(userNotification);
                            await _context.SaveChangesAsync();
                        }
                    }
                    _context.Aufgaben.Add(neueAufgabe);
                    nextStep.TaskCreated = true;
                }
               var notificationType2 = await _context.NotificationTypes
                    .FirstOrDefaultAsync(n => n.Name == "Workflow erledigt");
                var setting2 = await _context.UserNotificationSettings
                .FirstOrDefaultAsync(s => s.UserId == currentStep.UserId && s.NotificationTypeId == notificationType2.Id);
                if (setting2 == null || setting2.Enabled)
                {

                    var notification = new Notification
                    {
                        Title = "Aufgabe erledigt",
                        Content = $"Im von dir erstellten Workflow \"{ currentStep.Workflow.Title }\" wurde Aufgabe {currentStep.Order +1 } erledigt.",
                        CreatedAt = DateTime.UtcNow,
                        NotificationTypeId = notificationType2.Id
                    };
                    _context.Notifications.Add(notification);
                    await _context.SaveChangesAsync();

                    var userNotification = new UserNotification
                    {
                        UserId = currentStep.UserId,
                        NotificationId = notification.Id,
                        IsRead = false,
                        ReceivedAt = DateTime.UtcNow
                    };
                    _context.UserNotifications.Add(userNotification);
                    await _context.SaveChangesAsync();
                }

            }
            await _context.SaveChangesAsync();
            return RedirectToPage();
        }
        [BindProperty]
        public int Id { get; set; }

        public async Task<IActionResult> OnPostLoeschen(int id)
        {
            var currentUserId = _userManager.GetUserId(User);
            Console.WriteLine($"🗑️ Löschversuch: TaskID={id}, CurrentUser={currentUserId}");
            
            var aufgabe = _context.Aufgaben
                .Include(a => a.Dateien) // falls Navigation vorhanden
                .FirstOrDefault(a => a.Id == id);

            if (aufgabe == null)
            {
                Console.WriteLine($"❌ Aufgabe {id} nicht gefunden");
                return RedirectToPage();
            }

            Console.WriteLine($"📋 Aufgabe gefunden: VonUser={aufgabe.VonUser}, FuerUser={aufgabe.FuerUser}");

            // Berechtigung prüfen: Nur der Ersteller darf löschen
            if (aufgabe.VonUser != currentUserId)
            {
                Console.WriteLine($"🚫 BERECHTIGUNG VERWEIGERT! VonUser={aufgabe.VonUser} != CurrentUser={currentUserId}");
                return RedirectToPage();
            }

            Console.WriteLine($"✅ Berechtigung OK - Lösche Aufgabe {id}");

            // 📅 Zugehörigen Kalendereintrag löschen (falls vorhanden)
            if (aufgabe.CalendarEventId.HasValue)
            {
                var calendarEvent = await _context.CalendarEvents
                    .Include(ce => ce.Participants)
                    .FirstOrDefaultAsync(ce => ce.Id == aufgabe.CalendarEventId.Value);
                
                if (calendarEvent != null)
                {
                    // Teilnehmer werden durch Cascade Delete automatisch entfernt
                    _context.CalendarEvents.Remove(calendarEvent);
                    Console.WriteLine($"🗓️ Kalendereintrag {calendarEvent.Id} wird gelöscht");
                }
            }

            // Referenz bei allen Dokumenten entfernen
            var dokumente = _context.Dokumente.Where(d => d.AufgabeId == id).ToList();
            foreach (var d in dokumente)
            {
                d.AufgabeId = null;
            }

            _context.Aufgaben.Remove(aufgabe);
            _context.SaveChanges();

            return RedirectToPage();
        }



        public async Task<IActionResult> OnGetDetailsAsync(int id)
        {
            var a = await _context.Aufgaben
                .Include(x => x.VonUserNavigation)
                .Include(x => x.FuerUserNavigation)
                .Include(x => x.Dateien)
                .FirstOrDefaultAsync(x => x.Id == id);

            if (a == null)
                return new JsonResult(new { success = false });

            var daten = new
            {
                id = a.Id,
                titel = a.Titel,
                beschreibung = a.Beschreibung,
                faelligBis = a.FaelligBis.ToString("dd.MM.yyyy HH:mm"),
                prioritaet = a.Prioritaet,
                userName = a.FuerUser != null
    ? (a.FuerUserNavigation.Vorname + " " + a.FuerUserNavigation.Nachname)
    : "Unbekannt",
                dateien = a.Dateien?.Select(d => new
                {
                    name = d.Dateiname,
                    url = d.Dateipfad
                }).ToList()
            };

            return new JsonResult(daten);
        }



    }
}
