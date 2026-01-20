using DmsProjeckt.Data;
using DmsProjeckt.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Mime;
using System.IO;
using Microsoft.AspNetCore.Http;


namespace DmsProjeckt.Controllers
{
    [Route("api/pdfproxy")]
    [ApiController]
    public class PdfProxyController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ApplicationDbContext _db;
        private readonly ChunkService _chunkService;
        private readonly WebDavStorageService _webDav;
        private readonly ILogger<PdfProxyController> _logger;

        public PdfProxyController(
            IHttpClientFactory httpClientFactory,
            ApplicationDbContext db,
            ChunkService chunkService,
            WebDavStorageService webDav,
            ILogger<PdfProxyController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _db = db;
            _chunkService = chunkService;
            _webDav = webDav;
            _logger = logger;
        }

        // 🔹 GET: api/dokumente?objectPath=...
        [HttpGet]
        public async Task<IActionResult> GetPdf([FromQuery] string? url, [FromQuery] string? objectPath, [FromQuery] Guid? dokumentId)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(objectPath))
                    objectPath = Uri.UnescapeDataString(objectPath);

                // 🔹 1️⃣ Chunked PDF erkennen und rekonstruieren
                if (!string.IsNullOrWhiteSpace(objectPath) &&
                    (objectPath.StartsWith("chunked://", StringComparison.OrdinalIgnoreCase) ||
                     objectPath.StartsWith("chunked%3A%2F%2F", StringComparison.OrdinalIgnoreCase)))
                {
                    var guidPart = objectPath
                        .Replace("chunked://", "")
                        .Replace("chunked%3A%2F%2F", "")
                        .Trim();

                    Console.WriteLine($"🧩 Chunked document detected: {guidPart}");

                    var dokument = dokumentId.HasValue
                        ? await _db.Dokumente.FirstOrDefaultAsync(d => d.Id == dokumentId.Value)
                        : await _db.Dokumente.FirstOrDefaultAsync(d => d.ObjectPath.Contains(guidPart));

                    if (dokument == null)
                        return NotFound($"❌ Kein Dokument gefunden für {objectPath}");

                    // 🔹 Chunked Streaming (On-Demand)
                    var contentType = GetContentType(Path.GetExtension(dokument.Dateiname));
                    return await ServeFileAsync(null, contentType, dokument.Dateiname ?? "chunked_document", asAttachment: false, chunkedDokumentId: dokument.Id);
                }

                // 🔹 2️⃣ Direkter WebDAV-Download
                if (!string.IsNullOrWhiteSpace(objectPath))
                {
                    var contentType = GetContentType(Path.GetExtension(objectPath));
                    return await ServeFileAsync(objectPath, contentType, Path.GetFileName(objectPath), asAttachment: false);
                }

                // 🔹 3️⃣ Fallback: Direktlink (HTTP URL)
                if (!string.IsNullOrWhiteSpace(url))
                {
                    var client = _httpClientFactory.CreateClient();
                    var response = await client.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                        return NotFound($"❌ PDF nicht gefunden: {url}");

                    var content = await response.Content.ReadAsByteArrayAsync();
                    var ext = Path.GetExtension(url) ?? ".pdf";
                    return File(content, GetContentType(ext));
                }

                return BadRequest("❌ Kein URL oder objectPath angegeben.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim PDF-Laden: {ex.Message}");
                return BadRequest("❌ Fehler beim PDF-Laden: " + ex.Message);
            }
        }

        [HttpGet("view/{id}")]
        public async Task<IActionResult> View(Guid id)
        {
            try
            {
                _logger.LogInformation($"📄 [PdfProxy] Aufruf von view/{id}");

                // 1️⃣ Versuche d'abord, eine Version zu laden
                var version = await _db.DokumentVersionen
                    .Include(v => v.Abteilung)
                    .Include(v => v.ApplicationUser)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == id);

                if (version != null)
                {
                    _logger.LogInformation($"📄 [PdfProxy] Öffne Version: {version.Dateiname}");

                    if (version.IsChunked || (version.ObjectPath?.Contains("/chunks/") ?? false) || (version.ObjectPath?.Contains("/reconstructed/") ?? false))
                    {
                        _logger.LogInformation("🧩 Version ist chunked (oder rekonstruiert) → starte Streaming");
                        var contentTypeChunk = GetContentType(Path.GetExtension(version.Dateiname));
                        return await ServeFileAsync(null, contentTypeChunk, version.Dateiname, asAttachment: false, chunkedDokumentId: version.Id);
                    }

                    if (string.IsNullOrWhiteSpace(version.ObjectPath))
                    {
                        _logger.LogWarning("⚠️ Version ohne gültigen Pfad ({Id})", version.Id);
                        return NotFound("❌ Kein gültiger Pfad für Version gefunden.");
                    }

                    // 🆕 TYPE DETECTION + UTF-8 safe filename
                    var contentType = GetContentType(Path.GetExtension(version.Dateiname));
                    return await ServeFileAsync(version.ObjectPath, contentType, version.Dateiname, asAttachment: false);
                }

                // 2️⃣ Falls keine Version → normales Dokument laden
                var dokument = await _db.Dokumente
                    .Include(d => d.Abteilung)
                    .Include(d => d.ApplicationUser)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == id);

                if (dokument == null)
                {
                    _logger.LogWarning($"❌ Kein Dokument oder Version gefunden für ID {id}");
                    return NotFound();
                }

                _logger.LogInformation($"📄 [PdfProxy] Öffne Hauptdokument: {dokument.Dateiname}");

                if (dokument.IsChunked || (dokument.ObjectPath?.Contains("/chunks/") ?? false) || (dokument.ObjectPath?.Contains("/reconstructed/") ?? false))
                {
                    _logger.LogInformation("🧩 Dokument ist chunked (oder rekonstruiert) → starte Streaming");
                    var contentTypeChunk = GetContentType(Path.GetExtension(dokument.Dateiname));
                    return await ServeFileAsync(null, contentTypeChunk, dokument.Dateiname, asAttachment: false, chunkedDokumentId: dokument.Id);
                }

                // 🆕 TYPE DETECTION + UTF-8 safe filename
                var contentTypeMain = GetContentType(Path.GetExtension(dokument.Dateiname));
                return await ServeFileAsync(dokument.ObjectPath, contentTypeMain, dokument.Dateiname, asAttachment: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "💥 Fehler in PdfProxyController.View");
                return StatusCode(500, "💥 Interner Fehler beim Lesen der Datei.");
            }
        }

        private static string GetContentType(string extension)
        {
            switch (extension?.ToLowerInvariant())
            {
                case ".pdf": return "application/pdf";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".tif":
                case ".tiff": return "image/tiff";
                case ".bmp": return "image/bmp";
                case ".svg": return "image/svg+xml";

                case ".doc": return "application/msword";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls": return "application/vnd.ms-excel";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".ppt": return "application/vnd.ms-powerpoint";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";

                case ".csv": return "text/csv";
                case ".txt": return "text/plain";
                case ".xml": return "application/xml";
                case ".json": return "application/json";

                case ".zip": return "application/zip";
                case ".rar": return "application/vnd.rar";
                case ".7z": return "application/x-7z-compressed";

                default: return "application/octet-stream";
            }
        }




        [HttpGet("viewAny/{id}")]
        public async Task<IActionResult> ViewAny(Guid id)
        {
            try
            {
                // 1️⃣ Cherche d'abord un document principal
                var dokument = await _db.Dokumente.FirstOrDefaultAsync(d => d.Id == id);
                if (dokument != null)
                {
                    _logger.LogInformation("📄 Öffne Hauptdokument {Name} ({Id})", dokument.Dateiname, dokument.Id);

                    if (dokument.IsChunked || (dokument.ObjectPath?.Contains("/reconstructed/") ?? false))
                    {
                        var contentTypeChunk = GetContentType(Path.GetExtension(dokument.Dateiname));
                        return await ServeFileAsync(null, contentTypeChunk, dokument.Dateiname, asAttachment: false, chunkedDokumentId: dokument.Id);
                    }

                    var contentTypeDownload = GetContentType(Path.GetExtension(dokument.Dateiname));
                    return await ServeFileAsync(dokument.ObjectPath, contentTypeDownload, dokument.Dateiname, asAttachment: false);
                }

                // 2️⃣ Cherche une version directement via son ID ou son OriginalId
                var version = await _db.DokumentVersionen
                    .FirstOrDefaultAsync(v => v.Id == id || v.OriginalId == id);

                if (version == null)
                {
                    _logger.LogWarning("❌ Keine Version oder Dokument gefunden für ID {Id}", id);
                    return NotFound("❌ Weder Dokument noch Version gefunden.");
                }

                _logger.LogInformation("📄 Öffne Version {Name} ({Id})", version.Dateiname, version.Id);

                // 🔹 Récupère le fichier depuis WebDAV
                var contentTypeVer = GetContentType(Path.GetExtension(version.Dateiname));
                return await ServeFileAsync(version.ObjectPath, contentTypeVer, version.Dateiname, asAttachment: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Öffnen von Dokument oder Version.");
                return StatusCode(500, "Interner Fehler beim Öffnen der Datei: " + ex.Message);
            }
        }



        // 🔹 GET: api/dokumente/download/{id}
        [HttpGet("download/{id}")]
        public async Task<IActionResult> DownloadDokument(Guid id)
        {
            try
            {
                var dokument = await _db.Dokumente.FirstOrDefaultAsync(d => d.Id == id);
                if (dokument == null)
                    return NotFound("❌ Dokument nicht gefunden.");

                Console.WriteLine($"📦 Download-Anfrage für {dokument.Dateiname} (ID={id})");

                var contentType = GetContentType(Path.GetExtension(dokument.Dateiname));

                // 🔹 Chunked Datei → Streaming
                if (dokument.IsChunked || (dokument.ObjectPath?.Contains("/reconstructed/") ?? false))
                {
                    return await ServeFileAsync(null, contentType, dokument.Dateiname ?? "dokument", asAttachment: true, chunkedDokumentId: dokument.Id);
                }

                // 🔹 Normales Dokument → direkt von WebDAV
                if (!string.IsNullOrEmpty(dokument.ObjectPath))
                {
                    return await ServeFileAsync(dokument.ObjectPath, contentType, dokument.Dateiname ?? "dokument", asAttachment: true);
                }

                return NotFound("❌ Dokumentpfad ungültig oder leer.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Herunterladen: {ex.Message}");
                return StatusCode(500, "Fehler beim Herunterladen der Datei.");
            }
        }
        private async Task<IActionResult> ServeFileAsync(string? path, string contentType, string? fileName = null, bool asAttachment = false, Guid? chunkedDokumentId = null)
        {
            // 0. Chunked Streaming
            if (chunkedDokumentId.HasValue)
            {
                 var stream = await _chunkService.GetStreamForChunkedFileAsync(chunkedDokumentId.Value);
                 if (stream == null) return NotFound("❌ Chunk-Stream konnte nicht initialisiert werden.");
                 
                 // Handle Content-Disposition
                 if (!asAttachment && !string.IsNullOrEmpty(fileName))
                 {
                    var safeName = Uri.EscapeDataString(fileName);
                    Response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{safeName}";
                 }
                 
                 return File(stream, contentType, asAttachment ? fileName : null, enableRangeProcessing: true);
            }

            if (string.IsNullOrWhiteSpace(path)) return NotFound("❌ Pfad ist leer.");

            // Handle Content-Disposition
            if (!asAttachment && !string.IsNullOrEmpty(fileName))
            {
                var safeName = Uri.EscapeDataString(fileName);
                Response.Headers["Content-Disposition"] = $"inline; filename*=UTF-8''{safeName}";
            }

            // 1. Local File
            if (!path.StartsWith("http", StringComparison.OrdinalIgnoreCase) && 
                !path.StartsWith("https", StringComparison.OrdinalIgnoreCase) && 
                System.IO.File.Exists(path))
            {
                _logger.LogInformation($"📂 Serving local file: {path}");
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return File(fs, contentType, asAttachment ? fileName : null, enableRangeProcessing: true);
            }

            // 2. WebDAV File (Streaming with Range Support)
            var range = Request.Headers["Range"].ToString();
            var result = await _webDav.DownloadStreamWithRangeAsync(path, range);

            if (result == null)
                return NotFound($"❌ Datei nicht auf WebDAV gefunden: {path}");

            _logger.LogInformation($"✅ [PdfProxy] WebDAV stream obtained for {path}. Starting response (Range: {range})...");

            if (result.StatusCode == System.Net.HttpStatusCode.PartialContent)
            {
                Response.StatusCode = (int)System.Net.HttpStatusCode.PartialContent;
                if (result.ContentRange != null)
                    Response.Headers["Content-Range"] = result.ContentRange.ToString();
                if (result.ContentLength.HasValue)
                    Response.ContentLength = result.ContentLength;
                
                Response.Headers["Accept-Ranges"] = "bytes";
                
                return File(result.Stream, contentType, asAttachment ? fileName : null, enableRangeProcessing: false);
            }

            // Normal 200 OK
            if (result.ContentLength.HasValue)
                Response.ContentLength = result.ContentLength;
            
            Response.Headers["Accept-Ranges"] = "bytes";
                
            return File(result.Stream, contentType, asAttachment ? fileName : null, enableRangeProcessing: false);
        }
    }
}
