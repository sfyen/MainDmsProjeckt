using DmsProjeckt.Data;
using DmsProjeckt.Helpers;
using DmsProjeckt.Service;
using iText.Html2pdf;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Element;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.IO.Packaging;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;



namespace DmsProjeckt.Pages.Dokument
{
    [IgnoreAntiforgeryToken]
    public class BearbeitenModel : PageModel
    {
        private readonly WebDavStorageService _WebDav;
        private readonly ApplicationDbContext _dbContext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<BearbeitenModel> _logger;
        private readonly ChunkService _chunkService;
        private readonly IWebHostEnvironment _env;
        private readonly DbContextOptions<ApplicationDbContext> _dbContextOptions;
        public class TempSignatureStore
        {
            public Guid FileId { get; set; }
            public List<SignaturePayload> Signatures { get; set; } = new();
        }

        // Cache für Signaturen (kann später Redis oder DB sein)
        private static readonly Dictionary<Guid, TempSignatureStore> _pendingSignatures = new();

        public BearbeitenModel(
            WebDavStorageService WebDav,
            ApplicationDbContext dbContext,
            UserManager<ApplicationUser> userManager,
            ILogger<BearbeitenModel> logger,
            ChunkService chunkService,
            IWebHostEnvironment env,
            DbContextOptions<ApplicationDbContext> dbContextOptions)
        {
            _WebDav = WebDav;
            _dbContext = dbContext;
            _userManager = userManager;
            _logger = logger;
            _chunkService = chunkService;
            _env = env;
            _dbContextOptions = dbContextOptions;
        }

        [BindProperty(SupportsGet = true)]
        public Guid Id { get; set; }
        [BindProperty(SupportsGet = true)]
        public bool FromTask { get; set; }

        public string FileName { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty;
        public string SasUrl { get; set; } = string.Empty;

        public async Task<IActionResult> OnGetAsync()
        {
            if (Id == Guid.Empty)
                return BadRequest("❌ Dokument-Id fehlt.");

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Challenge();

            // =====================================================
            // 🔎 1️⃣ Hauptversuch: Dokument aus der Haupttabelle
            // =====================================================
            var dokument = await _dbContext.Dokumente
                .Include(d => d.Abteilung)
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == Id);

            bool isVersion = false;

            // =====================================================
            // 🔁 2️⃣ Wenn nicht gefunden → aus DokumentVersionen laden
            // =====================================================
            if (dokument == null)
            {
                var version = await _dbContext.DokumentVersionen
                    .Include(v => v.Abteilung)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == Id);

                if (version == null)
                {
                    _logger.LogWarning("❌ Dokument oder Version mit Id {Id} nicht gefunden", Id);
                    return NotFound();
                }

                isVersion = true;

                dokument = new Dokumente
                {
                    Id = version.Id,
                    OriginalId = version.OriginalId,
                    Dateiname = version.Dateiname,
                    Dateipfad = version.Dateipfad,
                    ObjectPath = version.ObjectPath,
                    ApplicationUserId = version.ApplicationUserId,
                    AbteilungId = version.AbteilungId,
                    Abteilung = version.Abteilung,
                    HochgeladenAm = version.HochgeladenAm,
                    EstSigne = version.EstSigne,
                    IsChunked = version.IsChunked,
                    IsVersion = true
                };

                // =====================================================
                // 🧬 Wenn es sich um eine Version handelt,
                // Kategorie aus dem Original-Dokument sicher laden
                // =====================================================
                if (version.OriginalId.HasValue)
                {
                    _logger.LogInformation("✅ Lade aktuelle Version {VersionId} (Pfad={Path})", version.Id, version.ObjectPath);

                    // ✅ Pfad & Basisdaten beibehalten
                    dokument.ObjectPath = version.ObjectPath ?? dokument.ObjectPath;
                    dokument.IsChunked = version.IsChunked;
                    dokument.Dateiname = version.Dateiname;
                    dokument.OriginalId = version.OriginalId;

                    // 🔍 Kategorie des Originals suchen
                    var original = await _dbContext.Dokumente
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == version.OriginalId.Value);

                    if (original != null && !string.IsNullOrWhiteSpace(original.Kategorie))
                    {
                        dokument.Kategorie = original.Kategorie.Trim().ToLowerInvariant();
                        _logger.LogInformation("📂 Kategorie von Original übernommen: {Kategorie}", dokument.Kategorie);
                    }
                    else
                    {
                        dokument.Kategorie ??= "misc";
                        _logger.LogWarning("⚠️ Keine Kategorie im Original gefunden, Standardwert 'misc' verwendet.");
                    }
                }
                else
                {
                    _logger.LogInformation("⚠️ Version ohne OriginalId – nutze gespeicherte Pfadangabe ({Path})", version.ObjectPath);
                    dokument.Kategorie ??= "misc";
                }

                // 🧹 Nettoyage pour éviter /versionen als Kategorie
                if (dokument.Kategorie?.Equals("versionen", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _logger.LogInformation("⚙️ Kategorie 'versionen' erkannt → wird aus Original korrigiert");

                    if (version.OriginalId.HasValue)
                    {
                        var orig = await _dbContext.Dokumente
                            .AsNoTracking()
                            .FirstOrDefaultAsync(d => d.Id == version.OriginalId.Value);

                        if (orig != null && !string.IsNullOrWhiteSpace(orig.Kategorie))
                        {
                            dokument.Kategorie = orig.Kategorie.Trim().ToLowerInvariant();
                            _logger.LogInformation("📂 Kategorie korrigiert zu {Kategorie}", dokument.Kategorie);
                        }
                        else
                        {
                            dokument.Kategorie = "misc";
                        }
                    }
                }
            }

            // =====================================================
            // ✅ 3️⃣ Pfad prüfen und normalisieren
            // =====================================================
            var filePath = dokument.ObjectPath;
            if (string.IsNullOrWhiteSpace(filePath))
                return BadRequest("❌ Dokument hat keinen gültigen Dateipfad.");

            if (!filePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                filePath = _WebDav.NormalizePath(filePath);

            // 🧹 Pfad-Duplikate bereinigen (z. B. /versionen/versionen)
            filePath = filePath.Replace("//", "/")
                               .Replace("/versionen/versionen", "/versionen")
                               .Trim();

            OriginalPath = filePath;
            _logger.LogInformation("📂 Bereinigter Dokumentpfad: {Path}", filePath);

            // =====================================================
            // 🖼️ Check if this is an image file - convert to base64
            // =====================================================
            string extension = System.IO.Path.GetExtension(dokument.Dateiname).ToLower();
            bool isImage = extension == ".jpg" || extension == ".jpeg" || extension == ".png";

            // =====================================================
            // 🧱 4️⃣ Chunked-Datei rekonstruieren
            // =====================================================
            if (dokument.IsChunked)
            {
                _logger.LogInformation("🧩 Dokument ist chunked — versuche Rekonstruktion...");

                var sourceId = dokument.OriginalId ?? dokument.Id;
                var reconstructed = await _chunkService.ReconstructFileFromWebDavAsync(sourceId);

                if (string.IsNullOrWhiteSpace(reconstructed) || !System.IO.File.Exists(reconstructed))
                {
                    _logger.LogWarning("⚠️ Rekonstruktion fehlgeschlagen für {File}", dokument.Dateiname);
                    TempData["ErrorMessage"] = "❌ Chunked-Datei konnte nicht rekonstruiert werden.";
                    SasUrl = string.Empty;
                    return Page();
                }

                _logger.LogInformation("✅ Rekonstruktion erfolgreich: {File}", reconstructed);
                _logger.LogDebug("📏 Rekonstruierte Dateigröße: {Size} Bytes", new FileInfo(reconstructed).Length);

                // For images, convert to base64
                if (isImage)
                {
                    byte[] imageBytes = await System.IO.File.ReadAllBytesAsync(reconstructed);
                    string mimeType = extension == ".png" ? "image/png" : "image/jpeg";
                    SasUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}";
                    _logger.LogInformation("🖼️ Bild als Base64 Data-URL bereitgestellt");
                }
                else
                {
                    SasUrl = reconstructed;
                }
                
                return Page();
            }

            // =====================================================
            // 📥 5️⃣ Nicht-chunked Datei vom WebDAV laden
            // =====================================================
            bool exists;
            try
            {
                exists = await _WebDav.FileExistsAsync(filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler bei FileExistsAsync für {Path}", filePath);
                return BadRequest($"Fehler bei WebDAV-Anfrage: {ex.Message}");
            }

            if (!exists)
            {
                _logger.LogWarning("❌ Datei existiert nicht auf WebDAV: {FilePath}", filePath);
                TempData["ErrorMessage"] = "❌ Dokument konnte nicht geladen werden.";
                SasUrl = string.Empty;
                return Page();
            }

            // =====================================================
            // 🖼️ 6️⃣ Für Bilder: Herunterladen und als Base64 bereitstellen
            // =====================================================
            if (isImage)
            {
                try
                {
                    using var stream = await _WebDav.DownloadStreamStableAsync(filePath);
                    if (stream == null)
                    {
                        _logger.LogWarning("⚠️ Konnte Bilddatei nicht vom WebDAV laden: {Path}", filePath);
                        TempData["ErrorMessage"] = "❌ Bild konnte nicht geladen werden.";
                        SasUrl = string.Empty;
                        return Page();
                    }

                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    byte[] imageBytes = ms.ToArray();
                    
                    string mimeType = extension == ".png" ? "image/png" : "image/jpeg";
                    SasUrl = $"data:{mimeType};base64,{Convert.ToBase64String(imageBytes)}";
                    
                    _logger.LogInformation("✅ Bild-URL bereit als Base64 Data-URL ({0} bytes)", imageBytes.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Fehler beim Laden des Bildes von WebDAV");
                    TempData["ErrorMessage"] = "❌ Fehler beim Laden des Bildes.";
                    SasUrl = string.Empty;
                    return Page();
                }
            }
            else
            {
                // =====================================================
                // 📄 Für PDFs: Normale URL bereitstellen
                // =====================================================
                SasUrl = filePath;
                _logger.LogInformation("✅ PDF-URL bereit: {Url}", SasUrl);
            }

            _logger.LogInformation(
                "✅ Geladenes Dokument: {Id}, IsVersion={IsVersion}, Pfad={Path}, Kategorie={Kategorie}",
                dokument.Id, isVersion, dokument.ObjectPath, dokument.Kategorie
            );

            return Page();
        }


        [HttpPost]
        public async Task<IActionResult> OnPostSaveSignature([FromBody] SignaturePayload payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ImageBase64))
                return BadRequest("❌ Ungültige Signatur-Daten.");

            // ==========================================================
            // 1️⃣ Dokument oder Version laden
            // ==========================================================
            var dokument = await _dbContext.Dokumente
                .Include(d => d.Abteilung)
                .FirstOrDefaultAsync(d => d.Id == payload.FileId);

            if (dokument == null)
            {
                // 🧩 Falls nicht gefunden → versuchen, in Versionen
                var version = await _dbContext.DokumentVersionen
                    .Include(v => v.Abteilung)
                    .FirstOrDefaultAsync(v => v.Id == payload.FileId);

                if (version == null)
                    return NotFound("❌ Dokument nicht gefunden.");

                // ✅ Kategorie forcée depuis Original
                var original = version.OriginalId.HasValue
                    ? await _dbContext.Dokumente
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == version.OriginalId.Value)
                    : null;

                dokument = new Dokumente
                {
                    Id = version.Id,
                    OriginalId = version.OriginalId,
                    Dateiname = version.Dateiname,
                    ObjectPath = version.ObjectPath,
                    Dateipfad = version.Dateipfad,
                    AbteilungId = version.AbteilungId,
                    Abteilung = version.Abteilung,
                    IsVersion = true,
                    EstSigne = version.EstSigne,
                    IsChunked = version.IsChunked,
                    Kategorie = original?.Kategorie?.Trim().ToLowerInvariant() ?? "misc"
                };

                _logger.LogInformation("📂 Kategorie übernommen von Original: {Kategorie}", dokument.Kategorie);
            }

            try
            {
                var user = await _userManager.GetUserAsync(User);
                if (user == null)
                    return Unauthorized();

                _logger.LogInformation("🖊️ [Signatur] Vorgang gestartet für Dokument {Id}", dokument.Id);

                // ==========================================================
                // 📥 2️⃣ PDF vom WebDAV laden
                // ==========================================================
                using var inputStream = await _WebDav.DownloadStreamStableAsync(dokument.ObjectPath);
                if (inputStream == null)
                    return NotFound("❌ Quelldatei nicht gefunden auf WebDAV.");

                inputStream.Position = 0; // Rewind du stream

                // ==========================================================
                // 🟡 3️⃣ Append Mode – Signatur hinzufügen
                // ==========================================================
                _logger.LogInformation("🟡 [AppendMode] Starte PDF-Append für Signatur");
                byte[] appendedPdfBytes = AppendSignatureToPdf(inputStream, payload);
                _logger.LogInformation("✅ [AppendMode] Append erfolgreich ({Length} Bytes)", appendedPdfBytes.Length);

                using var outputStream = new MemoryStream(appendedPdfBytes);

                // ==========================================================
                // 🗂️ 4️⃣ Speicherpfad vorbereiten
                // ==========================================================
                string firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";

                string abteilungName;
                if (dokument.Abteilung != null && !string.IsNullOrWhiteSpace(dokument.Abteilung.Name))
                {
                    abteilungName = dokument.Abteilung.Name.Trim().ToLowerInvariant();
                }
                else if (dokument.AbteilungId != null)
                {
                    var abt = await _dbContext.Abteilungen
                        .AsNoTracking()
                        .FirstOrDefaultAsync(a => a.Id == dokument.AbteilungId);
                    abteilungName = abt?.Name?.Trim().ToLowerInvariant() ?? "allgemein";
                }
                else
                {
                    abteilungName = "allgemein";
                }

                // ==========================================================
                // ✅ KATEGORIE-SICHERUNG & PFAD-KORREKTUR
                // ==========================================================
                string kategorie = dokument.Kategorie?.Trim().ToLowerInvariant() ?? "misc";

                if (kategorie == "versionen" && dokument.OriginalId != null)
                {
                    var originalDoc = await _dbContext.Dokumente
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == dokument.OriginalId);

                    if (originalDoc != null && !string.IsNullOrWhiteSpace(originalDoc.Kategorie))
                    {
                        kategorie = originalDoc.Kategorie.Trim().ToLowerInvariant();
                        _logger.LogInformation("📂 Kategorie korrigiert von 'versionen' → {Kategorie}", kategorie);
                    }
                    else
                    {
                        kategorie = "misc";
                        _logger.LogWarning("⚠️ Keine Kategorie im Original gefunden, fallback → misc");
                    }
                }

                // Nettoyage de tout résidu "/versionen"
                kategorie = kategorie.Replace("/versionen", "").Trim('/');

                // ✅ Dossier final cohérent
                // ==========================================================
                // 💾 6️⃣ Neue Version in DB speichern (Zählen für Label und Dateiname nötig)
                // ==========================================================
                int versionCount = await _dbContext.DokumentVersionen
                    .CountAsync(v => v.OriginalId == (dokument.OriginalId ?? dokument.Id));

                // ✅ Clean Version Name: OriginalName_V{x}.ext
                string ext = System.IO.Path.GetExtension(dokument.Dateiname);
                string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(dokument.Dateiname);
                string versionFileName = $"{nameNoExt}_V{versionCount + 1}{ext}";

                // ✅ Dossier final cohérent
                string versionFolder = $"dokumente/{firma}/{abteilungName}/{kategorie}/versionen";
                await _WebDav.EnsureFolderTreeExistsAsync(versionFolder);

                string versionPath = $"{versionFolder}/{versionFileName}";

                _logger.LogInformation("📂 Signierte Version wird gespeichert unter: {Path}", versionPath);

                // ==========================================================
                // 📤 5️⃣ Upload neue Version
                // ==========================================================
                outputStream.Position = 0;
                await _WebDav.UploadStreamAsync(outputStream, versionPath, "application/pdf");

                // ==========================================================
                // 💾 6️⃣ Neue Version in DB speichern
                // ==========================================================


                var newVersion = new DokumentVersionen
                {
                    DokumentId = dokument.Id,
                    ObjectPath = versionPath,
                    Dateiname = System.IO.Path.GetFileName(versionFileName),
                    AbteilungId = dokument.AbteilungId,
                    ApplicationUserId = user.Id,
                    EstSigne = true,
                    IsVersion = true,
                    HochgeladenAm = DateTime.UtcNow,
                    VersionsLabel = $"v{versionCount + 1}",
                    OriginalId = dokument.OriginalId ?? dokument.Id,
                    Kategorie = kategorie
                };

                _dbContext.DokumentVersionen.Add(newVersion);
                await _dbContext.SaveChangesAsync();

                // ==========================================================
                // 🧠 7️⃣ Clonage & mise à jour des métadonnées (nouveau)
                // ==========================================================
                await CloneAndAttachMetadataAsync(dokument, newVersion, new SaveRequest
                {
                    Metadata = new OriginalMetadataDto
                    {
                        Beschreibung = $"Signierte Version erstellt am {DateTime.UtcNow:dd.MM.yyyy HH:mm}",
                        PdfAutor = user.UserName,
                        PdfBetreff = "Signierte Kopie",
                        PdfSchluesselwoerter = "Signatur, Version",
                        OCRText = "Signatur hinzugefügt"
                    }
                });

                _logger.LogInformation("✅ Neue signierte Version erfolgreich gespeichert: {Path}", versionPath);

                // ==========================================================
                // ✅ 8️⃣ Antwort JSON
                // ==========================================================
                return new JsonResult(new
                {
                    success = true,
                    message = $"✔️ Signatur als neue Version gespeichert ({newVersion.VersionsLabel}).",
                    path = versionPath
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Speichern der Signatur.");
                return StatusCode(500, new { success = false, message = ex.InnerException?.Message ?? ex.Message });
            }
        }

        private byte[] AppendSignatureToPdf(Stream inputPdf, SignaturePayload payload)
        {
            using var outputStream = new MemoryStream();
            using var reader = new PdfReader(inputPdf);
            reader.SetUnethicalReading(true);

            // ✅ Append mode activé : ne réécrit que le diff
            var writerProps = new WriterProperties().SetFullCompressionMode(false);
            var stampingProps = new StampingProperties().UseAppendMode();

            using (var pdfDoc = new PdfDocument(reader, new PdfWriter(outputStream, writerProps), stampingProps))
            {
                var doc = new Document(pdfDoc);

                string base64Data = payload.ImageBase64.Contains(",")
                    ? payload.ImageBase64.Split(',')[1]
                    : payload.ImageBase64;

                byte[] imageBytes = Convert.FromBase64String(base64Data);
                var image = new iText.Layout.Element.Image(ImageDataFactory.Create(imageBytes))
                    .ScaleToFit(payload.Width, payload.Height);

                var page = pdfDoc.GetPage(payload.PageNumber);
                float pageHeight = page.GetPageSize().GetHeight();
                float correctedY = pageHeight - payload.Y - payload.Height;

                image.SetFixedPosition(payload.PageNumber, payload.X, correctedY, payload.Width);
                doc.Add(image);
                doc.Close(); // 🟢 append mode finalized
            }

            return outputStream.ToArray();
        }


        public async Task<IActionResult> OnGetUserSignature()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            if (string.IsNullOrWhiteSpace(user.SignaturePath))
                return new JsonResult(new { success = false, message = "❌ Keine Signatur gefunden." });

            try
            {
                string signaturePath = user.SignaturePath.Replace("\\", "/").Trim();

                _logger.LogInformation("🖋️ Lade Benutzersignatur von WebDAV: {Path}", signaturePath);

                // 🔍 Vérifier si le fichier existe sur WebDAV
                bool exists = await _WebDav.FileExistsAsync(signaturePath);
                if (!exists)
                {
                    _logger.LogWarning("❌ Signatur nicht gefunden auf WebDAV: {Path}", signaturePath);
                    return new JsonResult(new { success = false, message = "❌ Signatur-Datei nicht gefunden." });
                }

                // 📥 Télécharger le fichier en mémoire
                using var ms = new MemoryStream();
                using var stream = await _WebDav.DownloadStreamStableAsync(signaturePath);

                if (stream == null)
                {
                    _logger.LogWarning("❌ DownloadStreamAsync gab null zurück: {Path}", signaturePath);
                    return new JsonResult(new { success = false, message = "❌ Fehler beim Laden der Signatur." });
                }

                await stream.CopyToAsync(ms);
                ms.Position = 0;

                if (ms.Length == 0)
                {
                    _logger.LogWarning("⚠️ Signatur-Datei leer: {Path}", signaturePath);
                    return new JsonResult(new { success = false, message = "❌ Signatur-Datei leer oder beschädigt." });
                }

                // 🔄 En Base64 encodieren
                string base64 = Convert.ToBase64String(ms.ToArray());
                string mimeType = signaturePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" :
                                  signaturePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" :
                                  signaturePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" :
                                  "application/octet-stream";

                _logger.LogInformation("✅ Signatur erfolgreich geladen ({Length} Bytes)", ms.Length);

                return new JsonResult(new
                {
                    success = true,
                    base64 = $"data:{mimeType};base64,{base64}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Laden der Benutzersignatur.");
                return new JsonResult(new { success = false, message = $"Fehler beim Laden der Signatur: {ex.Message}" });
            }
        }



        [HttpPost]
        public async Task<IActionResult> OnPostSaveUserSignature([FromBody] SignatureSaveRequest payload)
        {
            if (payload == null || string.IsNullOrWhiteSpace(payload.ImageBase64))
                return BadRequest(new { success = false, message = "❌ Ungültige Signatur-Daten." });

            var user = await _userManager.GetUserAsync(User);
            if (user == null)
                return Unauthorized();

            try
            {
                _logger.LogInformation("🖋️ Speichere neue Benutzersignatur für {UserId}", user.Id);

                // =====================================================
                // 🔧 Base64 → Bytes umwandeln
                // =====================================================
                var base64Data = payload.ImageBase64.Contains(",")
                    ? payload.ImageBase64.Split(',')[1]
                    : payload.ImageBase64;

                byte[] imageBytes = Convert.FromBase64String(base64Data);
                var firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "default";

                string signaturePath = $"dokumente/{firma}/signatures/{user.Id}.png";

                // =====================================================
                // 🧹 Alte Signatur löschen (falls vorhanden)
                // =====================================================
                if (!string.IsNullOrWhiteSpace(user.SignaturePath))
                {
                    bool exists = await _WebDav.FileExistsAsync(user.SignaturePath);
                    if (exists)
                    {
                        _logger.LogInformation("🧹 Lösche alte Signatur: {Path}", user.SignaturePath);
                        await _WebDav.DeleteFileAsync(user.SignaturePath);
                    }
                }

                // =====================================================
                // 📤 Neue Signatur hochladen
                // =====================================================
                using var ms = new MemoryStream(imageBytes);
                await _WebDav.EnsureFolderTreeExistsAsync($"dokumente/{firma}/signatures");
                await _WebDav.UploadStreamAsync(ms, signaturePath, "image/png");

                // =====================================================
                // 💾 Benutzer aktualisieren
                // =====================================================
                user.SignaturePath = signaturePath;
                var result = await _userManager.UpdateAsync(user);

                if (!result.Succeeded)
                {
                    _logger.LogWarning("❌ Benutzer konnte nicht aktualisiert werden: {UserId}", user.Id);
                    return StatusCode(500, new { success = false, message = "❌ Benutzer konnte nicht aktualisiert werden." });
                }

                _logger.LogInformation("✅ Signatur erfolgreich gespeichert: {Path}", signaturePath);

                return new JsonResult(new
                {
                    success = true,
                    message = "✔️ Neue Signatur erfolgreich gespeichert.",
                    path = signaturePath
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Speichern der Benutzersignatur für {UserId}", user?.Id);
                return StatusCode(500, new { success = false, message = ex.InnerException?.Message ?? ex.Message });
            }
        }


        [HttpPost]
        public async Task<IActionResult> OnPostSaveImage([FromBody] SaveImageRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ImageBase64))
                return BadRequest(new { success = false, message = "❌ Ungültige Bilddaten" });

            try
            {
                var user = await _userManager.GetUserAsync(User);
                if (user == null)
                    return Unauthorized();

                _logger.LogInformation("🖼️ [SaveImage] Speichere bearbeitetes Bild für Dokument {Id}", request.FileId);

                // ==========================================================
                // 1️⃣ Dokument oder Version laden
                // ==========================================================
                var dokument = await _dbContext.Dokumente
                    .Include(d => d.Abteilung)
                    .FirstOrDefaultAsync(d => d.Id == request.FileId);

                if (dokument == null)
                {
                    var version = await _dbContext.DokumentVersionen
                        .Include(v => v.Abteilung)
                        .FirstOrDefaultAsync(v => v.Id == request.FileId);

                    if (version == null)
                        return NotFound("❌ Dokument nicht gefunden.");

                    var original = version.OriginalId.HasValue
                        ? await _dbContext.Dokumente
                            .AsNoTracking()
                            .FirstOrDefaultAsync(d => d.Id == version.OriginalId.Value)
                        : null;

                    dokument = new Dokumente
                    {
                        Id = version.Id,
                        OriginalId = version.OriginalId,
                        Dateiname = version.Dateiname,
                        ObjectPath = version.ObjectPath,
                        Dateipfad = version.Dateipfad,
                        AbteilungId = version.AbteilungId,
                        Abteilung = version.Abteilung,
                        IsVersion = true,
                        EstSigne = version.EstSigne,
                        IsChunked = version.IsChunked,
                        Kategorie = original?.Kategorie?.Trim().ToLowerInvariant() ?? "misc"
                    };

                    _logger.LogInformation("📂 Kategorie übernommen von Original: {Kategorie}", dokument.Kategorie);
                }

                // ==========================================================
                // 2️⃣ Base64 → Bytes umwandeln
                // ==========================================================
                string base64Data = request.ImageBase64.Contains(",")
                    ? request.ImageBase64.Split(',')[1]
                    : request.ImageBase64;

                byte[] imageBytes = Convert.FromBase64String(base64Data);

                // ==========================================================
                // 3️⃣ Speicherpfad vorbereiten
                // ==========================================================
                string firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";

                string abteilungName;
                if (dokument.Abteilung != null && !string.IsNullOrWhiteSpace(dokument.Abteilung.Name))
                {
                    abteilungName = dokument.Abteilung.Name.Trim().ToLowerInvariant();
                }
                else if (dokument.AbteilungId != null)
                {
                    var abt = await _dbContext.Abteilungen
                        .AsNoTracking()
                        .FirstOrDefaultAsync(a => a.Id == dokument.AbteilungId);
                    abteilungName = abt?.Name?.Trim().ToLowerInvariant() ?? "allgemein";
                }
                else
                {
                    abteilungName = "allgemein";
                }

                // Kategorie sicherstellen
                string kategorie = dokument.Kategorie?.Trim().ToLowerInvariant() ?? "misc";
                if (kategorie == "versionen" && dokument.OriginalId != null)
                {
                    var originalDoc = await _dbContext.Dokumente
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == dokument.OriginalId);

                    if (originalDoc != null && !string.IsNullOrWhiteSpace(originalDoc.Kategorie))
                    {
                        kategorie = originalDoc.Kategorie.Trim().ToLowerInvariant();
                        _logger.LogInformation("📂 Kategorie korrigiert von 'versionen' → {Kategorie}", kategorie);
                    }
                    else
                    {
                        kategorie = "misc";
                    }
                }

                kategorie = kategorie.Replace("/versionen", "").Trim('/');

                // ==========================================================
                // 4️⃣ Versionsnummer ermitteln
                // ==========================================================
                int versionCount = await _dbContext.DokumentVersionen
                    .CountAsync(v => v.OriginalId == (dokument.OriginalId ?? dokument.Id));

                string ext = System.IO.Path.GetExtension(dokument.Dateiname);
                string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(dokument.Dateiname);
                string versionFileName = $"{nameNoExt}_V{versionCount + 1}{ext}";

                string versionFolder = $"dokumente/{firma}/{abteilungName}/{kategorie}/versionen";
                await _WebDav.EnsureFolderTreeExistsAsync(versionFolder);

                string versionPath = $"{versionFolder}/{versionFileName}";

                _logger.LogInformation("📂 Bearbeitetes Bild wird gespeichert unter: {Path}", versionPath);

                // ==========================================================
                // 5️⃣ Upload bearbeitetes Bild
                // ==========================================================
                using var imageStream = new MemoryStream(imageBytes);
                string mimeType = ext.ToLower() == ".png" ? "image/png" : "image/jpeg";
                await _WebDav.UploadStreamAsync(imageStream, versionPath, mimeType);

                // ==========================================================
                // 6️⃣ Neue Version in DB speichern
                // ==========================================================
                var newVersion = new DokumentVersionen
                {
                    DokumentId = dokument.Id,
                    ObjectPath = versionPath,
                    Dateiname = versionFileName,
                    AbteilungId = dokument.AbteilungId,
                    ApplicationUserId = user.Id,
                    EstSigne = false,
                    IsVersion = true,
                    HochgeladenAm = DateTime.UtcNow,
                    VersionsLabel = $"v{versionCount + 1}",
                    OriginalId = dokument.OriginalId ?? dokument.Id,
                    Kategorie = kategorie
                };

                _dbContext.DokumentVersionen.Add(newVersion);
                await _dbContext.SaveChangesAsync();

                // ==========================================================
                // 7️⃣ Metadaten clonen
                // ==========================================================
                await CloneAndAttachMetadataAsync(dokument, newVersion, new SaveRequest
                {
                    Metadata = request.Metadata ?? new OriginalMetadataDto
                    {
                        Beschreibung = $"Bearbeitete Version erstellt am {DateTime.UtcNow:dd.MM.yyyy HH:mm}",
                        PdfAutor = user.UserName,
                        PdfBetreff = "Bearbeitetes Bild",
                        OCRText = "Bild bearbeitet"
                    }
                });

                _logger.LogInformation("✅ Bearbeitetes Bild erfolgreich gespeichert: {Path}", versionPath);

                return new JsonResult(new
                {
                    success = true,
                    message = $"✔️ Bild als neue Version gespeichert ({newVersion.VersionsLabel}).",
                    path = versionPath
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Speichern des Bildes.");
                return StatusCode(500, new { success = false, message = ex.InnerException?.Message ?? ex.Message });
            }
        }


        [HttpPost]
        public async Task<IActionResult> OnPostSaveWithName([FromBody] SaveRequest request)
        {
            _logger.LogInformation("═══════════════════════════════════════════════════════");
            _logger.LogInformation("📦 [SmartChunkSave] → Methode utilisée : {Method}",
                FromTask ? "SaveOverwriteAsync" : "SaveChunkedVersionAsync");
            _logger.LogInformation("═══════════════════════════════════════════════════════");

            if (request == null || string.IsNullOrWhiteSpace(request.FileName))
                return new JsonResult(new { success = false, message = "❌ Ungültige Daten" });

            // =====================================================
            // 🔍 1️⃣ Dokument oder Version finden
            // =====================================================
            var dokument = await _dbContext.Dokumente
                .Include(d => d.Abteilung)
                .FirstOrDefaultAsync(d => d.Id == request.FileId);

            if (dokument == null)
            {
                // 🧩 Kein Dokument → versuche in DokumentVersionen
                var version = await _dbContext.DokumentVersionen
                    .Include(v => v.Abteilung)
                    .FirstOrDefaultAsync(v => v.Id == request.FileId);

                if (version != null)
                {
                    _logger.LogInformation("🧬 [SmartChunkSave] Version erkannt → VersionId={VersionId}, OriginalId={OriginalId}",
                        version.Id, version.OriginalId);

                    // 🧠 Original-Dokument laden
                    dokument = await _dbContext.Dokumente
                        .Include(d => d.Abteilung)
                        .FirstOrDefaultAsync(d => d.Id == version.OriginalId);

                    if (dokument == null)
                    {
                        _logger.LogWarning("⚠️ [SmartChunkSave] Original-Dokument {OriginalId} nicht gefunden.", version.OriginalId);
                        return new JsonResult(new { success = false, message = "❌ Original-Dokument nicht gefunden." });
                    }

                    // 🚫 Nicht überschreiben des echten ObjectPath
                    if (string.IsNullOrWhiteSpace(dokument.ObjectPath))
                    {
                        dokument.ObjectPath = version.ObjectPath;
                        _logger.LogInformation("📎 [SmartChunkSave] Original.ObjectPath initialisiert aus Version.");
                    }

                    dokument.IsVersion = true;
                    dokument.OriginalId = version.OriginalId;

                    // ✅ Sicherstellen, dass Kategorie vom Original kommt
                    if (string.IsNullOrWhiteSpace(dokument.Kategorie) || dokument.Kategorie == "versionen")
                    {
                        dokument.Kategorie = dokument.Kategorie?.Trim().ToLowerInvariant();
                        var original = await _dbContext.Dokumente
                            .AsNoTracking()
                            .FirstOrDefaultAsync(d => d.Id == version.OriginalId);

                        dokument.Kategorie = original?.Kategorie?.Trim().ToLowerInvariant() ?? "misc";
                        _logger.LogInformation("📂 [SmartChunkSave] Kategorie vom Original übernommen: {Kategorie}", dokument.Kategorie);
                    }
                }
                else
                {
                    _logger.LogWarning("⚠️ [SmartChunkSave] Dokument oder Version mit Id {FileId} nicht gefunden.", request.FileId);
                    return new JsonResult(new { success = false, message = "❌ Dokument nicht gefunden." });
                }
            }

            // =====================================================
            // ⚙️ 2️⃣ Speichervorgang starten
            // =====================================================
            _logger.LogInformation("⚙️ [SmartChunkSave] Starte Speichervorgang für Dokument/Version {Id}", dokument.Id);

            // 🧠 Nettoyage Kategorie (supprime toute trace de '/versionen')
            if (!string.IsNullOrWhiteSpace(dokument.Kategorie))
            {
                dokument.Kategorie = dokument.Kategorie
                    .Replace("/versionen", "")
                    .Trim('/')
                    .ToLowerInvariant();
            }
            else
            {
                dokument.Kategorie = "misc";
            }

            // 🔁 3️⃣ Wenn der Aufruf von einer Aufgabe stammt → direkt überschreiben
            if (FromTask)
            {
                _logger.LogInformation("🧾 Überschreibmodus (FromTask) aktiviert – Dokument wird direkt ersetzt.");
                return await SaveOverwriteAsync(request);
            }

            // =====================================================
            // 🧩 4️⃣ Standard-Speicherlogik (Versionierung / Chunked)
            // =====================================================
            _logger.LogInformation("🧩 [SmartChunkSave] Verwende SaveChunkedVersionAsync() für Versionierung.");

            try
            {
                return await SaveChunkedVersionAsync(request, dokument);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [SmartChunkSave] Fehler beim Speichern der Version.");
                return new JsonResult(new { success = false, message = ex.InnerException?.Message ?? ex.Message });
            }
        }
        private async Task<JsonResult> SaveChunkedVersionAsync(SaveRequest request, Dokumente dokument)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync();
            _logger.LogInformation("🧩 [ChunkSystem] Starte SaveChunkedVersionAsync für Dokument {Id}", dokument.Id);
            _logger.LogDebug("🔍 IsVersion={IsVersion}, IsChunked={IsChunked}, OriginalId={OriginalId}", dokument.IsVersion, dokument.IsChunked, dokument.OriginalId);


            try
            {
                _logger.LogInformation("🧩 [SmartChunkSave] Starte Versionierung für Dokument/Version {Id}", dokument.Id);

                // =====================================================
                // 🧠 1️⃣ Prüfen, ob Dokument oder Version
                // =====================================================
                bool isVersion = false;
                string originalPath = null;

                var version = await _dbContext.DokumentVersionen
                    .Include(v => v.Abteilung)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == dokument.Id);

                if (version != null)
                {
                    isVersion = true;
                    _logger.LogInformation("🔄 Bearbeite bestehende Version → VersionId={VersionId}, OriginalId={OriginalId}", version.Id, version.OriginalId);

                    var lastVersion = await _dbContext.DokumentVersionen
                        .AsNoTracking()
                        .Where(v => v.OriginalId == version.OriginalId)
                        .OrderByDescending(v => v.HochgeladenAm)
                        .FirstOrDefaultAsync();

                    if (lastVersion != null)
                    {
                        _logger.LogInformation("📄 Verwende letzte Version als Basis: {VersionLabel}", lastVersion.VersionsLabel);
                        originalPath = lastVersion.ObjectPath ?? lastVersion.Dateipfad;
                    }
                    else
                    {
                        _logger.LogInformation("⚠️ Keine vorherige Version gefunden – benutze aktuelle Version als Basis.");
                        originalPath = version.ObjectPath ?? version.Dateipfad;
                    }

                    var originalDoc = await _dbContext.Dokumente
                        .AsNoTracking()
                        .Include(d => d.Abteilung)
                        .FirstOrDefaultAsync(d => d.Id == version.OriginalId);

                    if (originalDoc != null)
                    {
                        if (string.IsNullOrWhiteSpace(dokument.Kategorie))
                            dokument.Kategorie = originalDoc.Kategorie;

                        dokument.AbteilungId = originalDoc.AbteilungId;
                        dokument.Abteilung = originalDoc.Abteilung;
                    }
                }
                else
                {
                    var lastVersion = await _dbContext.DokumentVersionen
                        .AsNoTracking()
                        .Where(v => v.OriginalId == dokument.Id)
                        .OrderByDescending(v => v.HochgeladenAm)
                        .FirstOrDefaultAsync();

                    if (lastVersion != null)
                    {
                        _logger.LogInformation("📄 Verwende letzte Version als Basis (Original hat Versionen): {VersionLabel}", lastVersion.VersionsLabel);
                        originalPath = lastVersion.ObjectPath ?? lastVersion.Dateipfad;
                        isVersion = true;
                    }
                    else
                    {
                        _logger.LogInformation("🧾 Dokument hat keine Versionen – benutze Originaldatei.");
                        originalPath = dokument.ObjectPath ?? dokument.Dateipfad;
                    }
                }

                // =====================================================
                // 🔧 2️⃣ Kategorie sicherstellen
                // =====================================================
                if (dokument.IsVersion && dokument.OriginalId != null)
                {
                    var originalDoc = await _dbContext.Dokumente
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == dokument.OriginalId);

                    dokument.Kategorie = originalDoc?.Kategorie?.Trim().ToLowerInvariant() ?? "misc";
                }

                dokument.Kategorie ??= "misc";
                dokument.Kategorie = dokument.Kategorie.Replace("/versionen", "").Trim('/').ToLowerInvariant();

                // =====================================================
                // 📁 3️⃣ PDF-Quelle laden / rekonstruieren
                // =====================================================
                if (string.IsNullOrWhiteSpace(originalPath))
                {
                    _logger.LogWarning("⚠️ Kein gültiger Pfad für Original/Version gefunden.");
                    return new JsonResult(new { success = false, message = "❌ Kein gültiger PDF-Pfad gefunden." });
                }

                _logger.LogInformation("📂 Lade PDF von WebDAV: {Pfad}", originalPath);

                Stream? originalStream = null;
                bool isChunkedDoc = dokument.IsChunked || (originalPath?.Contains("/chunks/", StringComparison.OrdinalIgnoreCase) == true);

                if (isChunkedDoc)
                {
                    _logger.LogInformation("🧩 Dokument ist chunked – starte Rekonstruktion...");
                    Guid targetId = dokument.OriginalId ?? dokument.Id;
                    var reconstructedPath = await _chunkService.ReconstructFileFromWebDavAsync(targetId);

                    if (!string.IsNullOrWhiteSpace(reconstructedPath) && System.IO.File.Exists(reconstructedPath))
                    {
                        _logger.LogInformation("✅ Rekonstruktion erfolgreich: {Path}", reconstructedPath);
                        originalStream = System.IO.File.OpenRead(reconstructedPath);
                    }
                    else
                    {
                        _logger.LogError("❌ Rekonstruktion fehlgeschlagen ({Id})", dokument.Id);
                        return new JsonResult(new { success = false, message = "❌ Rekonstruktion fehlgeschlagen – keine PDF gefunden." });
                    }
                }
                else
                {
                    originalStream = await _WebDav.DownloadStreamStableAsync(originalPath);
                }

                if (originalStream == null)
                {
                    _logger.LogError("❌ Originaldatei nicht gefunden ({Pfad})", originalPath);
                    return new JsonResult(new { success = false, message = "❌ PDF konnte nicht geladen werden." });
                }

                using var ms = new MemoryStream();
                await originalStream.CopyToAsync(ms);
                ms.Position = 0;


                // =====================================================
                // 🧩 PDF-Handling (Normalisierung oder Append Mode)
                // =====================================================
                // ✅ AppendMode si on ajoute du contenu sans réécrire (texte, images, highlights, signatures)
                bool isAppendMode =
                    (request.Texts?.Any() == true) ||
                    (request.Highlights?.Any() == true) ||
                    (request.Images?.Any() == true) ||
                    (request.Signatures?.Any() == true);


                byte[] normalizedBytes; // ✅ Déclaration anticipée

                if (isAppendMode)
                {
                    _logger.LogInformation("🟡 [AppendMode] Erkannt – Überspringe Normalisierung, um differenziellen Hash zu erhalten.");
                    ms.Position = 0;
                    normalizedBytes = ms.ToArray(); // ✅ On garde le PDF tel quel
                }
                else
                {
                    _logger.LogInformation("🧠 Normalisiere PDF – entferne variable Metadaten für stabilen Hash-Vergleich.");

                    normalizedBytes = NormalizePdf(ms.ToArray());
                    ms.SetLength(0);
                    await ms.WriteAsync(normalizedBytes);
                    ms.Position = 0;

                    _logger.LogInformation("✅ PDF-Normalisierung abgeschlossen – stabilisierte Größe: {Size} Bytes", normalizedBytes.Length);
                }

                // =====================================================
                // 🧠 3️⃣ Smart-Rebuild-Check – détecte si le contenu a changé
                // =====================================================
                var baseId = dokument.OriginalId ?? dokument.Id;
                var previousChunks = await _dbContext.DokumentChunks
                    .Where(c => c.DokumentId == baseId)
                    .OrderBy(c => c.Index)
                    .ToListAsync();

                string currentHash = ComputeSHA256(normalizedBytes);
                string? lastHash = previousChunks.LastOrDefault()?.Hash;

                if (previousChunks.Any() && lastHash == currentHash && !(request.Signatures?.Any() == true))
                {
                    _logger.LogInformation("🧩 Keine Änderungen erkannt – überspringe neuen Chunk-Upload.");
                    return new JsonResult(new
                    {
                        success = true,
                        message = "✔️ Keine Änderungen am Dokument erkannt. Keine neue Version notwendig.",
                        skipped = true
                    });
                }


                // =====================================================
                // ✍️ 4️⃣ Änderungen anwenden (Texte, Markierungen, Bilder, Signaturen)
                // =====================================================

                bool hasChanges =
    (request.Texts?.Any() == true) ||
    (request.Highlights?.Any() == true) ||
    (request.Images?.Any() == true) ||
    (request.Signatures?.Any() == true);

                MemoryStream outputStream = new MemoryStream();

                if (!hasChanges)
                {
                    _logger.LogInformation("🔍 Keine Änderungen erkannt – benutze Original-PDF unverändert für DiffUpload.");

                    // Kein iText, direkte Kopie des Originalstreams
                    ms.Position = 0;
                    await ms.CopyToAsync(outputStream);
                    outputStream.Position = 0;
                }
                else
                {
                    outputStream = new MemoryStream();

                    // 🧠 Vérifie si les changements sont UNIQUEMENT des signatures / tampons
                    bool appendMode = (request.Signatures?.Any() == true &&
                                      !(request.Texts?.Any() == true || request.Highlights?.Any() == true || request.Images?.Any() == true));

                    if (!hasChanges)
                    {
                        _logger.LogInformation("🔍 Keine Änderungen erkannt – benutze Original-PDF unverändert für DiffUpload.");

                        ms.Position = 0;
                        await ms.CopyToAsync(outputStream);
                        outputStream.Position = 0;
                    }
                    else
                    {
                        outputStream = new MemoryStream();

                        bool hasImages = request.Images?.Any() == true;
                        bool hasSignatures = request.Signatures?.Any() == true;
                        bool hasText = request.Texts?.Any() == true;
                        bool hasHighlights = request.Highlights?.Any() == true;

                        isAppendMode = hasImages || hasSignatures || hasText || hasHighlights;

                        if (isAppendMode)
                        {
                            _logger.LogInformation("🟡 [AppendMode+All] AppendMode aktiviert → Texte, Highlights, Signaturen und Bilder werden inkrementell hinzugefügt.");

                            ms.Position = 0;
                            var reader = new PdfReader(ms);
                            reader.SetCloseStream(false);

                            var writerProps = new WriterProperties().SetFullCompressionMode(false);
                            var pdfWriter = new PdfWriter(outputStream, writerProps);
                            pdfWriter.SetCloseStream(false);

                            var stampingProps = new StampingProperties().UseAppendMode();

                            using (var pdfDoc = new PdfDocument(reader, pdfWriter, stampingProps))
                            {
                                var doc = new Document(pdfDoc);

                                // 🔹 Texte
                                if (hasText)
                                {
                                    foreach (var t in request.Texts)
                                    {
                                        try
                                        {
                                            var page = pdfDoc.GetPage(t.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = (float)(t.RelX * pageSize.GetWidth());
                                            float y = (float)((1 - (t.RelY + t.RelH)) * pageSize.GetHeight());
                                            float width = (float)(t.RelW * pageSize.GetWidth());

                                            string fontName = StandardFonts.HELVETICA;
                                            if (t.Bold && t.Italic) fontName = StandardFonts.HELVETICA_BOLDOBLIQUE;
                                            else if (t.Bold) fontName = StandardFonts.HELVETICA_BOLD;
                                            else if (t.Italic) fontName = StandardFonts.HELVETICA_OBLIQUE;

                                            var font = PdfFontFactory.CreateFont(fontName);
                                            var color = WebColors.GetRGBColor(t.Color ?? "#000000");

                                            string cleanText = WebUtility.HtmlDecode(t.Text ?? string.Empty);

                                            var textElement = new iText.Layout.Element.Text(cleanText)
                                                .SetFont(font)
                                                .SetFontSize((float)(t.FontSize > 0 ? t.FontSize : 12))
                                                .SetFontColor(color);

                                            var paragraph = new iText.Layout.Element.Paragraph(textElement)
                                                .SetFixedPosition(t.PageNumber, x, y, width);

                                            if (t.Underline)
                                                paragraph.SetUnderline();

                                            doc.Add(paragraph);
                                            _logger.LogInformation("🖊️ Text hinzugefügt auf Seite {Page} bei ({X},{Y})", t.PageNumber, x, y);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Hinzufügen von Text (AppendMode) auf Seite {Page}", t.PageNumber);
                                        }
                                    }
                                }

                                // 🔹 Highlights
                                if (hasHighlights)
                                {
                                    foreach (var h in request.Highlights)
                                    {
                                        try
                                        {
                                            var page = pdfDoc.GetPage(h.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = (float)(h.RelX * pageSize.GetWidth());
                                            float y = (float)((1 - (h.RelY + h.RelH)) * pageSize.GetHeight());
                                            float w = (float)(h.RelW * pageSize.GetWidth());
                                            float hgt = (float)(h.RelH * pageSize.GetHeight());

                                            var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), pdfDoc);
                                            var color = WebColors.GetRGBColor(h.Color ?? "#ffff00");

                                            canvas.SaveState();
                                            canvas.SetFillColor(color);
                                            canvas.Rectangle(x, y, w, hgt);
                                            canvas.Fill();
                                            canvas.RestoreState();

                                            _logger.LogInformation("🟨 Highlight hinzugefügt auf Seite {Page} bei ({X},{Y})", h.PageNumber, x, y);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Hinzufügen des Highlights (AppendMode) auf Seite {Page}", h.PageNumber);
                                        }
                                    }
                                }

                                // 🔹 Images (Stempel / Logos)
                                if (hasImages)
                                {
                                    foreach (var imgItem in request.Images)
                                    {
                                        try
                                        {
                                            if (string.IsNullOrEmpty(imgItem.ImageBase64)) continue;

                                            var base64Data = imgItem.ImageBase64.Contains(",")
                                                ? imgItem.ImageBase64.Split(',')[1]
                                                : imgItem.ImageBase64;

                                            byte[] imageBytes = Convert.FromBase64String(base64Data);
                                            var imgData = iText.IO.Image.ImageDataFactory.Create(imageBytes);
                                            var img = new iText.Layout.Element.Image(imgData);

                                            var page = pdfDoc.GetPage(imgItem.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = imgItem.RelX * pageSize.GetWidth();
                                            float y = (1 - (imgItem.RelY + imgItem.RelH)) * pageSize.GetHeight();
                                            float w = imgItem.RelW * pageSize.GetWidth();
                                            float h = imgItem.RelH * pageSize.GetHeight();

                                            // ✅ Conserver la taille exacte définie dans le front-end
                                            img.ScaleAbsolute(w, h);
                                            img.SetFixedPosition(imgItem.PageNumber, x, y);

                                            doc.Add(img);

                                            _logger.LogInformation("🖼️ [Stempel] Bild hinzugefügt auf Seite {Page} ({W}x{H}) an ({X},{Y})",
                                                imgItem.PageNumber, w, h, x, y);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Hinzufügen von Bild (AppendMode) auf Seite {Page}", imgItem.PageNumber);
                                        }
                                    }
                                }

                                // 🔹 Signaturen
                                if (hasSignatures)
                                {
                                    foreach (var sig in request.Signatures)
                                    {
                                        try
                                        {
                                            if (string.IsNullOrEmpty(sig.ImageBase64)) continue;

                                            var base64Data = sig.ImageBase64.Contains(",")
                                                ? sig.ImageBase64.Split(',')[1]
                                                : sig.ImageBase64;

                                            byte[] imageBytes = Convert.FromBase64String(base64Data);
                                            var imgData = iText.IO.Image.ImageDataFactory.Create(imageBytes);
                                            var img = new iText.Layout.Element.Image(imgData);

                                            var page = pdfDoc.GetPage(sig.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = sig.RelX * pageSize.GetWidth();
                                            float y = (1 - (sig.RelY + sig.RelH)) * pageSize.GetHeight();
                                            float w = sig.RelW * pageSize.GetWidth();
                                            float h = sig.RelH * pageSize.GetHeight();

                                            // ✅ Correction : taille et position exactes
                                            img.ScaleAbsolute(w, h);
                                            img.SetFixedPosition(sig.PageNumber, x, y);

                                            doc.Add(img);

                                            _logger.LogInformation("🖋️ [Signature] hinzugefügt auf Seite {Page} mit Größe {W}x{H}",
                                                sig.PageNumber, w, h);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Anwenden der Signatur (AppendMode) auf Seite {Page}", sig.PageNumber);
                                        }
                                    }
                                }


                                doc.Close();
                            }

                            outputStream.Flush();
                            outputStream.Position = 0;
                            _logger.LogInformation("✅ [AppendMode+All] PDF erfolgreich im Append-Mode aktualisiert (Texte, Highlights, Signaturen, Bilder).");
                        }

                        else
                        {
                            _logger.LogInformation("⚙️ [FullRewriteMode] Texte, Bilder oder Highlights erkannt → normaler PDF-Rewrite wird verwendet.");

                            var writerProps = new WriterProperties().SetFullCompressionMode(true);
                            var pdfWriter = new PdfWriter(outputStream, writerProps);
                            pdfWriter.SetCloseStream(false);

                            using (var reader = new PdfReader(ms))
                            using (var pdfDoc = new PdfDocument(reader, pdfWriter))
                            using (var doc = new Document(pdfDoc))
                            {
                                _logger.LogInformation("🧩 Beginne Anwendung der Änderungen...");

                                // 🔹 Texte
                                if (request.Texts?.Any() == true)
                                {
                                    foreach (var t in request.Texts)
                                    {
                                        try
                                        {
                                            var page = pdfDoc.GetPage(t.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = (float)(t.RelX * pageSize.GetWidth());
                                            float y = (float)((1 - (t.RelY + t.RelH)) * pageSize.GetHeight());
                                            float width = (float)(t.RelW * pageSize.GetWidth());

                                            string fontName = StandardFonts.HELVETICA;
                                            if (t.Bold && t.Italic) fontName = StandardFonts.HELVETICA_BOLDOBLIQUE;
                                            else if (t.Bold) fontName = StandardFonts.HELVETICA_BOLD;
                                            else if (t.Italic) fontName = StandardFonts.HELVETICA_OBLIQUE;

                                            var font = PdfFontFactory.CreateFont(fontName);
                                            var color = WebColors.GetRGBColor(t.Color ?? "#000000");

                                            string cleanText = WebUtility.HtmlDecode(t.Text ?? string.Empty);

                                            var textElement = new iText.Layout.Element.Text(cleanText)
                                                .SetFont(font)
                                                .SetFontSize((float)(t.FontSize > 0 ? t.FontSize : 12))
                                                .SetFontColor(color);

                                            var paragraph = new iText.Layout.Element.Paragraph(textElement)
                                                .SetFixedPosition(t.PageNumber, x, y, width);

                                            if (t.Underline)
                                                paragraph.SetUnderline();

                                            doc.Add(paragraph);
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Hinzufügen von Text auf Seite {Page}", t.PageNumber);
                                        }
                                    }
                                }

                                // 🔹 Highlights
                                if (request.Highlights?.Any() == true)
                                {
                                    foreach (var h in request.Highlights)
                                    {
                                        try
                                        {
                                            var page = pdfDoc.GetPage(h.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = (float)(h.RelX * pageSize.GetWidth());
                                            float y = (float)((1 - (h.RelY + h.RelH)) * pageSize.GetHeight());
                                            float w = (float)(h.RelW * pageSize.GetWidth());
                                            float hgt = (float)(h.RelH * pageSize.GetHeight());

                                            var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), pdfDoc);
                                            var color = WebColors.GetRGBColor(h.Color ?? "#ffff00");

                                            canvas.SaveState();
                                            canvas.SetFillColor(color);
                                            canvas.Rectangle(x, y, w, hgt);
                                            canvas.Fill();
                                            canvas.RestoreState();
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Anwenden des Highlights auf Seite {Page}", h.PageNumber);
                                        }
                                    }
                                }


                                // 🔹 Images
                                if (request.Images?.Any() == true)
                                {
                                    foreach (var img in request.Images)
                                    {
                                        try
                                        {
                                            if (string.IsNullOrEmpty(img.ImageBase64)) continue;

                                            var base64Data = img.ImageBase64.Contains(",")
                                                ? img.ImageBase64.Split(',')[1]
                                                : img.ImageBase64;

                                            byte[] imageBytes = Convert.FromBase64String(base64Data);
                                            var imgData = iText.IO.Image.ImageDataFactory.Create(imageBytes);

                                            var page = pdfDoc.GetPage(img.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = img.RelX * pageSize.GetWidth();
                                            float y = (1 - (img.RelY + img.RelH)) * pageSize.GetHeight();
                                            float w = img.RelW * pageSize.GetWidth();
                                            float h = img.RelH * pageSize.GetHeight();

                                            // 🧠 Utilisation directe du PdfCanvas (plus fiable dans le rewrite mode)
                                            var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(
                                                page.NewContentStreamAfter(),
                                                page.GetResources(),
                                                pdfDoc
                                            );

                                            canvas.AddImageFittedIntoRectangle(
                                                imgData,
                                                new iText.Kernel.Geom.Rectangle(x, y, w, h),
                                                false
                                            );

                                            canvas.Release();

                                            _logger.LogInformation("✅ Image ajoutée sur la page {Page} à ({X},{Y}) [{W}x{H}]",
                                                img.PageNumber, x, y, w, h);
                                            _logger.LogInformation($"🧩 Image page={img.PageNumber}, x={x}, y={y}, w={w}, h={h}");

                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Hinzufügen von Bild auf Seite {Page}", img.PageNumber);
                                        }
                                    }
                                }


                                // 🔹 Signaturen (si combinées avec texte/images)
                                if (request.Signatures?.Any() == true)
                                {
                                    foreach (var sig in request.Signatures)
                                    {
                                        try
                                        {
                                            if (string.IsNullOrEmpty(sig.ImageBase64)) continue;

                                            var base64Data = sig.ImageBase64.Contains(",") ? sig.ImageBase64.Split(',')[1] : sig.ImageBase64;
                                            var imageBytes = Convert.FromBase64String(base64Data);
                                            var imgData = iText.IO.Image.ImageDataFactory.Create(imageBytes);

                                            var page = pdfDoc.GetPage(sig.PageNumber);
                                            var pageSize = page.GetPageSize();

                                            float x = sig.RelX * pageSize.GetWidth();
                                            float y = (float)((1 - (sig.RelY + sig.RelH)) * pageSize.GetHeight());
                                            float w = sig.RelW * pageSize.GetWidth();
                                            float h = sig.RelH * pageSize.GetHeight();

                                            var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), pdfDoc);
                                            canvas.AddImageFittedIntoRectangle(imgData, new iText.Kernel.Geom.Rectangle(x, y, w, h), false);
                                            canvas.Release();
                                        }
                                        catch (Exception ex)
                                        {
                                            _logger.LogError(ex, "❌ Fehler beim Anwenden der Signatur auf Seite {Page}", sig.PageNumber);
                                        }
                                    }
                                }
                            }

                            outputStream.Flush();
                            outputStream.Position = 0;
                        }
                    }
                }

                if (outputStream.Length == 0)
                    return new JsonResult(new { success = false, message = "❌ Der erzeugte PDF-Stream ist leer." });

                // =====================================================
                // 🏢 5️⃣ Zielordner bestimmen
                // =====================================================
                var user = await _userManager.GetUserAsync(User);
                string firma = user?.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";
                string abteilungName = dokument.Abteilung?.Name?.Trim().ToLowerInvariant() ?? "allgemein";
                string kategorie = dokument.Kategorie?.Trim().ToLowerInvariant() ?? "misc";

                string versionFolder = $"dokumente/{firma}/{abteilungName}/{kategorie}/versionen";
                await _WebDav.EnsureFolderTreeExistsAsync(versionFolder);

                // =====================================================
                // 💾 6️⃣ Neue Version anlegen (vor Chunk-Upload)
                // =====================================================
                // =====================================================
                // 💾 6️⃣ Neue Version anlegen (vor Chunk-Upload)
                // =====================================================
                Guid versionId = Guid.NewGuid();

                int nextVersion = await _dbContext.DokumentVersionen
                    .CountAsync(v => v.OriginalId == (dokument.OriginalId ?? dokument.Id));

                // ✅ Clean Version Name: OriginalName_V{x}.ext
                string ext = System.IO.Path.GetExtension(request.FileName);
                string nameNoExt = System.IO.Path.GetFileNameWithoutExtension(request.FileName);
                string uniqueFileName = $"{nameNoExt}_V{nextVersion + 1}{ext}";

                string fullVersionPath = $"{versionFolder}/{uniqueFileName}";

                var newVersion = new DokumentVersionen
                {
                    Id = versionId,
                    DokumentId = dokument.Id,
                    ObjectPath = fullVersionPath,
                    Dateiname = request.FileName,
                    AbteilungId = dokument.AbteilungId,
                    Kategorie = dokument.Kategorie,
                    ApplicationUserId = user?.Id ?? dokument.ApplicationUserId,
                    HochgeladenAm = DateTime.UtcNow,
                    IsVersion = true,
                    IsChunked = false,
                    VersionsLabel = $"v{nextVersion + 1}",
                    OriginalId = dokument.OriginalId ?? dokument.Id
                };

                _dbContext.DokumentVersionen.Add(newVersion);
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("💾 [Versionierung] Neue Version erstellt: {Label} (ID={Id})", newVersion.VersionsLabel, newVersion.Id);
                // =====================================================
                // 🧾 6.1️⃣ Metadaten-Kopie für neue Version
                // =====================================================
                try
                {
                    _logger.LogInformation("🧾 Starte Erstellung der Metadatenkopie für Version {Label}", newVersion.VersionsLabel);

                    // 1️⃣ Hole die Metadaten des Originals (falls vorhanden)
                    Metadaten? originalMeta = null;

                    // Si le document a déjà un lien vers des métadaten
                    if (dokument.MetadatenId.HasValue)
                    {
                        originalMeta = await _dbContext.Metadaten
                            .AsNoTracking()
                            .FirstOrDefaultAsync(m => m.Id == dokument.MetadatenId.Value);
                    }

                    // 2️⃣ Fallback (aucune métadaten existante)
                    if (originalMeta == null)
                    {
                        _logger.LogWarning("⚠️ Keine Original-Metadaten gefunden, eine leere Vorlage wird erstellt.");
                        originalMeta = new Metadaten
                        {
                            Kategorie = dokument.Kategorie,
                            Titel = System.IO.Path.GetFileNameWithoutExtension(dokument.Dateiname),
                            Beschreibung = dokument.Beschreibung ?? "Automatisch generiert bei Versionierung"
                        };
                    }

                    // 3️⃣ Fusion logique : priorité à ce que l’utilisateur a modifié (request.Metadata)
                    Metadaten src;

                    if (request.Metadata != null)
                    {
                        // 🔹 Conversion manuelle du DTO vers l'entité Metadaten
                        src = new Metadaten
                        {
                            Kategorie = request.Metadata.Kategorie,
                            Beschreibung = request.Metadata.Beschreibung,
                            Titel = request.Metadata.Titel,
                            Rechnungsnummer = request.Metadata.Rechnungsnummer,
                            Kundennummer = request.Metadata.Kundennummer,
                            Rechnungsbetrag = request.Metadata.Rechnungsbetrag,
                            Nettobetrag = request.Metadata.Nettobetrag,
                            Steuerbetrag = request.Metadata.Steuerbetrag,
                            Gesamtpreis = request.Metadata.Gesamtpreis,
                            Rechnungsdatum = request.Metadata.Rechnungsdatum,
                            Lieferdatum = request.Metadata.Lieferdatum,
                            Faelligkeitsdatum = request.Metadata.Faelligkeitsdatum,
                            Zahlungsbedingungen = request.Metadata.Zahlungsbedingungen,
                            AnsprechPartner = request.Metadata.AnsprechPartner,
                            Email = request.Metadata.Email,
                            Telefon = request.Metadata.Telefon,
                            Adresse = request.Metadata.Adresse,
                            AbsenderAdresse = request.Metadata.AbsenderAdresse,
                            PdfAutor = request.Metadata.PdfAutor,
                            PdfBetreff = request.Metadata.PdfBetreff,
                            PdfSchluesselwoerter = request.Metadata.PdfSchluesselwoerter
                        };
                    }
                    else
                    {
                        src = new Metadaten(); // aucun champ modifié, donc on prendra tout de l'original
                    }


                    var newMeta = new Metadaten
                    {
                        // 🔹 Pas de DokumentId ici (évite le FK vers Dokumente)
                        Kategorie = string.IsNullOrWhiteSpace(src.Kategorie) ? originalMeta.Kategorie : src.Kategorie,
                        Beschreibung = string.IsNullOrWhiteSpace(src.Beschreibung) ? originalMeta.Beschreibung : src.Beschreibung,
                        Titel = string.IsNullOrWhiteSpace(src.Titel) ? originalMeta.Titel : src.Titel,
                        Rechnungsnummer = string.IsNullOrWhiteSpace(src.Rechnungsnummer) ? originalMeta.Rechnungsnummer : src.Rechnungsnummer,
                        Kundennummer = string.IsNullOrWhiteSpace(src.Kundennummer) ? originalMeta.Kundennummer : src.Kundennummer,
                        Rechnungsbetrag = src.Rechnungsbetrag == 0 ? originalMeta.Rechnungsbetrag : src.Rechnungsbetrag,
                        Nettobetrag = src.Nettobetrag == 0 ? originalMeta.Nettobetrag : src.Nettobetrag,
                        Steuerbetrag = src.Steuerbetrag == 0 ? originalMeta.Steuerbetrag : src.Steuerbetrag,
                        Gesamtpreis = src.Gesamtpreis == 0 ? originalMeta.Gesamtpreis : src.Gesamtpreis,
                        Rechnungsdatum = src.Rechnungsdatum ?? originalMeta.Rechnungsdatum,
                        Lieferdatum = src.Lieferdatum ?? originalMeta.Lieferdatum,
                        Faelligkeitsdatum = src.Faelligkeitsdatum ?? originalMeta.Faelligkeitsdatum,
                        Zahlungsbedingungen = string.IsNullOrWhiteSpace(src.Zahlungsbedingungen) ? originalMeta.Zahlungsbedingungen : src.Zahlungsbedingungen,
                        AnsprechPartner = string.IsNullOrWhiteSpace(src.AnsprechPartner) ? originalMeta.AnsprechPartner : src.AnsprechPartner,
                        Email = string.IsNullOrWhiteSpace(src.Email) ? originalMeta.Email : src.Email,
                        Telefon = string.IsNullOrWhiteSpace(src.Telefon) ? originalMeta.Telefon : src.Telefon,
                        Adresse = string.IsNullOrWhiteSpace(src.Adresse) ? originalMeta.Adresse : src.Adresse,
                        AbsenderAdresse = string.IsNullOrWhiteSpace(src.AbsenderAdresse) ? originalMeta.AbsenderAdresse : src.AbsenderAdresse,
                        PdfAutor = string.IsNullOrWhiteSpace(src.PdfAutor) ? originalMeta.PdfAutor : src.PdfAutor,
                        PdfBetreff = string.IsNullOrWhiteSpace(src.PdfBetreff) ? originalMeta.PdfBetreff : src.PdfBetreff,
                        PdfSchluesselwoerter = string.IsNullOrWhiteSpace(src.PdfSchluesselwoerter) ? originalMeta.PdfSchluesselwoerter : src.PdfSchluesselwoerter
                    };

                    // 4️⃣ Enregistrer la nouvelle entrée
                    await _dbContext.Metadaten.AddAsync(newMeta);
                    await _dbContext.SaveChangesAsync();

                    // 5️⃣ Associer la version à ses métadaten
                    // ✅ Récupère la version depuis la base pour garantir le tracking EF
                    var versionToUpdate = await _dbContext.DokumentVersionen
                        .FirstOrDefaultAsync(v => v.Id == newVersion.Id);

                    if (versionToUpdate != null)
                    {
                        versionToUpdate.MetadatenId = newMeta.Id;
                        versionToUpdate.MetadataJson = System.Text.Json.JsonSerializer.Serialize(newMeta);

                        await _dbContext.SaveChangesAsync();
                        _logger.LogInformation("✅ MetadatenId für Version {Label} korrekt verknüpft: {MetaId}",
                            newVersion.VersionsLabel, newMeta.Id);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Version {Id} nicht gefunden beim Versuch, Metadaten zu verknüpfen.", newVersion.Id);
                    }


                    _logger.LogInformation("✅ Neue Metadaten erstellt und Version {Label} zugeordnet: {MetaId}", newVersion.VersionsLabel, newMeta.Id);
                }
                catch (Exception exMeta)
                {
                    _logger.LogError(exMeta, "❌ Fehler beim Erstellen oder Kopieren der Metadaten für Version {Label}", newVersion.VersionsLabel);
                }

                // =====================================================
                // 📤 7️⃣ Optimierter Upload (differentielle Chunks)
                // =====================================================
                _logger.LogInformation("📦 Starte differenziellen Vergleich (DiffUpload) für {VersionLabel}", newVersion.VersionsLabel);
                _logger.LogDebug("📄 PDF-Größe für Vergleich: {Size} Bytes", outputStream.Length);

                // 🧩 7.1️⃣ Vergleichsbasis korrekt bestimmen


                if (dokument.IsVersion)
                {
                    // 🔹 Si le document actuel est déjà une version,
                    // on cherche la dernière version existante (sauf celle qu’on vient de créer)
                    var lastVersion = await _dbContext.DokumentVersionen
                        .AsNoTracking()
                        .Where(v => v.OriginalId == dokument.OriginalId && v.Id != newVersion.Id)
                        .OrderByDescending(v => v.HochgeladenAm)
                        .FirstOrDefaultAsync();

                    baseId = lastVersion?.Id ?? dokument.OriginalId ?? dokument.Id;
                }
                else
                {
                    // 🔹 Si c’est l’original, on regarde s’il a déjà des versions
                    var lastVersion = await _dbContext.DokumentVersionen
                        .AsNoTracking()
                        .Where(v => v.OriginalId == dokument.Id && v.Id != newVersion.Id)
                        .OrderByDescending(v => v.HochgeladenAm)
                        .FirstOrDefaultAsync();

                    baseId = lastVersion?.Id ?? dokument.Id;
                }

                _logger.LogInformation("🔁 [ChunkDiff] Vergleichsbasis bestimmt: {BaseId} (Letzte Version oder Original)", baseId);

                // 🧠 Important : le manifest doit correspondre à cette baseId
                string manifestPath = $"dokumente/{firma}/{abteilungName}/{kategorie}/versionen/chunks/{baseId}_manifest.json";
                _logger.LogInformation("🧩 [ChunkDiff] Erwartetes Manifest: {ManifestPath}", manifestPath);

                // ⚡ DiffUpload starten
                _logger.LogInformation("📦 Starte differenziellen Vergleich über CompareAndUploadNewVersionChunksAsync()");
                var chunks = await _chunkService.CompareAndUploadNewVersionChunksAsync(
                    baseId,            // ✅ toujours la base réelle (Original ou dernière version)
                    outputStream,      // nouveau fichier PDF stream
                    newVersion.Id,     // nouvelle version
                    firma,
                    abteilungName,
                    kategorie
                );

                // 🧾 Résumé des chunks
                int changedCount = chunks.Count(c => c.IsChanged);
                int reusedCount = chunks.Count(c => !c.IsChanged);

                _logger.LogInformation("📊 [DiffUpload] Vergleich abgeschlossen: {Count} Chunks verarbeitet.", chunks.Count);
                _logger.LogInformation("📊 [DiffUpload Zusammenfassung] Version {VersionLabel}: {Changed} geändert, {Reused} wiederverwendet, {Total} gesamt.",
                    newVersion.VersionsLabel, changedCount, reusedCount, chunks.Count);
                _logger.LogInformation("✅ Differenzieller Upload abgeschlossen für Version {VersionId}", newVersion.Id);

                // Enregistrer les infos des chunks dans la DB
                await _dbContext.SaveChangesAsync();


                // =====================================================
                // 🧩 7️⃣ Upload du PDF reconstruit pour visualisation rapide
                // =====================================================
                try
                {
                    outputStream.Position = 0;
                    await _WebDav.UploadStreamAsync(outputStream, fullVersionPath, "application/pdf");
                    _logger.LogInformation("📄 Full PDF version uploaded successfully for preview: {Path}", fullVersionPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Failed to upload full PDF version — only chunks were stored.");
                }


                // =====================================================
                // 🧹 9️⃣ Cleanup der temporären Rekonstruktionsdateien
                // =====================================================
                try
                {
                    if (isChunkedDoc)
                    {
                        var tempReconstructed = await _chunkService.ReconstructFileFromWebDavAsync(dokument.OriginalId ?? dokument.Id);
                        if (!string.IsNullOrWhiteSpace(tempReconstructed) && System.IO.File.Exists(tempReconstructed))
                        {
                            System.IO.File.Delete(tempReconstructed);
                            _logger.LogInformation("🧹 Temporäre Rekonstruktionsdatei gelöscht: {Path}", tempReconstructed);
                        }
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "⚠️ Fehler beim Löschen der temporären Datei nach Chunk-Verarbeitung.");
                }


                await transaction.CommitAsync();

                _logger.LogInformation("✅ Version {Label} gespeichert.", newVersion.VersionsLabel);
                // ======================================================
                // 📊 Professioneller Abschlussbericht – ChunkDiff Zusammenfassung
                // ======================================================
                if (chunks != null && chunks.Count > 0)
                {
                    int changed = chunks.Count(c => c.IsChanged);
                    int reused = chunks.Count(c => !c.IsChanged);
                    long uploadedBytes = chunks.Where(c => c.IsChanged).Sum(c => c.Size);
                    long totalBytes = chunks.Sum(c => c.Size);
                    long savedBytes = totalBytes - uploadedBytes;
                    double efficiency = totalBytes == 0 ? 0 : (double)savedBytes / totalBytes * 100;

                    _logger.LogInformation("");
                    _logger.LogInformation("═══════════════════════════════════════════════════════");
                    _logger.LogInformation("📊 [ChunkDiff Zusammenfassung] Differenzieller Upload-Bericht für Version {VersionId}", newVersion.Id);
                    _logger.LogInformation("   • Gesamtanzahl Chunks : {Total}", chunks.Count);
                    _logger.LogInformation("   • Geändert             : {Changed}", changed);
                    _logger.LogInformation("   • Wiederverwendet      : {Reused}", reused);
                    _logger.LogInformation("   • Effektiv hochgeladen : {UploadedBytes:N0} Bytes", uploadedBytes);
                    _logger.LogInformation("   • Gesamtgröße          : {TotalBytes:N0} Bytes", totalBytes);
                    _logger.LogInformation("   • Einsparung           : {SavedBytes:N0} Bytes ({Efficiency:P2})", savedBytes, efficiency / 100);
                    _logger.LogInformation("═══════════════════════════════════════════════════════");

                    foreach (var c in chunks)
                    {
                        string state = c.IsChanged ? "🟥 GEÄNDERT" : "🟩 WIEDERVERWENDET";
                        _logger.LogInformation($"   {state} → Chunk #{c.Index:D4} | Größe = {c.Size:N0} Bytes");
                    }

                    _logger.LogInformation("═══════════════════════════════════════════════════════");
                    _logger.LogInformation("✅ Differenzieller Upload abgeschlossen für Version {VersionId}", newVersion.Id);
                }


                return new JsonResult(new
                {
                    success = true,
                    message = $"✔️ Neue Version {newVersion.VersionsLabel} erfolgreich gespeichert.",
                    path = fullVersionPath,
                    signed = newVersion.EstSigne,
                    chunkSummary = new
                    {
                        total = chunks.Count,
                        changed = chunks.Count(c => c.IsChanged),
                        reused = chunks.Count(c => !c.IsChanged),
                        uploaded = chunks.Where(c => c.IsChanged).Sum(c => c.Size),
                        totalSize = chunks.Sum(c => c.Size),
                        efficiency = (double)chunks.Where(c => !c.IsChanged).Sum(c => c.Size) / chunks.Sum(c => c.Size) * 100,
                        chunkSession = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")

                    }
                });
            }

            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogCritical(ex, "🚨 Schwerwiegender Fehler während SaveChunkedVersionAsync – Transaktion wird zurückgesetzt!");

                return new JsonResult(new { success = false, message = ex.InnerException?.Message ?? ex.Message });
            }

        }
        private async Task CloneAndAttachMetadataAsync(Dokumente original, DokumentVersionen newVersion, SaveRequest request)
        {
            try
            {
                // 🔹 Charger les métadaten originales
                var originalMeta = await _dbContext.Metadaten
                    .AsNoTracking()
                    .FirstOrDefaultAsync(m => m.DokumentId == original.Id);

                if (originalMeta == null)
                {
                    _logger.LogWarning("⚠️ Keine Original-Metadaten für Dokument {Id}", original.Id);
                    return;
                }

                // 🔹 Créer une copie indépendante
                var clonedMeta = new Metadaten
                {
                    //Id = Guid.NewGuid(),
                    DokumentId = newVersion.Id, // liaison à la VERSION
                    Kategorie = originalMeta.Kategorie,
                    Beschreibung = request.Metadata?.Beschreibung ?? originalMeta.Beschreibung,
                    Titel = request.Metadata?.Titel ?? originalMeta.Titel,
                    Kundennummer = originalMeta.Kundennummer,
                    Rechnungsnummer = originalMeta.Rechnungsnummer,
                    Rechnungsbetrag = originalMeta.Rechnungsbetrag,
                    Nettobetrag = originalMeta.Nettobetrag,
                    Steuerbetrag = originalMeta.Steuerbetrag,
                    Gesamtpreis = originalMeta.Gesamtpreis,
                    Rechnungsdatum = originalMeta.Rechnungsdatum,
                    Faelligkeitsdatum = originalMeta.Faelligkeitsdatum,
                    IBAN = originalMeta.IBAN,
                    BIC = originalMeta.BIC,
                    Email = originalMeta.Email,
                    Telefon = originalMeta.Telefon,
                    Adresse = originalMeta.Adresse,
                    PdfAutor = request.Metadata?.PdfAutor ?? originalMeta.PdfAutor,
                    PdfBetreff = request.Metadata?.PdfBetreff ?? originalMeta.PdfBetreff,
                    PdfSchluesselwoerter = request.Metadata?.PdfSchluesselwoerter ?? originalMeta.PdfSchluesselwoerter,
                    OCRText = request.Metadata?.OCRText ?? originalMeta.OCRText,
                    // facultatif : tu peux ajouter ici un champ "SigniertAm"
                    Zahlungsbedingungen = originalMeta.Zahlungsbedingungen,
                    Website = originalMeta.Website
                };

                _dbContext.Metadaten.Add(clonedMeta);
                await _dbContext.SaveChangesAsync();

                // 🔹 Lier la version à ces nouvelles métadaten
                newVersion.MetadatenId = clonedMeta.Id;
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("✅ Metadaten cloniert und an Version {Id} angehängt", newVersion.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Klonen der Metadaten für Version {Id}", newVersion.Id);
            }
        }
        private Metadaten BuildVersionMetadaten(Metadaten originalMeta, Metadaten requestMeta, Guid newVersionId)
        {
            return new Metadaten
            {
                DokumentId = newVersionId,
                Kategorie = requestMeta?.Kategorie ?? originalMeta?.Kategorie,
                Beschreibung = requestMeta?.Beschreibung ?? originalMeta?.Beschreibung,
                Titel = requestMeta?.Titel ?? originalMeta?.Titel,
                Rechnungsnummer = requestMeta?.Rechnungsnummer ?? originalMeta?.Rechnungsnummer,
                Kundennummer = requestMeta?.Kundennummer ?? originalMeta?.Kundennummer,
                Rechnungsbetrag = requestMeta?.Rechnungsbetrag ?? originalMeta?.Rechnungsbetrag,
                Nettobetrag = requestMeta?.Nettobetrag ?? originalMeta?.Nettobetrag,
                Steuerbetrag = requestMeta?.Steuerbetrag ?? originalMeta?.Steuerbetrag,
                Gesamtpreis = requestMeta?.Gesamtpreis ?? originalMeta?.Gesamtpreis,
                Rechnungsdatum = requestMeta?.Rechnungsdatum ?? originalMeta?.Rechnungsdatum,
                Faelligkeitsdatum = requestMeta?.Faelligkeitsdatum ?? originalMeta?.Faelligkeitsdatum,
                IBAN = requestMeta?.IBAN ?? originalMeta?.IBAN,
                BIC = requestMeta?.BIC ?? originalMeta?.BIC,
                Email = requestMeta?.Email ?? originalMeta?.Email,
                Telefon = requestMeta?.Telefon ?? originalMeta?.Telefon,
                Adresse = requestMeta?.Adresse ?? originalMeta?.Adresse,
                PdfAutor = requestMeta?.PdfAutor ?? originalMeta?.PdfAutor,
                PdfBetreff = requestMeta?.PdfBetreff ?? originalMeta?.PdfBetreff,
                PdfSchluesselwoerter = requestMeta?.PdfSchluesselwoerter ?? originalMeta?.PdfSchluesselwoerter
            };
        }


        private byte[] NormalizePdf(byte[] inputBytes)
        {
            using var input = new MemoryStream(inputBytes);
            using var reader = new iText.Kernel.Pdf.PdfReader(input);
            using var output = new MemoryStream();

            var writerProps = new iText.Kernel.Pdf.WriterProperties()
                .SetFullCompressionMode(true)
                .AddXmpMetadata();

            using var writer = new iText.Kernel.Pdf.PdfWriter(output, writerProps);
            using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader, writer);

            var info = pdfDoc.GetDocumentInfo();

            // 🔹 Remplace toutes les métadonnées variables
            info.SetMoreInfo(new Dictionary<string, string>
            {
                ["Producer"] = "StableChunkSystem",
                ["Creator"] = "DmsProjekt",
                ["ModDate"] = "D:20000101000000+00'00'",
                ["CreationDate"] = "D:20000101000000+00'00'",
                ["Author"] = "",
                ["Title"] = "",
                ["Subject"] = "",
                ["Keywords"] = ""
            });

            // 🔹 Supprime les IDs internes et le bloc info variable
            pdfDoc.GetTrailer().Remove(iText.Kernel.Pdf.PdfName.ID);
            pdfDoc.GetTrailer().Remove(iText.Kernel.Pdf.PdfName.Info);

            pdfDoc.Close();
            return output.ToArray();
        }


        private static string ComputeSHA256(byte[] data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }


        private static decimal? TryParseDecimal(object? value)
        {
            if (value == null) return null;
            return decimal.TryParse(value.ToString(), out var result) ? result : null;
        }

        private static DateTime? TryParseDate(object? value)
        {
            if (value == null) return null;
            return DateTime.TryParse(value.ToString(), out var result) ? result : null;
        }


        public async Task<IActionResult> OnGetFileNameAsync(Guid id)
        {

            var dokument = await _dbContext.Dokumente.FindAsync(id);
            if (dokument != null)
            {
                return new JsonResult(new
                {
                    success = true,
                    suggestedName = string.IsNullOrWhiteSpace(dokument.Dateiname)
                        ? $"Dokument_{DateTime.UtcNow:yyyy-MM-dd}.pdf"
                        : dokument.Dateiname
                });
            }


            var version = await _dbContext.DokumentVersionen.FindAsync(id);
            if (version != null)
            {
                return new JsonResult(new
                {
                    success = true,
                    suggestedName = string.IsNullOrWhiteSpace(version.Dateiname)
                        ? $"Version_{DateTime.UtcNow:yyyy-MM-dd}.pdf"
                        : version.Dateiname
                });
            }


            return new JsonResult(new { success = false, message = "❌ Dokument oder Version nicht gefunden." });
        }

        public async Task<IActionResult> OnGetPdfProxyAsync(Guid id)
        {
            _logger.LogInformation("📥 PDF-Proxy gestartet für ID={Id}", id);

            // =====================================================
            // 1️⃣ Dokument oder Version suchen
            // =====================================================
            var dokument = await _dbContext.Dokumente
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id);

            bool isVersion = false;

            if (dokument == null)
            {
                var version = await _dbContext.DokumentVersionen
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == id);

                if (version == null)
                {
                    _logger.LogWarning("❌ Weder Dokument noch Version mit ID={Id} gefunden", id);
                    return NotFound("❌ Dokument oder Version nicht gefunden.");
                }

                isVersion = true;

                var originalDoc = await _dbContext.Dokumente
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == version.OriginalId);

                dokument = new Dokumente
                {
                    Id = version.Id,
                    Dateiname = version.Dateiname,
                    ObjectPath = version.ObjectPath ?? version.Dateipfad,
                    Dateipfad = version.Dateipfad,
                    IsChunked = version.IsChunked,
                    EstSigne = version.EstSigne,
                    IsVersion = true,
                    Kategorie = originalDoc?.Kategorie ?? "versionen",
                    AbteilungId = originalDoc?.AbteilungId
                };

                if (version.OriginalId.HasValue)
                {
                    var original = await _dbContext.Dokumente
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == version.OriginalId.Value);

                    if (original != null && !string.IsNullOrWhiteSpace(original.Kategorie))
                    {
                        dokument.Kategorie = original.Kategorie.Trim().ToLowerInvariant();
                        _logger.LogInformation("📂 Kategorie aus Original übernommen: {Kategorie}", dokument.Kategorie);
                    }
                    else
                    {
                        dokument.Kategorie = "misc";
                        _logger.LogWarning("⚠️ Keine Kategorie im Original gefunden → Standardwert 'misc' verwendet.");
                    }
                }
                else
                {
                    dokument.Kategorie = "misc";
                    _logger.LogInformation("⚠️ Version ohne OriginalId – Standardkategorie 'misc' gesetzt.");
                }

                _logger.LogInformation("✅ Version erkannt ({VersionId}), benutze eigene Datei ({Path})",
                    version.Id, dokument.ObjectPath);
            }

            // =====================================================
            // 2️⃣ Pfad prüfen
            // =====================================================
            var filePath = dokument.ObjectPath ?? dokument.Dateipfad;
            if (string.IsNullOrWhiteSpace(filePath))
            {
                _logger.LogWarning("❌ Kein gültiger Pfad im Dokument {Id}", dokument.Id);
                return NotFound("❌ Ungültiger oder leerer Dateipfad.");
            }

            // =====================================================
            // 3️⃣ Chunked → Rekonstruktion
            // =====================================================
            if (dokument.IsChunked)
            {
                _logger.LogInformation("🧩 Chunked-Dokument erkannt ({Id}), starte Rekonstruktion...", dokument.Id);
                var reconstructed = await _chunkService.ReconstructFileFromWebDavAsync(dokument.Id);

                if (string.IsNullOrWhiteSpace(reconstructed) || !System.IO.File.Exists(reconstructed))
                {
                    _logger.LogError("❌ Rekonstruktion fehlgeschlagen für {Id}", dokument.Id);
                    return NotFound("❌ Chunked-Datei konnte nicht rekonstruiert werden.");
                }

                _logger.LogInformation("✅ Rekonstruktion erfolgreich ({Path})", reconstructed);

                // 🧠 AJOUT ICI : Désactiver le cache navigateur
                Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                Response.Headers["Pragma"] = "no-cache";
                Response.Headers["Expires"] = "0";

                return PhysicalFile(reconstructed, "application/pdf");
            }

            // =====================================================
            // 4️⃣ Pfad normalisieren (WebDAV-kompatibel)
            // =====================================================
            if (filePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                int idx = filePath.IndexOf("/DmsDaten/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    filePath = filePath.Substring(idx + "/DmsDaten/".Length);
            }

            filePath = filePath.TrimStart('/');

            _logger.LogInformation("📂 Lade PDF von WebDAV: {Path}", filePath);

            // =====================================================
            // 5️⃣ Download via WebDav
            // =====================================================
            var stream = await _WebDav.DownloadStreamStableAsync(filePath);
            if (stream == null)
            {
                _logger.LogError("❌ Datei konnte nicht geladen werden: {Path}", filePath);
                return NotFound($"❌ Datei nicht gefunden: {filePath}");
            }

            if (stream.Length == 0)
            {
                _logger.LogError("❌ Leere PDF von WebDAV empfangen: {Path}", filePath);
                return BadRequest("❌ Fehler: PDF leer oder nicht gefunden.");
            }

            _logger.LogInformation("✅ PDF erfolgreich geladen ({Bytes} Bytes)", stream.Length);

            Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            Response.Headers["Pragma"] = "no-cache";
            Response.Headers["Expires"] = "0";

            return File(stream, "application/pdf");
        }

        public async Task<JsonResult> OnGetOriginalMetadataAsync(Guid id)
        {
            _logger.LogInformation("📦 [OnGetOriginalMetadataAsync] Aufgerufen mit ID={Id}", id);

            Dokumente? dokument = null;
            Metadaten? meta = null;

            // Vérifie si c’est une version
            var version = await _dbContext.DokumentVersionen
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == id);

            if (version != null)
            {
                _logger.LogInformation("🔁 Version erkannt ({VersionId}), Original={OriginalId}", version.Id, version.OriginalId);

                // 1️⃣ Cherche la dernière version existante avec métadonnées
                var lastVersion = await _dbContext.DokumentVersionen
                    .AsNoTracking()
                    .Where(v => v.OriginalId == version.OriginalId)
                    .OrderByDescending(v => v.HochgeladenAm)
                    .FirstOrDefaultAsync();

                if (lastVersion != null)
                {
                    // 🔹 Essaie d'abord via MetadatenId
                    if (lastVersion.MetadatenId.HasValue)
                    {
                        meta = await _dbContext.Metadaten
                            .AsNoTracking()
                            .FirstOrDefaultAsync(m => m.Id == lastVersion.MetadatenId.Value);
                    }

                    // 🔹 Fallback : si vide, tente de lire depuis MetadataJson
                    if (meta == null && !string.IsNullOrEmpty(lastVersion.MetadataJson))
                    {
                        try
                        {
                            var metaDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(lastVersion.MetadataJson);
                            if (metaDict != null)
                            {
                                meta = new Metadaten
                                {
                                    Beschreibung = metaDict.GetValueOrDefault("Beschreibung")?.ToString(),
                                    Rechnungsnummer = metaDict.GetValueOrDefault("Rechnungsnummer")?.ToString(),
                                    Kundennummer = metaDict.GetValueOrDefault("Kundennummer")?.ToString(),
                                    Rechnungsbetrag = metaDict.TryGetDecimal("Rechnungsbetrag"),
                                    Nettobetrag = metaDict.TryGetDecimal("Nettobetrag"),
                                    Gesamtpreis = metaDict.TryGetDecimal("Gesamtpreis"),
                                    Steuerbetrag = metaDict.TryGetDecimal("Steuerbetrag"),
                                    Rechnungsdatum = metaDict.TryGetDateTime("Rechnungsdatum"),
                                    Lieferdatum = metaDict.TryGetDateTime("Lieferdatum"),
                                    Faelligkeitsdatum = metaDict.TryGetDateTime("Faelligkeitsdatum"),
                                    Zahlungsbedingungen = metaDict.GetValueOrDefault("Zahlungsbedingungen")?.ToString(),
                                    Email = metaDict.GetValueOrDefault("Email")?.ToString(),
                                    Telefon = metaDict.GetValueOrDefault("Telefon")?.ToString(),
                                    Adresse = metaDict.GetValueOrDefault("Adresse")?.ToString(),
                                    AnsprechPartner = metaDict.GetValueOrDefault("AnsprechPartner")?.ToString(),
                                    PdfAutor = metaDict.GetValueOrDefault("PdfAutor")?.ToString(),
                                    PdfBetreff = metaDict.GetValueOrDefault("PdfBetreff")?.ToString(),
                                    PdfSchluesselwoerter = metaDict.GetValueOrDefault("PdfSchluesselwoerter")?.ToString()
                                };
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "⚠️ Fehler beim Deserialisieren der MetadatenJson für Version {VersionId}", lastVersion.Id);
                        }
                    }

                    // Charge aussi le document original pour info
                    dokument = await _dbContext.Dokumente
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == version.OriginalId);
                }

                // 2️⃣ Fallback si rien trouvé
                if (meta == null && version.OriginalId.HasValue)
                {
                    dokument = await _dbContext.Dokumente
                        .Include(d => d.MetadatenObjekt)
                        .AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == version.OriginalId);
                    meta = dokument?.MetadatenObjekt;
                }
            }
            else
            {
                // 🔹 Cas document original
                dokument = await _dbContext.Dokumente
                    .Include(d => d.MetadatenObjekt)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.Id == id);

                meta = dokument?.MetadatenObjekt;
            }

            if (meta == null)
            {
                _logger.LogWarning("❌ Keine Metadaten gefunden für Dokument {Id}", id);
                return new JsonResult(new { success = false, message = "Keine Metadaten gefunden." });
            }

            _logger.LogInformation("✅ Metadaten erfolgreich für Dokument {Id} geladen", id);

            return new JsonResult(new
            {
                success = true,
                metadata = new
                {
                    meta.Beschreibung,
                    meta.Rechnungsnummer,
                    meta.Kundennummer,
                    meta.Rechnungsbetrag,
                    meta.Nettobetrag,
                    meta.Gesamtpreis,
                    meta.Steuerbetrag,
                    meta.Rechnungsdatum,
                    meta.Lieferdatum,
                    meta.Faelligkeitsdatum,
                    meta.Zahlungsbedingungen,
                    meta.Email,
                    meta.Telefon,
                    meta.Telefax,
                    meta.Adresse,
                    meta.AbsenderAdresse,
                    meta.AnsprechPartner,
                    meta.Lieferart,
                    meta.ArtikelAnzahl,
                    meta.SteuerNr,
                    meta.UIDNummer,
                    meta.IBAN,
                    meta.BIC,
                    meta.Bankverbindung,
                    meta.Zeitraum,
                    meta.PdfAutor,
                    meta.PdfBetreff,
                    meta.PdfSchluesselwoerter,
                    meta.Website,
                    meta.OCRText
                }
            });
        }



        public class SaveRequest
        {
            public string FileName { get; set; } = string.Empty;
            public List<SignaturePayload> Signatures { get; set; } = new();
            public OriginalMetadataDto Metadata { get; set; }
            public Guid FileId { get; set; } // 🔥 neu, damit Dokument auch ohne Signatur referenziert werden kann
            public List<HighlightPayload> Highlights { get; set; } = new(); // 🔥 NEU
            public List<TextElementDto> Texts { get; set; } = new();
            public List<ImagePayload> Images { get; set; } = new();



        }


        private async Task<JsonResult> SaveOverwriteAsync(SaveRequest request)
        {
            var dokument = await _dbContext.Dokumente.FindAsync(request.FileId);
            if (dokument == null)
                return new JsonResult(new { success = false, message = "❌ Dokument nicht gefunden" });

            try
            {
                using var ms = new MemoryStream();
                using var originalStream = await _WebDav.DownloadStreamStableAsync(dokument.ObjectPath);
                if (originalStream == null)
                    return new JsonResult(new { success = false, message = "❌ Datei konnte nicht geladen werden." });

                await originalStream.CopyToAsync(ms);
                ms.Position = 0;


                var outputStream = new MemoryStream();

                var writerProps = new iText.Kernel.Pdf.WriterProperties().SetFullCompressionMode(true);
                var pdfWriter = new iText.Kernel.Pdf.PdfWriter(outputStream, writerProps);
                pdfWriter.SetCloseStream(false); // 👈 verhindert, dass outputStream geschlossen wird

                using (var reader = new iText.Kernel.Pdf.PdfReader(ms))
                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader, pdfWriter))
                using (var doc = new iText.Layout.Document(pdfDoc))
                {
                    // === Highlights einbetten ===
                    // === Highlights einbetten ===
                    if (request.Highlights != null && request.Highlights.Any())
                    {
                        foreach (var hl in request.Highlights)
                        {
                            if (string.IsNullOrWhiteSpace(hl.ImageBase64))
                                continue;

                            var base64Data = hl.ImageBase64.Contains(",")
                                ? hl.ImageBase64.Split(',')[1]
                                : hl.ImageBase64;

                            byte[] imageBytes = Convert.FromBase64String(base64Data);

                            var img = new iText.Layout.Element.Image(
                                iText.IO.Image.ImageDataFactory.Create(imageBytes)
                            );

                            img.SetOpacity(0.4f); // halbtransparent
                            var page = pdfDoc.GetPage(hl.PageNumber);
                            float pageWidth = page.GetPageSize().GetWidth();
                            float pageHeight = page.GetPageSize().GetHeight();

                            img.ScaleAbsolute(pageWidth, pageHeight);
                            img.SetFixedPosition(hl.PageNumber, 0, 0);

                            doc.Add(img);
                        }
                    }

                    if (request.Signatures != null)
                    {
                        foreach (var sig in request.Signatures)
                        {
                            byte[] imgBytes = Convert.FromBase64String(sig.ImageBase64.Split(',')[1]);
                            var img = new iText.Layout.Element.Image(
                                iText.IO.Image.ImageDataFactory.Create(imgBytes));

                            var page = pdfDoc.GetPage(sig.PageNumber);
                            float pageWidth = page.GetPageSize().GetWidth();
                            float pageHeight = page.GetPageSize().GetHeight();

                            float x = sig.RelX * pageWidth;
                            float w = sig.RelW * pageWidth;
                            float h = sig.RelH * pageHeight;

                            // ✅ korrektes Y: Browser oben links → PDF unten links
                            float y = (1 - sig.RelY - sig.RelH) * pageHeight;

                            img.ScaleAbsolute(w, h);
                            img.SetFixedPosition(sig.PageNumber, x, y);

                            doc.Add(img);

                            _logger.LogInformation(
                                $"✔ Signatur: X={x}, Y={y}, W={w}, H={h}, RelX={sig.RelX}, RelY={sig.RelY}, RelW={sig.RelW}, RelH={sig.RelH}"
                            );
                        }

                    }
                }

                // ✅ Jetzt bleibt outputStream offen
                outputStream.Position = 0;
                await _WebDav.UploadStreamAsync(outputStream, dokument.ObjectPath, "application/pdf");

                // ===== Nur Metadaten speichern =====
                if (request.Metadata != null)
                {
                    var m = dokument.MetadatenObjekt;
                    if (m == null)
                    {
                        // 🆕 Falls keine Metadaten existieren → neu anlegen
                        m = new Metadaten { DokumentId = dokument.Id };
                        dokument.MetadatenObjekt = m;
                        _dbContext.Metadaten.Add(m);
                    }

                    // 🧠 Metadaten aktualisieren
                    m.Beschreibung = request.Metadata.Beschreibung;
                    m.Rechnungsnummer = request.Metadata.Rechnungsnummer;
                    m.Kundennummer = request.Metadata.Kundennummer;
                    m.Rechnungsbetrag = request.Metadata.Rechnungsbetrag;
                    m.Nettobetrag = request.Metadata.Nettobetrag;
                    m.Gesamtpreis = request.Metadata.Gesamtpreis;
                    m.Steuerbetrag = request.Metadata.Steuerbetrag;
                    m.Rechnungsdatum = request.Metadata.Rechnungsdatum;
                    m.Lieferdatum = request.Metadata.Lieferdatum;
                    m.Faelligkeitsdatum = request.Metadata.Faelligkeitsdatum;
                    m.Zahlungsbedingungen = request.Metadata.Zahlungsbedingungen;
                    m.Lieferart = request.Metadata.Lieferart;
                    m.ArtikelAnzahl = request.Metadata.ArtikelAnzahl;
                    m.Email = request.Metadata.Email;
                    m.Telefon = request.Metadata.Telefon;
                    m.Telefax = request.Metadata.Telefax;
                    m.IBAN = request.Metadata.IBAN;
                    m.BIC = request.Metadata.BIC;
                    m.Bankverbindung = request.Metadata.Bankverbindung;
                    m.SteuerNr = request.Metadata.SteuerNr;
                    m.UIDNummer = request.Metadata.UIDNummer;
                    m.Adresse = request.Metadata.Adresse;
                    m.AbsenderAdresse = request.Metadata.AbsenderAdresse;
                    m.AnsprechPartner = request.Metadata.AnsprechPartner;
                    m.Zeitraum = request.Metadata.Zeitraum;
                    m.PdfAutor = request.Metadata.PdfAutor;
                    m.PdfBetreff = request.Metadata.PdfBetreff;
                    m.PdfSchluesselwoerter = request.Metadata.PdfSchluesselwoerter;
                    m.Website = request.Metadata.Website;
                    m.OCRText = request.Metadata.OCRText;

                    // 👇 Le seul champ venant de `Dokumente`
                    dokument.FileSizeBytes = request.Metadata.FileSizeBytes;
                }

                // ===== Dokument aktualisieren =====
                dokument.HochgeladenAm = DateTime.UtcNow;
                dokument.IsUpdated = true;

                _dbContext.Dokumente.Update(dokument);
                await _dbContext.SaveChangesAsync();

                string redirectUrl = null;

                // ✅ Prüfen, ob es eine Aufgabe oder einen Workflow-Step gibt
                if (dokument.AufgabeId != null)
                {
                    var aufgabe = await _dbContext.Aufgaben.FindAsync(dokument.AufgabeId);
                    if (aufgabe != null)
                    {
                        aufgabe.Erledigt = true;
                        _dbContext.Aufgaben.Update(aufgabe);

                        redirectUrl = $"/Tests/Aufgaben/Details/{aufgabe.Id}";
                    }
                }
                else if (dokument.StepId != null && dokument.WorkflowId != null)
                {
                    var step = await _dbContext.Steps.FindAsync(dokument.StepId);
                    var aufgabe = await _dbContext.Aufgaben.FindAsync(dokument.AufgabeId);
                    if (step != null)
                    {
                        step.Completed = true;
                        aufgabe.Erledigt = true;
                        _dbContext.Steps.Update(step);

                        redirectUrl = $"/Workflow/StepDetails/{dokument.StepId}{dokument.WorkflowId}";
                    }
                }

                await _dbContext.SaveChangesAsync();

                return new JsonResult(new
                {
                    success = true,
                    message = "✔️ Dokument überschrieben.",
                    saveMethod = "SaveOverwriteAsync",
                    dokumentId = dokument.Id,
                    redirectUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Überschreiben");
                return new JsonResult(new { success = false, message = ex.InnerException?.Message ?? ex.Message });
            }
        }


        [HttpPost]
        public IActionResult OnPostSaveTempSignatures([FromBody] TempSignatureStore payload)
        {
            if (payload == null || payload.FileId == Guid.Empty)
                return BadRequest(new { success = false, message = "❌ Ungültige Daten." });

            _pendingSignatures[payload.FileId] = payload;
            return new JsonResult(new { success = true });
        }

        // Zugriff von außen (für Metadaten-Seite)
        public static List<SignaturePayload> GetPendingSignatures(Guid fileId)
        {
            if (_pendingSignatures.TryGetValue(fileId, out var store))
                return store.Signatures;
            return new List<SignaturePayload>();
        }

        // Nach finalem Speichern aufräumen
        public static void ClearPendingSignatures(Guid fileId)
        {
            if (_pendingSignatures.ContainsKey(fileId))
                _pendingSignatures.Remove(fileId);
        }

        public async Task<IActionResult> OnPostSaveMetaAsync(Guid id, string title, string category)
        {
            var dokument = await _dbContext.Dokumente.FindAsync(id);
            if (dokument == null) return NotFound();

            // ✅ Metadaten speichern
            dokument.Titel = title;
            dokument.Kategorie = category;
            await _dbContext.SaveChangesAsync();

            // ✅ Hol Signaturen aus Bearbeiten.cshtml.cs
            var signatures = BearbeitenModel.GetPendingSignatures(id);

            if (signatures.Any())
            {
                using var ms = new MemoryStream();
                using var originalStream = await _WebDav.DownloadStreamStableAsync(dokument.ObjectPath);
                if (originalStream == null)
                {
                    _logger.LogWarning("❌ Datei konnte nicht geladen werden: {Path}", dokument.ObjectPath);
                    return RedirectToPage("/Dokument/AlleVersionen");
                }
                await originalStream.CopyToAsync(ms);
                ms.Position = 0;


                var outputStream = new MemoryStream();

                using (var reader = new iText.Kernel.Pdf.PdfReader(ms))
                using (var writer = new iText.Kernel.Pdf.PdfWriter(outputStream))
                using (var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader, writer))
                {
                    var doc = new iText.Layout.Document(pdfDoc);

                    foreach (var sig in signatures)
                    {
                        var base64Data = sig.ImageBase64.Contains(",")
                            ? sig.ImageBase64.Split(',')[1]
                            : sig.ImageBase64;

                        byte[] imageBytes = Convert.FromBase64String(base64Data);
                        var img = new iText.Layout.Element.Image(
                            iText.IO.Image.ImageDataFactory.Create(imageBytes)
                        );
                        img.ScaleToFit(sig.Width, sig.Height);

                        var page = pdfDoc.GetPage(sig.PageNumber);
                        var pageHeight = page.GetPageSize().GetHeight();


                        img.SetFixedPosition(sig.PageNumber, sig.X, sig.Y, sig.Width);

                        doc.Add(img);
                    }

                    doc.Close();
                }

                // Neue Version speichern
                outputStream.Position = 0;
                await _WebDav.UploadStreamAsync(outputStream, dokument.ObjectPath, "application/pdf");

                // Pending Signatures aufräumen
                BearbeitenModel.ClearPendingSignatures(id);
            }

            return RedirectToPage("/Dokument/AlleVersionen");
        }

        public async Task<IActionResult> OnGetUserFirma()
        {
            var user = await _userManager.GetUserAsync(User);

            string firma = user.FirmenName ?? "Unbekannte Firma";

            return new JsonResult(new { success = true, firma });
        }


        public async Task<IActionResult> OnGetStampText()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            string firma = user.FirmenName ?? "Firma Unbekannt";
            string datum = DateTime.UtcNow.ToString("dd.MM.yyyy");
            string text = text = $"{firma}\n{DateTime.Now:dd.MM.yyyy}";

            return new JsonResult(new { success = true, text });
        }


        public class OriginalMetadataDto
        {
            public string Titel { get; set; }
            public string Beschreibung { get; set; }
            public string Rechnungsnummer { get; set; }
            public string Kundennummer { get; set; }
            public decimal? Rechnungsbetrag { get; set; }
            public decimal? Nettobetrag { get; set; }
            public decimal? Gesamtpreis { get; set; }
            public decimal? Steuerbetrag { get; set; }
            public DateTime? Rechnungsdatum { get; set; }
            public DateTime? Lieferdatum { get; set; }
            public DateTime? Faelligkeitsdatum { get; set; }
            public string Zahlungsbedingungen { get; set; }
            public string Lieferart { get; set; }
            public int? ArtikelAnzahl { get; set; }
            public string Email { get; set; }
            public string Telefon { get; set; }
            public string Telefax { get; set; }
            public string IBAN { get; set; }
            public string BIC { get; set; }
            public string Bankverbindung { get; set; }
            public string SteuerNr { get; set; }
            public string UIDNummer { get; set; }
            public string Adresse { get; set; }
            public string AbsenderAdresse { get; set; }
            public string AnsprechPartner { get; set; }
            public string Zeitraum { get; set; }
            public string Website { get; set; }
            public string OCRText { get; set; }
            public long? FileSizeBytes { get; set; }
            public string? Kategorie { get; set; }
            [JsonPropertyName("pdfautor")]
            public string PdfAutor { get; set; }

            [JsonPropertyName("pdfbetreff")]
            public string PdfBetreff { get; set; }

            [JsonPropertyName("pdfschluesselwoerter")]
            public string PdfSchluesselwoerter { get; set; }

        }


        public class SignatureSaveRequest
        {
            public string? ImageBase64 { get; set; }
        }

        // DTO for saving edited images
        public class SaveImageRequest
        {
            public Guid FileId { get; set; }
            public string ImageBase64 { get; set; }
            public OriginalMetadataDto? Metadata { get; set; }
        }

        public class SignaturePayload
        {
            public Guid FileId { get; set; }
            public int PageNumber { get; set; }
            public string ImageBase64 { get; set; } = string.Empty;

            // alte absolute Werte (kannst du behalten falls du sie noch nutzt)
            public float X { get; set; }
            public float Y { get; set; }
            public float Width { get; set; }
            public float Height { get; set; }

            public float ViewportWidth { get; set; }
            public float ViewportHeight { get; set; }

            // ✅ neue relative Werte (Frontend sendet die)
            public float RelX { get; set; }
            public float RelY { get; set; }
            public float RelW { get; set; }
            public float RelH { get; set; }
        }

        public class HighlightPayload
        {
            public Guid FileId { get; set; }
            public int PageNumber { get; set; }

            // Base64 pas forcément utile pour highlight, mais tu peux le garder
            public string ImageBase64 { get; set; } = string.Empty;

            // Degré de transparence du surlignage (0-1)
            public float Opacity { get; set; } = 0.3f;

            // ✅ Coordonnées relatives envoyées par le frontend (0–1)
            public float RelX { get; set; }
            public float RelY { get; set; }
            public float RelW { get; set; }
            public float RelH { get; set; }
            public string? Color { get; set; }
        }

        public class TextElementDto
        {
            public Guid FileId { get; set; }
            public int PageNumber { get; set; }
            public string Text { get; set; } = "";
            public double RelX { get; set; }
            public double RelY { get; set; }
            public double RelW { get; set; }
            public double RelH { get; set; }
            public double FontSize { get; set; }
            public string? Color { get; set; }
            public string? FontFamily { get; set; }
            public bool Bold { get; set; }
            public bool Italic { get; set; }
            public bool Underline { get; set; }
        }

        public class ImagePayload
        {
            public int PageNumber { get; set; }
            public string ImageBase64 { get; set; } = string.Empty;
            public float RelX { get; set; }
            public float RelY { get; set; }
            public float RelW { get; set; }
            public float RelH { get; set; }
        }
        public class ManifestModel
        {
            public Guid OriginalDokumentId { get; set; }
            public Guid VersionDokumentId { get; set; }
            public string Dateiname { get; set; }
            public DateTime ErzeugtAm { get; set; }
            public List<ManifestChunk> Chunks { get; set; } = new();
        }

        public class ManifestChunk
        {
            public int Index { get; set; }
            public string Hash { get; set; }
            public long Size { get; set; }
            public string Path { get; set; }
        }



    }
}
