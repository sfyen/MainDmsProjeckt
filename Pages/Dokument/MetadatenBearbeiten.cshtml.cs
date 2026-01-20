using DmsProjeckt.Data;
using DmsProjeckt.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Pages.Dokument
{
    public class MetadatenBearbeitenModel : PageModel
    {

        private readonly ApplicationDbContext _db;
        private readonly ILogger<MetadatenBearbeitenModel> _logger;
        private readonly WebDavStorageService _WebDav ;

        public MetadatenBearbeitenModel(ApplicationDbContext db, ILogger<MetadatenBearbeitenModel> logger, WebDavStorageService WebDav)
        {
            _db = db;
            _logger = logger;
            _WebDav = WebDav;
        }

        [BindProperty]
        public Dokumente Dokument { get; set; }
        [BindProperty]
        public string PendingSignaturesJson { get; set; }
        [BindProperty]
        public Metadaten Metadaten { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            try
            {
                Dokumente? dokument = null;
                DokumentVersionen? version = null;

                // 1️⃣ Recherche dans DokumentVersionen d'abord
                version = await _db.DokumentVersionen
                    .AsNoTracking()
                    .FirstOrDefaultAsync(v => v.Id == id);

                if (version != null)
                {
                    _logger.LogInformation("📄 Version erkannt: {File}", version.Dateiname);

                    // Charger l’original lié
                    dokument = await _db.Dokumente
                        .Include(d => d.Abteilung)
                        .Include(d => d.MetadatenObjekt)
                        .FirstOrDefaultAsync(d => d.Id == version.DokumentId);

                    Dokument = dokument ?? new Dokumente();

                    // 🧠 Essayer de charger les métadaten depuis la version
                    if (!string.IsNullOrEmpty(version.MetadataJson))
                    {
                        _logger.LogInformation("📦 MetadataJson gefunden – lade Version-Metadaten");
                        try
                        {
                            Metadaten = System.Text.Json.JsonSerializer.Deserialize<Metadaten>(version.MetadataJson)
                                ?? new Metadaten();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "⚠️ Fehler beim Parsen von MetadataJson – fallback auf Original");
                            Metadaten = dokument?.MetadatenObjekt ?? new Metadaten();
                        }
                    }
                    else
                    {
                        // ⚠️ Pas de MetadataJson → on récupère depuis l’original
                        _logger.LogInformation("ℹ️ Version ohne MetadataJson – verwende Original-Metadaten");
                        Metadaten = dokument?.MetadatenObjekt ?? new Metadaten();
                    }

                    return Page();
                }

                // 2️⃣ Sinon → c’est un document original
                dokument = await _db.Dokumente
                    .Include(d => d.Abteilung)
                    .Include(d => d.MetadatenObjekt)
                    .FirstOrDefaultAsync(d => d.Id == id);

                if (dokument == null)
                {
                    _logger.LogWarning("❌ Dokument {Id} nicht gefunden", id);
                    return NotFound();
                }

                Dokument = dokument;
                Metadaten = dokument.MetadatenObjekt ?? new Metadaten();

                _logger.LogInformation("📘 Original-Dokument geladen: {Name}", dokument.Dateiname);
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Laden der Metadaten für {Id}", id);
                TempData["Error"] = $"Fehler beim Laden: {ex.Message}";
                return RedirectToPage("/Dokument/Index");
            }
        }

        public async Task<IActionResult> OnPostAsync(Guid id)
        {
            _logger.LogInformation("💾 Starte Metadaten-Speicherung für ID={Id}", id);

            // 🔹 1️⃣ Vérifie si c’est une version
            var version = await _db.DokumentVersionen.FirstOrDefaultAsync(v => v.Id == id);
            if (version != null)
            {
                _logger.LogInformation("🧩 Version erkannt ({Name}), speichere JSON-Metadaten", version.Dateiname);

                var metaDict = new Dictionary<string, string?>
                {
                    ["Beschreibung"] = Metadaten.Beschreibung,
                    ["Rechnungsnummer"] = Metadaten.Rechnungsnummer,
                    ["Kundennummer"] = Metadaten.Kundennummer,
                    ["Email"] = Metadaten.Email,
                    ["Telefon"] = Metadaten.Telefon,
                    ["IBAN"] = Metadaten.IBAN,
                    ["BIC"] = Metadaten.BIC,
                    ["Adresse"] = Metadaten.Adresse,
                    ["PdfAutor"] = Metadaten.PdfAutor,
                    ["PdfBetreff"] = Metadaten.PdfBetreff,
                    ["PdfSchluesselwoerter"] = Metadaten.PdfSchluesselwoerter,
                    ["Website"] = Metadaten.Website,
                    ["OCRText"] = Metadaten.OCRText
                };

                version.MetadataJson = System.Text.Json.JsonSerializer.Serialize(metaDict);

                _db.DokumentVersionen.Update(version);
                await _db.SaveChangesAsync();

                TempData["Success"] = "✅ Metadaten für Version erfolgreich gespeichert.";
                return RedirectToPage("/Dokument/AlleVersionen");
            }

            // 🔹 2️⃣ Sinon → document original
            var dokument = await _db.Dokumente
                .Include(d => d.MetadatenObjekt)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (dokument == null)
            {
                _logger.LogWarning("❌ Dokument {Id} nicht gefunden", id);
                return NotFound();
            }

            var meta = dokument.MetadatenObjekt ?? new Metadaten();
            meta.Beschreibung = Metadaten.Beschreibung;
            meta.Rechnungsnummer = Metadaten.Rechnungsnummer;
            meta.Kundennummer = Metadaten.Kundennummer;
            meta.Email = Metadaten.Email;
            meta.Telefon = Metadaten.Telefon;
            meta.PdfAutor = Metadaten.PdfAutor;
            meta.PdfBetreff = Metadaten.PdfBetreff;
            meta.PdfSchluesselwoerter = Metadaten.PdfSchluesselwoerter;
            meta.Website = Metadaten.Website;
            meta.OCRText = Metadaten.OCRText;

            _db.Metadaten.Update(meta);
            await _db.SaveChangesAsync();

            TempData["Success"] = "✅ Metadaten für Original erfolgreich gespeichert.";
            return RedirectToPage("/Dokument/Index");
        }



    }
    public class SignaturePayload
    {
        public Guid FileId { get; set; }
        public int PageNumber { get; set; }
        public string ImageBase64 { get; set; }
        public float X { get; set; }   // war int
        public float Y { get; set; }   // war int
        public float Width { get; set; }   // war int
        public float Height { get; set; }  // war int
    }


}
