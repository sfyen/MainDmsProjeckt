using System.Text.Json.Serialization;
using DmsProjeckt.Data;
using DmsProjeckt.Hubs;
using DmsProjeckt.Service;
using Firebase.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
namespace DmsProjeckt.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class ChatController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly WebDavStorageService _WebDav;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ILogger<ChatController> _logger;
        public ChatController(ApplicationDbContext db, UserManager<ApplicationUser> userManager, WebDavStorageService Webdav, IHubContext<ChatHub> hubContext, ILogger<ChatController> logger)
        {
            _db = db;
            _userManager = userManager;
            _WebDav = Webdav;
            _hubContext = hubContext;
            _logger = logger;
        }

        // 1. Alle Chats des Users (Gruppen und private)
        [HttpGet("userchats")]
        public async Task<IActionResult> GetUserChats()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
            {
                return Unauthorized(new { error = "User nicht gefunden oder nicht eingeloggt" });
            }

            try
            {
                var allUsers = await _db.Users.ToListAsync();

                // 🔹 Load all MessageRead entries for this user upfront to avoid DataReader conflicts
                var allReadMessageIds = await _db.MessageRead
                    .Where(r => r.UserId == user.Id)
                    .Select(r => r.MessageId)
                    .ToListAsync();

                // --- Gruppenchats laden ---
                var groupChatsRaw = await _db.ChatGroupMembers
                    .Where(cgm => cgm.UserId == user.Id)
                    .Select(cgm => new
                    {
                        cgm.ChatGroupId,
                        cgm.ChatGroup.Name,
                        cgm.ChatGroup.AvatarUrl
                    })
                    .ToListAsync();

                var groupChats = new List<ChatViewModel>();
                foreach (var gc in groupChatsRaw)
                {
                    var lastMessageTime = await _db.ChatMessages
                        .Where(m => m.GroupId == gc.ChatGroupId)
                        .OrderByDescending(m => m.SentAt)
                        .Select(m => (DateTime?)m.SentAt)
                        .FirstOrDefaultAsync();

                    var unreadMessages = await _db.ChatMessages
                        .Where(m => m.GroupId == gc.ChatGroupId && m.SenderId != user.Id)
                        .Select(m => m.Id)
                        .ToListAsync();

                    var unreadCount = unreadMessages.Count(msgId => !allReadMessageIds.Contains(msgId));

                    groupChats.Add(new ChatViewModel
                    {
                        ChatId = gc.ChatGroupId.ToString(),
                        Type = "group",
                        DisplayName = gc.Name,
                        AvatarUrl = string.IsNullOrEmpty(gc.AvatarUrl) ? "/images/group-icon.png" : gc.AvatarUrl,
                        LastMessageTime = lastMessageTime,
                        UnreadCount = unreadCount
                    });
                }

                // --- Private Chats laden ---
                var privateChatsRaw = await _db.ChatMessages
                    .Where(m => (m.SenderId == user.Id && m.ReceiverId != null) ||
                                (m.ReceiverId == user.Id && m.SenderId != null))
                    .Select(m => new
                    {
                        ChatPartnerId = m.SenderId == user.Id ? m.ReceiverId : m.SenderId,
                        m.SentAt,
                        m.Id,
                        m.SenderId,
                        m.ReceiverId
                    })
                    .Where(x => x.ChatPartnerId != null)
                    .ToListAsync();

                var privateChats = new List<ChatViewModel>();
                foreach (var group in privateChatsRaw.GroupBy(x => x.ChatPartnerId))
                {
                    var partnerId = group.Key;
                    var partner = allUsers.FirstOrDefault(u => u.Id == partnerId);

                    var unreadMessages = group
                        .Where(m => m.SenderId != user.Id)
                        .Select(m => m.Id)
                        .ToList();

                    var unreadCount = unreadMessages.Count(msgId => !allReadMessageIds.Contains(msgId));

                    privateChats.Add(new ChatViewModel
                    {
                        ChatId = partnerId,
                        Type = "private",
                        DisplayName = partner != null ? (partner.Vorname + " " + partner.Nachname).Trim() : "Unknown",
                        AvatarUrl = partner?.ProfilbildUrl,
                        LastMessageTime = group.OrderByDescending(m => m.SentAt)
                                               .Select(m => (DateTime?)m.SentAt)
                                               .FirstOrDefault(),
                        UnreadCount = unreadCount
                    });
                }

                // --- Alle Chats zusammen ---
                var allChats = groupChats.Concat(privateChats)
                    .OrderByDescending(c => c.LastMessageTime ?? DateTime.MinValue)
                    .ToList();

                return Ok(new
                {
                    UserChats = allChats,
                    AllUsers = allUsers.Select(u => new
                    {
                        u.Id,
                        u.UserName,
                        u.Email,
                        u.ProfilbildUrl,
                        u.Vorname,
                        u.Nachname
                    }),
                    CurrentUserId = user.Id
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message, stack = ex.StackTrace });
            }
        }




        // 2. Nachrichten für einen Chat abrufen
        [HttpGet("messages")]
        public async Task<IActionResult> GetChatMessages([FromQuery] string chatId, [FromQuery] string type)
        {
            var user = await _userManager.GetUserAsync(User);
            List<ChatMessage> chatMessages = new();
            string selectedChatName = null;
            string avatarUrl = null;  // <-- Avatar vorbereiten

            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(type))
                return BadRequest("chatId und type sind erforderlich");

            if (type == "group")
            {
                if (!int.TryParse(chatId, out int groupId))
                    return BadRequest("Ungültige Gruppen-ID");

                chatMessages = await _db.ChatMessages
                    .Where(m => m.GroupId == groupId)
                    .OrderBy(m => m.SentAt)
                    .Take(100)
                    .ToListAsync();

                selectedChatName = await _db.ChatGroups
                    .Where(g => g.Id == groupId)
                    .Select(g => g.Name)
                    .FirstOrDefaultAsync();

                avatarUrl = await _db.ChatGroups
                    .Where(g => g.Id == groupId)
                    .Select(g => g.AvatarUrl)
                    .FirstOrDefaultAsync();
            }
            else if (type == "private")
            {
                chatMessages = await _db.ChatMessages
                    .Where(m => (m.SenderId == user.Id && m.ReceiverId == chatId) ||
                                (m.ReceiverId == user.Id && m.SenderId == chatId))
                    .OrderBy(m => m.SentAt)
                    .Take(100)
                    .ToListAsync();

                selectedChatName = await _db.Users
                    .Where(u => u.Id == chatId)
                    .Select(u => u.Vorname + " " + u.Nachname)
                    .FirstOrDefaultAsync();

                avatarUrl = await _db.Users
                    .Where(u => u.Id == chatId)
                    .Select(u => u.ProfilbildUrl)
                    .FirstOrDefaultAsync();
            }
            else
            {
                return BadRequest("Ungültiger Typ");
            }

            // 🔹 Load all MessageRead entries to check who has read which messages
            var allMessageReads = await _db.MessageRead
                .Where(r => chatMessages.Select(m => m.Id).Contains(r.MessageId))
                .ToListAsync();

            // 🔹 Map messages with isRead status
            _logger.LogInformation("=== [GetChatMessages] === ChatId={chatId}, Type={type}, User={user}", chatId, type, user.Id);
            _logger.LogInformation("Nachrichten im Chat: {count}", chatMessages.Count);

            var messagesWithReadStatus = chatMessages.Select(m =>
            {
                bool isRead = false;

                if (m.SenderId == user.Id)
                {
                    if (m.GroupId != null)
                    {
                        isRead = _db.MessageRead.Any(r => r.MessageId == m.Id && r.UserId != user.Id);
                    }
                    else
                    {
                        isRead = _db.MessageRead.Any(r => r.MessageId == m.Id && r.UserId == m.ReceiverId);
                    }
                }

                _logger.LogInformation("MsgID={id}, Sender={sender}, Empf={recv}, Group={group}, IsRead={isRead}",
                    m.Id, m.SenderId, m.ReceiverId, m.GroupId, isRead);

                return new
                {
                    m.Id,
                    m.SenderId,
                    m.ReceiverId,
                    m.GroupId,
                    m.Message,
                    m.SentAt,
                    IsRead = isRead
                };
            }).ToList();


            return Ok(new
            {
                ChatMessages = messagesWithReadStatus,
                SelectedChatName = selectedChatName,
                AvatarUrl = string.IsNullOrEmpty(avatarUrl)
                    ? (type == "group" ? "/images/group-icon.png" : "/images/default-profile.png")
                    : avatarUrl
            });
        }

        // 3. Gruppe erstellen
        [HttpPost("creategroup")]
        public async Task<IActionResult> CreateGroup([FromForm] CreateGroupRequest dto, IFormFile? avatar)
        {
            if (dto == null)
                return BadRequest("Ungültiges JSON.");

            if (string.IsNullOrWhiteSpace(dto.GroupName) || dto.UserIds == null || !dto.UserIds.Any())
                return BadRequest("Gruppenname und mindestens ein Mitglied sind erforderlich.");

            var members = await _db.Users.Where(u => dto.UserIds.Contains(u.Id)).ToListAsync();
            var user = await _userManager.GetUserAsync(User);

            if (!members.Any() || user == null)
                return BadRequest("Ungültige Mitglieder");

            // Gruppe anlegen
            var group = new ChatGroup
            {
                Name = dto.GroupName
            };

            // 🔹 Falls Bild hochgeladen wurde → auf WebDAV speichern
            if (avatar != null && avatar.Length > 0)
            {
                var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif" };
                if (!allowedTypes.Contains(avatar.ContentType))
                    return BadRequest("Nur JPG, PNG oder GIF erlaubt.");

                var objectName = $"gruppen/{Guid.NewGuid()}_{Path.GetFileName(avatar.FileName)}";
                using var stream = avatar.OpenReadStream();

                // 🧠 Ici la méthode attend 3 arguments → (stream, remotePath, contentType)
                await _WebDav.UploadStreamAsync(stream, objectName, avatar.ContentType);

                // 🔹 URL complète de ton fichier WebDAV
                var baseUrl = _WebDav.BaseUrl.TrimEnd('/');
                var imageUrl = $"{baseUrl}/{objectName}";

                group.AvatarUrl = imageUrl;
            }
            else
            {
                group.AvatarUrl = "/images/group-icon.png"; // Bild Standard
            }

            _db.ChatGroups.Add(group);
            await _db.SaveChangesAsync();

            // 🔹 Alle Mitglieder + Ersteller hinzufügen
            var allMemberIds = new HashSet<string>(dto.UserIds) { user.Id };
            foreach (var memberId in allMemberIds)
            {
                _db.ChatGroupMembers.Add(new ChatGroupMember
                {
                    ChatGroupId = group.Id,
                    UserId = memberId
                });
            }

            await _db.SaveChangesAsync();

            return Ok(new { chatId = group.Id, avatarUrl = group.AvatarUrl });
        }


        // --- Gruppendetails abrufen ---
        [HttpGet("groupdetails")]
        public async Task<IActionResult> GetGroupDetails([FromQuery] int groupId)
        {
            var group = await _db.ChatGroups
                .Include(g => g.ChatGroupMembers)
                .ThenInclude(m => m.User)
                .FirstOrDefaultAsync(g => g.Id == groupId);

            if (group == null) return NotFound();

            return Ok(new
            {
                name = group.Name,
                avatarUrl = string.IsNullOrEmpty(group.AvatarUrl) ? "/images/group-icon.png" : group.AvatarUrl,
                members = group.ChatGroupMembers.Select(m => new {
                    m.User.Id,
                    name = (m.User.Vorname + " " + m.User.Nachname).Trim(),
                    avatarUrl = string.IsNullOrEmpty(m.User.ProfilbildUrl) ? "/images/default-profile.png" : m.User.ProfilbildUrl
                })
            });
        }

        // --- Gruppenname/Avatar ändern ---
        [HttpPost("updategroup")]
        public async Task<IActionResult> UpdateGroup([FromForm] int groupId, [FromForm] string? name, IFormFile? avatar)
        {
            var group = await _db.ChatGroups.FindAsync(groupId);
            if (group == null)
                return NotFound("❌ Groupe introuvable.");

            // 🔹 Mise à jour du nom du groupe
            if (!string.IsNullOrWhiteSpace(name))
                group.Name = name.Trim();

            // 🔹 Mise à jour de l’image d’avatar (upload vers WebDAV)
            if (avatar != null && avatar.Length > 0)
            {
                var allowedTypes = new[] { "image/jpeg", "image/png", "image/gif" };
                if (!allowedTypes.Contains(avatar.ContentType))
                    return BadRequest("⚠️ Seuls les fichiers JPG, PNG ou GIF sont autorisés.");

                // 🔸 Nouveau nom unique du fichier
                var safeFileName = Path.GetFileName(avatar.FileName);
                var objectName = $"gruppen/{Guid.NewGuid()}_{safeFileName}";

                try
                {
                    using var stream = avatar.OpenReadStream();

                    // 🔹 Upload vers WebDAV
                    await _WebDav.UploadStreamAsync(stream, objectName, avatar.ContentType);

                    // 🔹 Construction de l’URL publique ou interne selon ta config
                    var baseUrl = _WebDav.BaseUrl.TrimEnd('/');
                    group.AvatarUrl = $"{baseUrl}/{objectName}";

                    Console.WriteLine($"✅ Avatar du groupe mis à jour : {group.AvatarUrl}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Erreur lors du chargement de l’avatar : {ex.Message}");
                    return StatusCode(500, "Erreur lors du chargement de l’image.");
                }
            }

            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                name = group.Name,
                avatarUrl = group.AvatarUrl
            });
        }


        // --- Mitglied hinzufügen ---
        [HttpPost("addmember")]
        public async Task<IActionResult> AddMember([FromForm] int groupId, [FromForm] string userId)
        {
            if (await _db.ChatGroupMembers.AnyAsync(m => m.ChatGroupId == groupId && m.UserId == userId))
                return BadRequest("User ist schon Mitglied.");

            _db.ChatGroupMembers.Add(new ChatGroupMember { ChatGroupId = groupId, UserId = userId });
            await _db.SaveChangesAsync();

            return Ok(new { success = true });
        }

        // --- Gruppe verlassen ---
        [HttpPost("leavegroup")]
        public async Task<IActionResult> LeaveGroup([FromForm] int groupId)
        {
            var user = await _userManager.GetUserAsync(User);
            var member = await _db.ChatGroupMembers.FirstOrDefaultAsync(m => m.ChatGroupId == groupId && m.UserId == user.Id);

            if (member == null) return BadRequest("Du bist kein Mitglied.");

            _db.ChatGroupMembers.Remove(member);
            await _db.SaveChangesAsync();

            return Ok(new { success = true });
        }
        [HttpPost("markasread")]
        public async Task<IActionResult> MarkAsRead([FromBody] MarkReadDto dto)
        {

            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            _logger.LogInformation("=== [MarkAsRead] === ChatId={chatId}, Type={type}, User={user}", dto.ChatId, dto.Type, user.Id);


            IQueryable<ChatMessage>? query = null;

            if (dto.Type == "group")
            {
                if (!int.TryParse(dto.ChatId, out int groupId))
                    return BadRequest("Ungültige Gruppen-ID");
                query = _db.ChatMessages.Where(m => m.GroupId == groupId && m.SenderId != user.Id);
            }
            else if (dto.Type == "private")
            {
                query = _db.ChatMessages.Where(m => m.SenderId == dto.ChatId && m.ReceiverId == user.Id);
            }
            else
            {
                return BadRequest("Ungültiger Chat-Typ");
            }

            if (query == null) return BadRequest("Query error");

            var messages = await query.ToListAsync();
            
            // 🔹 Load existing MessageRead entries upfront to avoid sync queries in loop
            var messageIds = messages.Select(m => m.Id).ToList();
            var existingReads = await _db.MessageRead
                .Where(r => messageIds.Contains(r.MessageId) && r.UserId == user.Id)
                .Select(r => r.MessageId)
                .ToListAsync();
            
            var newlyRead = new List<ChatMessage>();
            _logger.LogInformation("Neue Nachrichten als gelesen markiert: {count}", newlyRead.Count);
            foreach (var msg in messages)
            {
                // Check if not already read
                if (!existingReads.Contains(msg.Id))
                {
                    _db.MessageRead.Add(new MessageRead
                    {
                        MessageId = msg.Id,
                        UserId = user.Id,
                        ReadAt = DateTime.UtcNow
                    });
                    newlyRead.Add(msg);
                    
                    // Notify sender via SignalR
                    await _hubContext.Clients.User(msg.SenderId).SendAsync("MessageRead", new
                    {
                        MessageId = msg.Id,
                        ChatId = dto.ChatId,
                        Type = dto.Type,
                        ReaderId = user.Id,
                        ReaderName = $"{user.Vorname} {user.Nachname}",
                        ReadAt = DateTime.UtcNow
                    });
                    _logger.LogInformation("→ MessageId={id}, Sender={sender}", msg.Id, msg.SenderId);
                }
            }

            await _db.SaveChangesAsync();
            return Ok(new { success = true, count = newlyRead.Count });
        }




        // --- DTOs / ViewModels ---
        public class CreateGroupRequest
        {
            [JsonPropertyName("groupName")]
            public string GroupName { get; set; }

            [JsonPropertyName("userIds")]
            public List<string> UserIds { get; set; }
        }

        public class ChatViewModel
        {
            public string ChatId { get; set; }
            public string Type { get; set; } // "group" oder "private"
            public string DisplayName { get; set; }
            public string AvatarUrl { get; set; }

            public DateTime? LastMessageTime { get; set; }
            public int UnreadCount { get; set; }
        }
        public class MarkReadDto
        {
            public string ChatId { get; set; }
            public string Type { get; set; } // "private" oder "group"
        }

    }
}
