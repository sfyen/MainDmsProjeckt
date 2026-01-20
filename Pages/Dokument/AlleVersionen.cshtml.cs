using DmsProjeckt.Data;
using Google.Apis.Storage.v1;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using DmsProjeckt.Service;

namespace DmsProjeckt.Pages.Dokument;

public class AlleVersionenModel : PageModel
{
    private readonly ApplicationDbContext _db;
    private readonly WebDavStorageService _WebDav;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly EmailService _emailService;
    private readonly ILogger<AlleVersionenModel>? _logger;
    [BindProperty]
    public Guid DokumentId { get; set; }

    [BindProperty]
    public string Text { get; set; } = string.Empty;
    public class VersionInfo
    {
        public Guid DokumentId { get; set; }
        public string OriginalName { get; set; } = "";
        public string Dateiname { get; set; } = "";
        public DateTime HochgeladenAm { get; set; }
        public string SasUrl { get; set; } = "";
        public string ObjectPath { get; set; } = "";

    }

    public List<VersionInfo> Versionen { get; private set; } = new();

    public AlleVersionenModel(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        WebDavStorageService Webdav,
        EmailService emailService,
        ILogger<AlleVersionenModel>? logger)
    {
        _db = db;
        _userManager = userManager;
        _WebDav = Webdav;
        _emailService = emailService;
        _logger = logger;
    }
    // ✅ Corriger ici avec le bon type :
    public List<VersionGroup> GruppierteVersionen { get; set; } = new();


    public class VersionItem
    {
        public Guid OriginalId { get; set; }   // Guid vom Original
        public Guid? VersionId { get; set; }    // int Id der Version (nullable, weil Original keine hat)

        public string OriginalName { get; set; }
        public string Dateiname { get; set; }
        public string SasUrl { get; set; }
        public string ObjectPath { get; set; }
        public DateTime HochgeladenAm { get; set; }
        public Guid DokumentId { get; set; }
        public string Kategorie { get; set; }
        public int CommentCount { get; set; }
        public string CommentSummary { get; set; }
        public string VersionLabel { get; set; } = "";
        public bool IsOriginal { get; set; }
        public string Benutzer { get; set; } = "";
    }
    public class VersionGroup
    {
        public string OriginalName { get; set; }
        public List<VersionItem> Versions { get; set; }
    }

    public async Task OnGetAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        // 🧩 IDs der Dokumente mit Versionen
        var dokumentIdsMitVersionen = await _db.DokumentVersionen
            .Select(v => v.DokumentId)
            .Distinct()
            .ToListAsync();

        // 📄 Nur Dokumente laden, die auch Versionen haben
        var dokumente = await _db.Dokumente
            .Include(d => d.ApplicationUser)
            .Where(d => d.ApplicationUserId == userId
                && dokumentIdsMitVersionen.Contains(d.Id))
            .ToListAsync();

        // 🧾 Versionen laden (mit Benutzer)
        var versionen = await _db.DokumentVersionen
            .Include(v => v.ApplicationUser)
            .Where(v => v.ApplicationUserId == userId)
            .ToListAsync();

        // 💬 Kommentare sammeln
        var kommentare = await _db.Kommentare
            .Where(k => k.ApplicationUserId == userId)
            .GroupBy(k => k.DokumentId)
            .ToDictionaryAsync(
                g => g.Key,
                g => new
                {
                    Count = g.Count(),
                    Summary = string.Join("<br/>", g
                        .OrderByDescending(c => c.ErstelltAm)
                        .Take(3)
                        .Select(c => $"<small>{c.ErstelltAm:dd.MM.yy HH:mm} – {c.Text}</small>"))
                });

        // 🧱 Gruppen bauen
        var gruppen = dokumente
            .Select(orig => new VersionGroup
            {
                OriginalName = orig.Dateiname,
                Versions = (
                    new[]
                    {
                    // 🟩 ORIGINAL
                    new VersionItem
                    {
                        OriginalId = orig.Id,
                        VersionId = null,
                        Dateiname = orig.Dateiname,
                        VersionLabel = "Original",
                        Kategorie = orig.Kategorie,
                        // ✅ Correction : ObjectPath prioritaire
                        ObjectPath = !string.IsNullOrEmpty(orig.ObjectPath) ? orig.ObjectPath : orig.Dateipfad,
                        HochgeladenAm = orig.HochgeladenAm,
                        CommentCount = kommentare.ContainsKey(orig.Id) ? kommentare[orig.Id].Count : 0,
                        CommentSummary = kommentare.ContainsKey(orig.Id) ? kommentare[orig.Id].Summary : "",
                        IsOriginal = true,
                        Benutzer = orig.ApplicationUser?.FullName ?? "-"
                    }
                    }
                    .Concat(
                        // 🟦 VERSIONEN
                        versionen
                        .Where(v => v.DokumentId == orig.Id)
                        .Select(v => new VersionItem
                        {
                            OriginalId = orig.Id,
                            VersionId = v.Id,
                            Dateiname = v.Dateiname,
                            VersionLabel = string.IsNullOrWhiteSpace(v.VersionsLabel) ? "Version" : v.VersionsLabel,
                            Kategorie = orig.Kategorie,
                            // ✅ Correction ici : ObjectPath prioritaire
                            ObjectPath = !string.IsNullOrEmpty(v.ObjectPath) ? v.ObjectPath : v.Dateipfad,
                            HochgeladenAm = v.HochgeladenAm,
                            CommentCount = kommentare.ContainsKey(v.DokumentId) ? kommentare[v.DokumentId].Count : 0,
                            CommentSummary = kommentare.ContainsKey(v.DokumentId) ? kommentare[v.DokumentId].Summary : "",
                            IsOriginal = false,
                            Benutzer = v.ApplicationUser?.FullName ?? "-"
                        })
                    )
                    .OrderByDescending(x => x.IsOriginal)
                    .ThenByDescending(x => x.HochgeladenAm)
                    .ToList()
                )
            })
            .ToList();

        // 🔗 SAS-URLs async nachladen (WebDAV Links)
        foreach (var g in gruppen)
        {
            foreach (var v in g.Versions)
            {
                if (!string.IsNullOrEmpty(v.ObjectPath))
                    v.SasUrl = await _WebDav.GetDownloadUrlAsync(v.ObjectPath);
            }
        }

        // ✅ Résultat final pour l'affichage
        GruppierteVersionen = gruppen;
    }




    public async Task<FileResult> OnGetDownloadAllAsync(string original)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        var versiongruppen = await _db.DokumentVersionen
            .Where(v => v.ApplicationUserId == userId
                && _db.Dokumente
                    .Where(d => d.ApplicationUserId == userId)
                    .Any(d => d.Id == v.DokumentId && d.Dateiname == original))
            .OrderByDescending(v => v.HochgeladenAm)
            .ToListAsync();

        if (!versiongruppen.Any())
        {
            var message = $"Keine Versionen gefunden für \"{original}\".";
            return new FileContentResult(System.Text.Encoding.UTF8.GetBytes(message), "text/plain");
        }

        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var v in versiongruppen)
            {
                using var fileStream = await _WebDav.DownloadStreamAsync(v.Dateipfad);
                if (fileStream == null) continue;

                var entry = archive.CreateEntry(v.Dateiname, System.IO.Compression.CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                await fileStream.CopyToAsync(entryStream);
            }
        }

        zipStream.Seek(0, SeekOrigin.Begin);
        return File(zipStream.ToArray(), "application/zip", $"{original}-alle-versionen.zip");
    }


    public async Task<IActionResult> OnPostArchiveOldAsync(string original)
    {
        var vers = await _db.DokumentVersionen.Where(v => v.ApplicationUserId == User.FindFirstValue(ClaimTypes.NameIdentifier)
                              && _db.Dokumente.FirstOrDefault(d => d.Id == v.DokumentId).Dateiname == original)
                              .OrderByDescending(v => v.HochgeladenAm).ToListAsync();
        if (vers.Count > 1)
        {
            var toArchive = vers.Skip(1);
            _db.DokumentVersionen.RemoveRange(toArchive);
            await _db.SaveChangesAsync();
        }
        return new JsonResult(new { ok = true });
    }

    public async Task<JsonResult> OnGetGetCommentsAsync(Guid docId)
    {
        var comments = await _db.Kommentare
            .Where(c => c.DokumentId == docId)
            .OrderBy(c => c.ErstelltAm)
            .ToListAsync();

        var html = string.Join("<br/>", comments.Select(c =>
            $"<small>{c.ErstelltAm:dd.MM.yyyy HH:mm} – {c.Text}</small>"));

        return new JsonResult(new { html });
    }

    [IgnoreAntiforgeryToken] // pour ignorer les erreurs CSRF lors du test, à sécuriser ensuite
    public async Task<JsonResult> OnPostAddCommentAsync(Guid dokumentId, string text)
    {
        _db.Kommentare.Add(new Kommentare
        {
            DokumentId = dokumentId,
            Text = text,
            ErstelltAm = DateTime.UtcNow,
            ApplicationUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            BenutzerId = User.Identity?.Name ?? "Unbekannt"
        });

        await _db.SaveChangesAsync();
       
        return new JsonResult(new { ok = true });
    }
    public async Task<IActionResult> OnGetPdfProxyAsync(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(id, out var guidId))
            return BadRequest("❌ Ungültige Id (keine GUID).");

        _logger?.LogInformation($"📄 [AlleVersionen.PdfProxy] Anfrage für ID={guidId}");

        // ===================================================
        // 🔎 1️⃣ Prüfe zuerst in DokumentVersionen
        // ===================================================
        var version = await _db.DokumentVersionen
            .Include(v => v.ApplicationUser)
            .Include(v => v.Abteilung)
            .FirstOrDefaultAsync(v => v.Id == guidId && v.ApplicationUserId == userId);

        if (version != null)
        {
            _logger?.LogInformation($"📘 [PdfProxy] Gefundene Version: {version.Dateiname}");

            string path = version.ObjectPath ?? version.Dateipfad ?? "";
            path = Normalize(path);

            // 🧩 Wenn Version chunked → versuche Rekonstruktion
            if (version.IsChunked || path.Contains("/chunks/"))
            {
                _logger?.LogInformation($"🧩 Version ist chunked – starte Rekonstruktion für {guidId}");

                try
                {
                    var chunkService = HttpContext.RequestServices.GetRequiredService<ChunkService>();
                    var reconstructedPath = await chunkService.ReconstructFileFromWebDavAsync(guidId);

                    if (!string.IsNullOrEmpty(reconstructedPath))
                    {
                        path = Normalize(reconstructedPath);
                        _logger?.LogInformation($"✅ Rekonstruktion erfolgreich: {path}");
                    }
                    else
                    {
                        _logger?.LogWarning("⚠️ Rekonstruktion fehlgeschlagen, versuche Standardpfad.");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Fehler beim Rekonstruieren der Version");
                }
            }

            var stream = await _WebDav.DownloadStreamAsync(path);
            if (stream == null)
            {
                _logger?.LogWarning($"❌ Datei nicht gefunden (Version) → {path}");
                return NotFound($"Datei nicht gefunden (Version) → {path}");
            }

            Response.Headers["Content-Disposition"] = $"inline; filename=\"{version.Dateiname}\"";
            return File(stream, "application/pdf");
        }

        // ===================================================
        // 🔎 2️⃣ Fallback – Prüfe Dokument
        // ===================================================
        var dokument = await _db.Dokumente
            .Include(d => d.ApplicationUser)
            .Include(d => d.Abteilung)
            .FirstOrDefaultAsync(d => d.Id == guidId && d.ApplicationUserId == userId);

        if (dokument != null)
        {
            _logger?.LogInformation($"📗 [PdfProxy] Gefundenes Original: {dokument.Dateiname}");

            string path = dokument.ObjectPath ?? dokument.Dateipfad ?? "";
            path = Normalize(path);

            if (dokument.IsChunked || path.Contains("/chunks/"))
            {
                try
                {
                    var chunkService = HttpContext.RequestServices.GetRequiredService<ChunkService>();
                    var reconstructedPath = await chunkService.ReconstructFileFromWebDavAsync(guidId);

                    if (!string.IsNullOrEmpty(reconstructedPath))
                        path = Normalize(reconstructedPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "❌ Fehler beim Rekonstruieren des Dokuments");
                }
            }

            var stream = await _WebDav.DownloadStreamAsync(path);
            if (stream == null)
                return NotFound($"Datei nicht gefunden (Dokument) → {path}");

            Response.Headers["Content-Disposition"] = $"inline; filename=\"{dokument.Dateiname}\"";
            return File(stream, "application/pdf");
        }

        _logger?.LogWarning($"❌ Weder Dokument noch Version gefunden → {guidId}");
        return NotFound("❌ Kein passendes Dokument oder Version gefunden.");
    }

    private static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        input = input.Trim();

        // 🔹 Ne rien changer si déjà un chemin relatif correct
        if (input.StartsWith("dokumente/", StringComparison.OrdinalIgnoreCase))
            return input;

        // 🔹 Supprime le domaine WebDAV s'il est présent
        if (input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var idx = input.IndexOf("/DmsDaten/", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                return input.Substring(idx + "/DmsDaten/".Length);
        }

        // 🔹 Supprime tout éventuel slash de début
        return input.TrimStart('/');
    }


    public async Task<JsonResult> OnPostSendEmailAsync(string dokumentId, string to, string subject, string message)
    {
        if (string.IsNullOrWhiteSpace(to))
            return new JsonResult(new { ok = false, error = "Empfänger fehlt" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        string downloadUrl = null;

        // 🔹 Prüfen ob gültiger GUID
        if (!Guid.TryParse(dokumentId, out var guidId))
            return new JsonResult(new { ok = false, error = "Ungültige DokumentId (keine GUID)" });

        // 🔹 Erst versuchen, ein Original-Dokument zu finden
        var original = await _db.Dokumente
            .FirstOrDefaultAsync(d => d.Id == guidId && d.ApplicationUserId == userId);

        if (original != null)
        {
            downloadUrl = await _WebDav.GetDownloadUrlAsync(original.ObjectPath ?? original.Dateipfad);
        }
        else
        {
            // 🔹 Falls kein Original → nach einer Version suchen
            var version = await _db.DokumentVersionen
                .FirstOrDefaultAsync(v => v.Id == guidId && v.ApplicationUserId == userId);

            if (version == null)
                return new JsonResult(new { ok = false, error = "Dokument oder Version nicht gefunden" });

            downloadUrl = await _WebDav.GetDownloadUrlAsync(version.ObjectPath ?? version.Dateipfad);
        }

        // 📧 E-Mail Text bauen
        var body = $@"
    <p>{message}</p>
    <p>
        📎 <a href=""{downloadUrl}"" target=""_blank"">Dokument öffnen</a>
    </p>
    ";

        try
        {
            await _emailService.SendEmailAsync(to, subject, body);
            return new JsonResult(new { ok = true });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { ok = false, error = ex.Message });

        }
    }



    public async Task<IActionResult> OnGetExportCsvAsync()
    {

        return Content("TODO: Export CSV");
    }

}
