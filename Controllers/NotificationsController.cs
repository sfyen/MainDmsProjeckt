using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using DmsProjeckt.Data;
namespace DmsProjeckt.Controllers
{
    [Authorize]
    [Route("Notifications")]
    public class NotificationsController : Controller
    {
        private readonly ApplicationDbContext _context;
        //private readonly UserManager<IdentityUser> _userManager;

        public NotificationsController(ApplicationDbContext context)
        {
            _context = context;
            //_userManager = userManager;
        }

        [HttpGet("GetUserNotifications")]
        public async Task<IActionResult> GetUserNotifications()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return Unauthorized();

            var notifications = await _context.UserNotifications
                .Include(un => un.Notification)
                .ThenInclude(n => n.NotificationType)
                .Where(un => un.UserId == userId)
                .OrderByDescending(un => un.ReceivedAt)
                .Take(20)
                .ToListAsync();

            var result = new List<object>();

            foreach (var un in notifications.Where(n => n.Notification != null))
            {
                var notif = un.Notification!;
                string? eventStart = null;
                string? eventEnd = null;
                string? organizerName = null;
                CalendarEventParticipant? participant = null;
                
                if (notif.NotificationType?.Name == "CalendarInv" && notif.RelatedEntityId.HasValue)
                {
                    // 1️⃣ Teilnehmer anhand RelatedEntityId finden
                    participant = await _context.CalendarEventParticipants
                           .FirstOrDefaultAsync(p => p.Id == notif.RelatedEntityId.Value);

                    if (participant != null)
                    {
                        // 2️⃣ Zugehöriges Event abrufen
                        var ev = await _context.CalendarEvents
                            .FirstOrDefaultAsync(e => e.Id == participant.CalendarEventId);

                        if (ev != null)
                        {
                            eventStart = ev.StartDate.ToString("dd.MM.yyyy") + " " + ev.StartTime;
                            eventEnd = ev.EndDate.ToString("dd.MM.yyyy") + " " + ev.EndTime;

                            // 3️⃣ Organisator ermitteln
                            if (!string.IsNullOrEmpty(ev.CreatedById))
                            {
                                organizerName = await _context.Users
                                    .Where(u => u.Id == ev.CreatedById)
                                    .Select(u => u.FullName)
                                    .FirstOrDefaultAsync();
                            }
                        }
                    }
                }


                result.Add(new
                {
                    un.Id,
                    Title = notif.Title ?? "(Keine Titelangabe)",
                    Content = notif.Content ?? "(Kein Inhalt verfügbar)",
                    Type = notif.NotificationType?.Name ?? "Unbekannt",
                    un.IsRead,
                    receivedAt = un.ReceivedAt.ToString("g"),
                    ActionLink = notif.ActionLink,
                    relatedEntityId = notif.RelatedEntityId,
                    EventStart = eventStart,
                    EventEnd = eventEnd,
                    OrganizerName = organizerName,
                    Status = participant?.Status  // 🟢 Hier!
                });

            }

            return Json(result);
        }



        // POST: /Notifications/MarkAsRead/5
        [HttpPost("MarkAsRead/{id}")]
        public async Task<IActionResult> MarkAsRead(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userNotification = await _context.UserNotifications
                .FirstOrDefaultAsync(un => un.Id == id && un.UserId == userId);

            if (userNotification == null) return NotFound();
            userNotification.IsRead = true;
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpPost("MarkAllAsRead")]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userNotifs = await _context.UserNotifications
                .Where(un => un.UserId == userId && !un.IsRead)
                .ToListAsync();

            foreach (var notif in userNotifs)
                notif.IsRead = true;

            await _context.SaveChangesAsync();
            return Ok();
        }

        // POST: /Notifications/ToggleNotification/5

        [HttpPost]
        public async Task<IActionResult> ToggleType([FromBody] ToggleTypeDto dto)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var setting = await _context.UserNotificationSettings
                .FirstOrDefaultAsync(s => s.UserId == userId && s.NotificationTypeId == dto.TypeId);
            if (setting == null)
            {
                setting = new UserNotificationSetting
                {
                    UserId = userId,
                    NotificationTypeId = dto.TypeId,
                    Enabled = dto.Enabled
                };
                _context.UserNotificationSettings.Add(setting);
            }
            else
            {
                setting.Enabled = dto.Enabled;
            }
            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpGet]
        public async Task<IActionResult> GetTypeSettings()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var types = await _context.NotificationTypes.ToListAsync();
            var settings = await _context.UserNotificationSettings
                .Where(s => s.UserId == userId)
                .ToListAsync();

            var result = types.Select(t => new {
                t.Id,
                t.Name,
                t.Description,
                Enabled = settings.FirstOrDefault(s => s.NotificationTypeId == t.Id)?.Enabled ?? true
            });

            return Json(result);
        }

    }
    public class ToggleTypeDto
    {
        public int TypeId { get; set; }
        public bool Enabled { get; set; }
    }
}
