using DmsProjeckt.Data;
using DmsProjeckt.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DokumentIndexController : ControllerBase
    {
        private readonly DokumentIndexService _service;
        private readonly ApplicationDbContext _context;
        private readonly EmailService _emailService;
        private readonly WebDavStorageService _webDavStorage;

        public DokumentIndexController(
            DokumentIndexService service,
            ApplicationDbContext context,
            EmailService emailService,
            WebDavStorageService webDavStorage)
        {
            _service = service;
            _context = context;
            _emailService = emailService;
            _webDavStorage = webDavStorage;
        }

        // 🔹 Liste de tous les documents indexés
        [HttpGet("all")]
        public async Task<IActionResult> GetAll()
        {
            var docs = await _service.GetAllIndexedAsync();
            return Ok(docs);
        }

        // 🔹 Envoi d’un document par email
        [HttpPost]
        public async Task<IActionResult> SendEmail([FromBody] ShareEmailDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Dateiname))
                return BadRequest("⚠️ Ungültige Anfrage (Dateiname fehlt).");

            // 🔍 Dokument anhand des Dateinamens finden
            var dokument = await _context.Dokumente
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Dateiname == dto.Dateiname);

            if (dokument == null)
                return NotFound("❌ Dokument nicht gefunden!");

            try
            {
                // 📥 Datei vom WebDAV laden
                var fileStream = await _webDavStorage.DownloadStreamAsync(dokument.ObjectPath);
                if (fileStream == null || fileStream.Length == 0)
                    return NotFound("❌ Datei konnte nicht geladen werden.");

                byte[] fileBytes;
                using (var ms = new MemoryStream())
                {
                    await fileStream.CopyToAsync(ms);
                    fileBytes = ms.ToArray();
                }

                // 📧 Email senden
                await _emailService.SendEmailAsync(
                    dto.Empfaenger,
                    dto.Betreff,
                    dto.Nachricht,
                    new List<(byte[], string, string)>
                    {
                        (fileBytes, dto.Dateiname, "application/pdf")
                    });

                Console.WriteLine($"✅ Email erfolgreich mit Anhang {dto.Dateiname} gesendet.");
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim E-Mail-Versand: {ex.Message}");
                return StatusCode(500, "Fehler beim Senden der E-Mail.");
            }
        }

        // 🔹 Download eines Dokuments direkt vom WebDAV
        [HttpGet("download")]
        public async Task<IActionResult> Download(string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return BadRequest("⚠️ Kein Dateiname angegeben.");

            var dokument = await _context.Dokumente
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Dateiname == file);

            if (dokument == null)
                return NotFound("❌ Dokument nicht gefunden!");

            try
            {
                var fileStream = await _webDavStorage.DownloadStreamAsync(dokument.ObjectPath);
                if (fileStream == null)
                    return NotFound("❌ Datei nicht gefunden auf WebDAV.");

                Response.Headers["Content-Disposition"] = $"inline; filename=\"{file}\"";
                return File(fileStream, "application/pdf");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Download: {ex.Message}");
                return StatusCode(500, "Fehler beim Herunterladen der Datei.");
            }
        }
    }

    // 🔸 DTOs
    public class ShareEmailDto
    {
        public string Dateiname { get; set; } = string.Empty;
        public string Empfaenger { get; set; } = string.Empty;
        public string Betreff { get; set; } = string.Empty;
        public string Nachricht { get; set; } = string.Empty;
    }

    public class RenameFolderRequest
    {
        public string Path { get; set; } = string.Empty;
        public string NewName { get; set; } = string.Empty;
    }
}
