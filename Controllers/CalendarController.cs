using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DmsProjeckt.Data;

using System.Threading.Tasks;
using System.Linq;

namespace DmsProjeckt.Controllers
{
    [Authorize]
    [Route("Calendar")]
    [ApiController]
    public class CalendarController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public CalendarController(ApplicationDbContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        // ========================================
        // 🟩 EVENT ERSTELLEN
        // ========================================
        [HttpPost("SaveEvent")]
        public async Task<IActionResult> SaveEvent([FromBody] CalendarEventDto dto)
        {
            if (dto == null)
                return BadRequest("Event-Daten fehlen.");

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            if (!DateTime.TryParse(dto.StartDate, out DateTime startDate))
                return BadRequest("Ungültiges Startdatum.");

            if (!DateTime.TryParse(dto.EndDate, out DateTime endDate))
                endDate = startDate;

            // 🎨 Farb-Logik
            string color = dto.EventType switch
            {
                "task" => "#77dd77",
                "meeting" => "#84b6f4",
                "personal" => "#ffb347",
                _ => "#b8a5ff"
            };

            // 📅 Event speichern
            var ev = new CalendarEvent
            {
                Title = dto.Title,
                Description = dto.Description,
                StartDate = startDate,
                EndDate = endDate,
                StartTime = dto.AllDay ? null : dto.StartTime,
                EndTime = dto.AllDay ? null : dto.EndTime,
                EventType = dto.EventType,
                Color = color,
                AllDay = dto.AllDay,
                UserId = user.Id,
                CreatedById = user.Id
            };

            _context.CalendarEvents.Add(ev);
            await _context.SaveChangesAsync();

            // ✅ Ersteller automatisch akzeptiert
            _context.CalendarEventParticipants.Add(new CalendarEventParticipant
            {
                CalendarEventId = ev.Id,
                UserId = user.Id,
                Status = EventParticipationStatus.Accepted
            });

            // 🔔 NotificationType holen oder erstellen
            var notifType = await _context.NotificationTypes
                .FirstOrDefaultAsync(n => n.Name == "CalendarInv");

            if (notifType == null)
            {
                notifType = new NotificationType
                {
                    Name = "CalendarInv",
                    Description = "Kalendereinladungen"
                };
                _context.NotificationTypes.Add(notifType);
                await _context.SaveChangesAsync();
            }

            // 👥 Eingeladene Benutzer
            foreach (var invitedUserId in dto.InvitedUserIds.Distinct())
            {
                if (invitedUserId == user.Id)
                    continue;

                // 🧩 Teilnehmer anlegen
                var participant = new CalendarEventParticipant
                {
                    CalendarEventId = ev.Id,
                    UserId = invitedUserId,
                    Status = EventParticipationStatus.Pending
                };
                _context.CalendarEventParticipants.Add(participant);
                await _context.SaveChangesAsync();

                // 📨 Notification mit Bezug zum Teilnehmer
                var notification = new Notification
                {
                    Title = "Kalendereinladung",
                    Content = $"Du wurdest zu „{ev.Title}“ eingeladen.",
                    ActionLink = "/Calendar/Invitations",
                    CreatedAt = DateTime.UtcNow,
                    NotificationTypeId = notifType.Id,
                    RelatedEntityId = participant.Id // 🔗 Verknüpft mit Participant!
                };

                _context.Notifications.Add(notification);
                await _context.SaveChangesAsync();

                // 👤 UserNotification erstellen
                _context.UserNotifications.Add(new UserNotification
                {
                    UserId = invitedUserId,
                    NotificationId = notification.Id,
                    IsRead = false,
                    ReceivedAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                ev.Id,
                ev.Title,
                StartDate = ev.StartDate.ToString("yyyy-MM-dd"),
                EndDate = ev.EndDate.ToString("yyyy-MM-dd"),
                ev.StartTime,
                ev.EndTime,
                ev.AllDay,
                ev.Color,
                ev.EventType
            });
        }



        // ========================================
        // 🟦 EVENTS HOLEN
        // ========================================
        [AllowAnonymous]
        [HttpGet("GetEvents")]
        public async Task<IActionResult> GetEvents()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            var events = await _context.CalendarEventParticipants
    .Include(p => p.CalendarEvent)
    .Where(p => p.UserId == user.Id && p.Status == EventParticipationStatus.Accepted)
    .Select(p => p.CalendarEvent)
    .Distinct()
    .Select(e => new {
        e.Id,
        e.Title,
        StartDate = e.StartDate.ToString("yyyy-MM-dd"),
        EndDate = e.EndDate.ToString("yyyy-MM-dd"),
        e.StartTime,
        e.EndTime,
        e.Color,
        e.AllDay
    })
    .ToListAsync();


            return Ok(events);
        }


        // ========================================
        // 🟨 EINZELNES EVENT LADEN
        // ========================================
        [HttpGet("/Calendar/GetEvent/{id}")]
        public async Task<IActionResult> GetEvent(int id)
        {
            var ev = await _context.CalendarEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
                return NotFound();

            int? workflowId = null;
            int? stepId = null;

            if (ev.RelatedAufgabeId != null)
            {
                var aufgabe = await _context.Aufgaben
                    .AsNoTracking()
                    .FirstOrDefaultAsync(a => a.Id == ev.RelatedAufgabeId);

                if (aufgabe != null)
                {
                    workflowId = aufgabe.WorkflowId;
                    stepId = aufgabe.StepId;
                }
            }

            return new JsonResult(new
            {
                ev.Id,
                ev.Title,
                ev.Description,
                ev.StartDate,
                ev.EndDate,
                ev.StartTime,
                ev.EndTime,
                ev.Color,
                ev.AllDay,
                ev.EventType,
                ev.RelatedAufgabeId,
                WorkflowId = workflowId,
                StepId = stepId
            });
        }





        // ========================================
        // 🟧 EVENT UPDATEN
        // ========================================
        [HttpPost("UpdateEvent")]
        public async Task<IActionResult> UpdateEvent([FromBody] CalendarEventDto dto)
        {
            if (dto == null)
                return BadRequest("Event-Daten fehlen.");

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            var ev = await _context.CalendarEvents
                .FirstOrDefaultAsync(e => e.Id == dto.Id && e.UserId == user.Id);

            if (ev == null)
                return NotFound();

            DateTime startDate, endDate;
            if (!DateTime.TryParse(dto.StartDate, out startDate))
                return BadRequest("Ungültiges Startdatum.");
            if (!DateTime.TryParse(dto.EndDate, out endDate))
                endDate = startDate;

            ev.Title = dto.Title;
            ev.Description = dto.Description;
            ev.StartDate = startDate;
            ev.EndDate = endDate;
            ev.StartTime = dto.AllDay ? null : dto.StartTime;
            ev.EndTime = dto.AllDay ? null : dto.EndTime;
            ev.AllDay = dto.AllDay;
            ev.EventType = dto.EventType;
            ev.Color = dto.EventType switch
            {
                "task" => "#77dd77",
                "meeting" => "#84b6f4",
                "personal" => "#ffb347",
                _ => "#b8a5ff"
            };

            await _context.SaveChangesAsync();
            return Ok(new
            {
                ev.Id,
                ev.Title,
                ev.Description,
                StartDate = ev.StartDate.ToString("yyyy-MM-dd"),
                EndDate = ev.EndDate.ToString("yyyy-MM-dd"),
                ev.StartTime,
                ev.EndTime,
                ev.Color,
                ev.AllDay,
                ev.EventType
            });

        }


        // ========================================
        // 🟥 EVENT LÖSCHEN
        // ========================================
        [HttpDelete("DeleteEvent/{id}")]
        public async Task<IActionResult> DeleteEvent(int id)
        {
            var userId = _userManager.GetUserId(User);

            var ev = await _context.CalendarEvents
                .Include(e => e.Participants)
                .FirstOrDefaultAsync(e => e.Id == id);

            if (ev == null)
                return NotFound("Termin nicht gefunden");

            // 🔒 Nur Ersteller darf löschen
            if (ev.CreatedById != userId)
                return Forbid("Nur der Ersteller kann diesen Termin löschen.");

            _context.CalendarEvents.Remove(ev);
            await _context.SaveChangesAsync();

            return Ok("Termin gelöscht");
        }

        [HttpGet("GetUsers")]
        public async Task<IActionResult> GetUsers()
        {
            var user = await _userManager.GetUserAsync(User);
            var users = await _context.Users
                .Select(u => new {
                    id = u.Id,
                    fullName = (u.Vorname + " " + u.Nachname).Trim(),
                    firma = u.FirmenName
                })
                .Where(u => u.firma == user.FirmenName && u.id != user.Id)
                .ToListAsync();

            return Ok(users);
        }
        // ========================================
        // 🟪 EINLADUNGEN LADEN (Für Notifications)
        // ========================================
        [HttpGet("GetInvitations")]
        public async Task<IActionResult> GetInvitations()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            var pendingInvites = await _context.CalendarEventParticipants
                .Include(p => p.CalendarEvent)
                .Where(p => p.UserId == user.Id && p.Status == EventParticipationStatus.Pending)
                .Select(p => new
                {
                    p.Id,
                    ParticipantId = p.Id,
                    Title = p.CalendarEvent.Title,
                    StartDate = p.CalendarEvent.StartDate.ToString("yyyy-MM-dd"),
                    p.CalendarEvent.StartTime,
                    p.CalendarEvent.EndTime
                })
                .ToListAsync();

            return Ok(pendingInvites);
        }

        // ========================================
        // 🟫 EINLADUNG ANNEHMEN / ABLEHNEN
        // ========================================
        [HttpPost("RespondInvitation")]
        public async Task<IActionResult> RespondInvitation([FromQuery] int participantId, [FromQuery] bool accept)
        {
            Console.WriteLine($"📩 RespondInvitation received: {participantId}, accept={accept}");
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            var participant = await _context.CalendarEventParticipants
                .Include(p => p.CalendarEvent)
                .FirstOrDefaultAsync(p => p.Id == participantId && p.UserId == user.Id);

            if (participant == null)
                return NotFound("Teilnahme nicht gefunden.");

            participant.Status = accept
                ? EventParticipationStatus.Accepted
                : EventParticipationStatus.Declined;

            // 🔹 Notification updaten
            var notif = await _context.UserNotifications
                .Include(un => un.Notification)
                .FirstOrDefaultAsync(un =>
                    un.UserId == user.Id &&
                    un.Notification.RelatedEntityId == participantId);

            if (notif != null)
            {
                notif.IsRead = true;
                notif.Notification.Content = accept
                    ? $"✅ Einladung zu „{participant.CalendarEvent.Title}“ angenommen."
                    : $"❌ Einladung zu „{participant.CalendarEvent.Title}“ abgelehnt.";
            }

            await _context.SaveChangesAsync();

            return Ok(new
            {
                message = accept ? "Einladung angenommen" : "Einladung abgelehnt"
            });
        }


        // ========================================
        // 🟦 EINLADUNG ANTWORT AUS NOTIFICATION
        // ========================================
        [HttpPost("RespondInvitationFromNotification")]
        public async Task<IActionResult> RespondInvitationFromNotification(int id, bool accept)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            // Finde UserNotification → Notification → CalendarEventParticipant
            var userNotif = await _context.UserNotifications
                .Include(un => un.Notification)
                .FirstOrDefaultAsync(un => un.Id == id && un.UserId == user.Id);

            if (userNotif?.Notification == null)
                return NotFound();

            // Suche Teilnehmer anhand User und Event
            var participant = await _context.CalendarEventParticipants
                .Include(p => p.CalendarEvent)
                .FirstOrDefaultAsync(p =>
                    p.UserId == user.Id &&
                    p.CalendarEvent.Title == userNotif.Notification.Content
                        .Replace("Du wurdest zu „", "")
                        .Replace("“ eingeladen.", "").Trim()
                );

            if (participant == null)
                return NotFound();

            participant.Status = accept ? EventParticipationStatus.Accepted : EventParticipationStatus.Declined;
            userNotif.IsRead = true;
            await _context.SaveChangesAsync();

            return Ok(new { message = accept ? "Einladung angenommen" : "Einladung abgelehnt" });
        }

    }
    public class CalendarEventDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }

        public string? StartDate { get; set; }
        public string? EndDate { get; set; }

        public string? StartTime { get; set; }
        public string? EndTime { get; set; }

        public string EventType { get; set; } = "personal";
        public bool AllDay { get; set; } = true;

        // 🔥 NEU
        public List<string> InvitedUserIds { get; set; } = new();
    }

}
