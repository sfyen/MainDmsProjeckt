using Azure.AI.FormRecognizer.DocumentAnalysis;
using DmsProjeckt.Data;
using DmsProjeckt.Helpers;
using DmsProjeckt.Service;
using DmsProjeckt.Services;
using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;
//using iTextSharp.text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

using SkiaSharp;
using System.Drawing;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;
using ZXing.SkiaSharp;
using static DmsProjeckt.Pages.Tests.UploadMultiModel;

namespace DmsProjeckt.Pages.Tests
{
    public class UploadMultiModel : PageModel
    {
        private readonly AzureOcrService _ocrService;
        private readonly WebDavStorageService _Webdavstorage;
        private readonly ApplicationDbContext _db;
        private readonly AuditLogDokumentService _audit;
        private readonly DocumentHashService _hashService;
        private readonly ChunkService _chunkService;

        public UploadMultiModel(
            AzureOcrService ocrService,
            WebDavStorageService Webdavstorage,
            ApplicationDbContext db,
            AuditLogDokumentService audit,
            DocumentHashService hashService,
            ChunkService chunkService)
        {
            _ocrService = ocrService;
            _Webdavstorage = Webdavstorage;
            _db = db;
            _audit = audit;
            _hashService = hashService;
            _chunkService = chunkService;
        }

        [BindProperty(SupportsGet = false)]
        public List<IFormFile> Dateien { get; set; }
        [BindProperty]
        public List<DokumentViewModel> Dokumente { get; set; } = new();
        public List<Abteilung> Abteilungen { get; set; } = new();
        [BindProperty]
        public int? AbteilungId { get; set; }
        [BindProperty] public string Titel { get; set; }
        [BindProperty] public string Kategorie { get; set; }
        [BindProperty] public string Beschreibung { get; set; }
        [BindProperty] public string Rechnungsnummer { get; set; }
        [BindProperty] public string Kundennummer { get; set; }
        [BindProperty] public string Rechnungsbetrag { get; set; }
        [BindProperty] public string Nettobetrag { get; set; }
        [BindProperty] public string Gesamtpreis { get; set; }
        [BindProperty] public string Steuerbetrag { get; set; }
        [BindProperty] public string Rechnungsdatum { get; set; }
        [BindProperty] public string Lieferdatum { get; set; }
        [BindProperty] public string Faelligkeitsdatum { get; set; }
        [BindProperty] public string Zahlungsbedingungen { get; set; }
        [BindProperty] public string Lieferart { get; set; }
        [BindProperty] public string ArtikelAnzahl { get; set; }
        [BindProperty] public string Email { get; set; }
        [BindProperty] public string Telefon { get; set; }
        [BindProperty] public string Telefax { get; set; }
        [BindProperty] public string IBAN { get; set; }
        [BindProperty] public string BIC { get; set; }
        [BindProperty] public string Bankverbindung { get; set; }
        [BindProperty] public string SteuerNr { get; set; }
        [BindProperty] public string UIDNummer { get; set; }
        [BindProperty] public string Adresse { get; set; }
        [BindProperty] public string AbsenderAdresse { get; set; }
        [BindProperty] public string AnsprechPartner { get; set; }
        [BindProperty] public string Zeitraum { get; set; }
        [BindProperty] public string PdfAutor { get; set; }
        [BindProperty] public string PdfBetreff { get; set; }
        [BindProperty] public string PdfSchluesselwoerter { get; set; }
        [BindProperty] public string Website { get; set; }
        [BindProperty] public string OCRText { get; set; }
        [BindProperty] public IFormFile Datei { get; set; }
        public List<string> Kategorien { get; set; } = new() { "rechnungen", "verträge", "gebühren", "projekt_A", "korrespondenz", "unbekannt" };

        public int? UserAbteilungId { get; set; }
        public string UserAbteilungName { get; set; }


        public class DokumentViewModel
        {

            public IFormFile Content { get; set; }
            public string FileName { get; set; }
            public MetadataModel Metadata { get; set; } = new();
            public DokumentStatus Status { get; set; } = DokumentStatus.Pending;
            public string ObjectPath { get; set; }
            public int? Progress { get; set; }
            public string? Titel { get; set; }
            public DateTime? HochgeladenAm { get; set; } = DateTime.UtcNow;
            public bool IsGrossDoc { get; set; }
            public string AbteilungName { get; set; }
            public int? AbteilungId { get; set; }
            public string Kategorie
            {
                get => Metadata.Kategorie;
                set => Metadata.Kategorie = value;
            }
        }
        [TempData]
        public string DokumenteJson { get; set; }

        public class MetadataModel
        {
            public int? KundeId { get; set; }
            public string Kategorie { get; set; }
            public string Beschreibung { get; set; }
            public string Titel { get; set; }
            public string Rechnungsnummer { get; set; }
            public string Kundennummer { get; set; }
            public string Rechnungsbetrag { get; set; }
            public string Nettobetrag { get; set; }
            public string Gesamtpreis { get; set; }
            public string Steuerbetrag { get; set; }
            public string Rechnungsdatum { get; set; }
            public string Lieferdatum { get; set; }
            public string Faelligkeitsdatum { get; set; }
            public string Zahlungsbedingungen { get; set; }
            public string Lieferart { get; set; }
            public string ArtikelAnzahl { get; set; }

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

            public string PdfAutor { get; set; }
            public string PdfBetreff { get; set; }
            public string PdfSchluesselwoerter { get; set; }
            public string Website { get; set; }
            public string OCRText { get; set; }
            public int? AbteilungId { get; set; }

            public bool IsGrossDoc { get; set; }

            public string AbteilungName { get; set; }
            public string ObjectPath { get; set; }
            public string FileHash { get; set; }
        }



        public async Task OnGetAsync(bool analyzed = false, Guid? id = null, string ids = null)
        {
            Dokumente = new List<DokumentViewModel>();

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                Console.WriteLine("❌ Kein Benutzer gefunden!");
                return;
            }

            // 🔐 Abteilungen laden
            if (User.IsInRole("Admin") || User.IsInRole("SuperAdmin"))
            {
                Abteilungen = await _db.Abteilungen.OrderBy(a => a.Name).ToListAsync();
                Console.WriteLine($"[DEBUG] Admin angemeldet → {Abteilungen.Count} Abteilungen geladen");
            }
            else
            {
                if (user.AbteilungId.HasValue)
                {
                    var abt = await _db.Abteilungen.FindAsync(user.AbteilungId.Value);
                    if (abt != null)
                    {
                        UserAbteilungId = abt.Id;
                        UserAbteilungName = abt.Name;
                        Abteilungen = new List<Abteilung> { abt };
                        Console.WriteLine($"[DEBUG] User angemeldet → Abteilung {abt.Name}");
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ Benutzer hat keine Abteilung.");
                    Abteilungen = new List<Abteilung>();
                }
            }

            // =======================
            // 1️⃣ Analyse: TempData laden
            // =======================
            if (analyzed && TempData.ContainsKey(nameof(DokumenteJson)))
            {
                DokumenteJson = TempData[nameof(DokumenteJson)]?.ToString();
                if (!string.IsNullOrEmpty(DokumenteJson))
                {
                    var serialisiert = JsonSerializer.Deserialize<List<DokumentSerialisiert>>(DokumenteJson);
                    if (serialisiert != null)
                    {
                        Dokumente = serialisiert.Select(d => new DokumentViewModel
                        {
                            FileName = d.FileName,
                            Metadata = d.Metadata ?? new MetadataModel(),
                            Status = d.Status
                        }).ToList();
                    }
                }
                TempData.Keep(nameof(DokumenteJson));
                Console.WriteLine($"✔️ Geladene Dokumente: {Dokumente.Count}");
                return;
            }

            // =======================
            // 2️⃣ Einzelnes Dokument (?id=)
            // =======================
            if (id.HasValue)
            {
                var doc = await _db.Dokumente
                    .Include(d => d.MetadatenObjekt)
                    .FirstOrDefaultAsync(d => d.Id == id.Value);

                if (doc != null)
                {
                    var meta = doc.MetadatenObjekt ?? new Metadaten();

                    Dokumente.Add(new DokumentViewModel
                    {
                        FileName = doc.Dateiname,
                        Metadata = new MetadataModel
                        {
                            Kategorie = meta.Kategorie ?? doc.Kategorie,
                            Beschreibung = meta.Beschreibung,
                            Titel = meta.Titel,
                            Rechnungsnummer = meta.Rechnungsnummer,
                            Kundennummer = meta.Kundennummer,
                            Rechnungsbetrag = meta.Rechnungsbetrag?.ToString("F2"),
                            Nettobetrag = meta.Nettobetrag?.ToString("F2"),
                            Gesamtpreis = meta.Gesamtpreis?.ToString("F2"),
                            Steuerbetrag = meta.Steuerbetrag?.ToString("F2"),
                            Rechnungsdatum = meta.Rechnungsdatum?.ToString("yyyy-MM-dd"),
                            Lieferdatum = meta.Lieferdatum?.ToString("yyyy-MM-dd"),
                            Faelligkeitsdatum = meta.Faelligkeitsdatum?.ToString("yyyy-MM-dd"),
                            Zahlungsbedingungen = meta.Zahlungsbedingungen,
                            Lieferart = meta.Lieferart,
                            ArtikelAnzahl = meta.ArtikelAnzahl?.ToString(),
                            Email = meta.Email,
                            Telefon = meta.Telefon,
                            Telefax = meta.Telefax,
                            IBAN = meta.IBAN,
                            BIC = meta.BIC,
                            Bankverbindung = meta.Bankverbindung,
                            SteuerNr = meta.SteuerNr,
                            UIDNummer = meta.UIDNummer,
                            Adresse = meta.Adresse,
                            AbsenderAdresse = meta.AbsenderAdresse,
                            AnsprechPartner = meta.AnsprechPartner,
                            Zeitraum = meta.Zeitraum,
                            PdfAutor = meta.PdfAutor,
                            PdfBetreff = meta.PdfBetreff,
                            PdfSchluesselwoerter = meta.PdfSchluesselwoerter,
                            Website = meta.Website,
                            OCRText = meta.OCRText,
                            ObjectPath = doc.ObjectPath
                        },
                        Status = DokumentStatus.Analyzed
                    });

                    Console.WriteLine($"✔️ Dokument geladen: {doc.Dateiname} ({doc.Id})");
                }

                return;
            }

            // =======================
            // 3️⃣ Mehrere Dokumente (?ids=...)
            // =======================
            if (!string.IsNullOrEmpty(ids))
            {
                var guids = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(g => Guid.TryParse(g, out var guid) ? guid : (Guid?)null)
                    .Where(g => g.HasValue)
                    .Select(g => g.Value)
                    .ToList();

                if (guids.Any())
                {
                    var docs = await _db.Dokumente
                        .Include(d => d.MetadatenObjekt)
                        .Where(d => guids.Contains(d.Id))
                        .ToListAsync();

                    foreach (var doc in docs)
                    {
                        var meta = doc.MetadatenObjekt ?? new Metadaten();

                        Dokumente.Add(new DokumentViewModel
                        {
                            FileName = doc.Dateiname,
                            Metadata = new MetadataModel
                            {
                                Kategorie = meta.Kategorie ?? doc.Kategorie,
                                Beschreibung = meta.Beschreibung,
                                Titel = meta.Titel,
                                Rechnungsnummer = meta.Rechnungsnummer,
                                Kundennummer = meta.Kundennummer,
                                Rechnungsbetrag = meta.Rechnungsbetrag?.ToString("F2"),
                                Nettobetrag = meta.Nettobetrag?.ToString("F2"),
                                Gesamtpreis = meta.Gesamtpreis?.ToString("F2"),
                                Steuerbetrag = meta.Steuerbetrag?.ToString("F2"),
                                Rechnungsdatum = meta.Rechnungsdatum?.ToString("yyyy-MM-dd"),
                                Lieferdatum = meta.Lieferdatum?.ToString("yyyy-MM-dd"),
                                Faelligkeitsdatum = meta.Faelligkeitsdatum?.ToString("yyyy-MM-dd"),
                                Zahlungsbedingungen = meta.Zahlungsbedingungen,
                                Lieferart = meta.Lieferart,
                                ArtikelAnzahl = meta.ArtikelAnzahl?.ToString(),
                                Email = meta.Email,
                                Telefon = meta.Telefon,
                                Telefax = meta.Telefax,
                                IBAN = meta.IBAN,
                                BIC = meta.BIC,
                                Bankverbindung = meta.Bankverbindung,
                                SteuerNr = meta.SteuerNr,
                                UIDNummer = meta.UIDNummer,
                                Adresse = meta.Adresse,
                                AbsenderAdresse = meta.AbsenderAdresse,
                                AnsprechPartner = meta.AnsprechPartner,
                                Zeitraum = meta.Zeitraum,
                                PdfAutor = meta.PdfAutor,
                                PdfBetreff = meta.PdfBetreff,
                                PdfSchluesselwoerter = meta.PdfSchluesselwoerter,
                                Website = meta.Website,
                                OCRText = meta.OCRText,
                                ObjectPath = doc.ObjectPath
                            },
                            Status = DokumentStatus.Analyzed
                        });
                    }

                    Console.WriteLine($"✔️ {Dokumente.Count} Dokumente geladen");
                }
                // =======================
                // 4️⃣ Standardfall: Alle gespeicherten Dokumente anzeigen (Explorer-Ansicht)
                // =======================
                if (!analyzed && !id.HasValue && string.IsNullOrEmpty(ids))
                {
                    var docs = await _db.Dokumente
                        .Include(d => d.MetadatenObjekt)
                        .OrderByDescending(d => d.HochgeladenAm)
                        .ToListAsync();

                    Dokumente = docs.Select(doc => new DokumentViewModel
                    {
                        FileName = doc.Dateiname,
                        Metadata = new MetadataModel
                        {
                            Kategorie = doc.MetadatenObjekt?.Kategorie ?? doc.Kategorie,
                            Beschreibung = doc.MetadatenObjekt?.Beschreibung,
                            Titel = doc.MetadatenObjekt?.Titel,
                            Rechnungsnummer = doc.MetadatenObjekt?.Rechnungsnummer,
                            Kundennummer = doc.MetadatenObjekt?.Kundennummer,
                            Rechnungsbetrag = doc.MetadatenObjekt?.Rechnungsbetrag?.ToString("F2"),
                            Nettobetrag = doc.MetadatenObjekt?.Nettobetrag?.ToString("F2"),
                            Gesamtpreis = doc.MetadatenObjekt?.Gesamtpreis?.ToString("F2"),
                            Steuerbetrag = doc.MetadatenObjekt?.Steuerbetrag?.ToString("F2"),
                            Rechnungsdatum = doc.MetadatenObjekt?.Rechnungsdatum?.ToString("yyyy-MM-dd"),
                            Lieferdatum = doc.MetadatenObjekt?.Lieferdatum?.ToString("yyyy-MM-dd"),
                            Faelligkeitsdatum = doc.MetadatenObjekt?.Faelligkeitsdatum?.ToString("yyyy-MM-dd"),
                            Zahlungsbedingungen = doc.MetadatenObjekt?.Zahlungsbedingungen,
                            Lieferart = doc.MetadatenObjekt?.Lieferart,
                            ArtikelAnzahl = doc.MetadatenObjekt?.ArtikelAnzahl?.ToString(),
                            Email = doc.MetadatenObjekt?.Email,
                            Telefon = doc.MetadatenObjekt?.Telefon,
                            Telefax = doc.MetadatenObjekt?.Telefax,
                            IBAN = doc.MetadatenObjekt?.IBAN,
                            BIC = doc.MetadatenObjekt?.BIC,
                            Bankverbindung = doc.MetadatenObjekt?.Bankverbindung,
                            SteuerNr = doc.MetadatenObjekt?.SteuerNr,
                            UIDNummer = doc.MetadatenObjekt?.UIDNummer,
                            Adresse = doc.MetadatenObjekt?.Adresse,
                            AbsenderAdresse = doc.MetadatenObjekt?.AbsenderAdresse,
                            AnsprechPartner = doc.MetadatenObjekt?.AnsprechPartner,
                            Zeitraum = doc.MetadatenObjekt?.Zeitraum,
                            Website = doc.MetadatenObjekt?.Website,
                            OCRText = doc.MetadatenObjekt?.OCRText,
                            ObjectPath = doc.ObjectPath
                        },
                        Status = DokumentStatus.Fertig
                    }).ToList();

                    Console.WriteLine($"📁 {Dokumente.Count} gespeicherte Dokumente für Explorer geladen.");
                }

            }
        }


        private (string Title, string Author, string Subject, string Keywords) ExtractPdfMetadata(string filePath)
        {
            using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(filePath, PdfSharp.Pdf.IO.PdfDocumentOpenMode.ReadOnly);

            string title = pdf.Info.Title ?? "";
            string author = pdf.Info.Author ?? "";
            string subject = pdf.Info.Subject ?? "";
            string keywords = pdf.Info.Keywords ?? "";

            return (title, author, subject, keywords);
        }

        private static string ToStringValue(decimal? value)
        {
            return value?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static decimal? ToDecimalValue(string value)
        {
            if (decimal.TryParse(value?.Replace("€", "").Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result))
                return result;
            return null;
        }
        [RequestSizeLimit(4L * 1024 * 1024 * 1024)]
        [DisableRequestSizeLimit]
        public async Task<IActionResult> OnPostAnalyzeAsync()
        {
            Dokumente = new List<DokumentViewModel>();

            if (Dateien == null || Dateien.Count == 0)
            {
                ModelState.AddModelError("Dateien", "Bitte wählen Sie mindestens eine Datei aus.");
                return Page();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            var firma = user?.FirmenName?.Trim()?.ToLowerInvariant();
            var kunde = await ResolveKundeForUserAsync(user);

            if (string.IsNullOrWhiteSpace(firma) || kunde == null)
            {
                TempData["Error"] = "❌ Benutzer/Kunde ungültig.";
                return RedirectToPage(new { analyzed = true });
            }

            Console.WriteLine($"👤 Benutzer = {user?.UserName}, Firma = {firma}");
            Console.WriteLine($"📂 Anzahl Dateien: {Dateien.Count}");
            Console.WriteLine("========================================");

            foreach (var file in Dateien)
            {
                var vm = new DokumentViewModel
                {
                    Content = file,
                    FileName = file.FileName,
                    Titel = Path.GetFileNameWithoutExtension(file.FileName),
                    Progress = 10,
                    Metadata = new MetadataModel()
                };

                try
                {
                    if (file.Length == 0)
                    {
                        vm.Metadata.Beschreibung = "⚠️ Datei ist leer oder konnte nicht gelesen werden.";
                        Dokumente.Add(vm);
                        continue;
                    }

                    // =======================================================
                    // 1️⃣ HASH + DUPLIKATENPRÜFUNG
                    // =======================================================
                    string hash;
                    using (var hashStream = file.OpenReadStream())
                        hash = _hashService.ComputeHash(hashStream);

                    vm.Metadata.FileHash = hash;

                    var existing = await _db.Dokumente
                        .Include(d => d.MetadatenObjekt)
                        .FirstOrDefaultAsync(d => d.FileHash == hash);

                    if (existing != null)
                    {
                        Console.WriteLine($"♻️ Duplikat erkannt → {existing.Dateiname}");
                        vm.ObjectPath = existing.ObjectPath;
                        vm.Metadata = new MetadataModel
                        {
                            Titel = existing.MetadatenObjekt?.Titel ?? existing.Titel,
                            Beschreibung = "Duplikat erkannt – Datei wurde bereits hochgeladen.",
                            Kategorie = existing.MetadatenObjekt?.Kategorie ?? existing.Kategorie ?? "allgemein"
                        };
                        vm.Status = DokumentStatus.Analyzed;
                        vm.Progress = 100;
                        Dokumente.Add(vm);
                        continue;
                    }

                    // =======================================================
                    // 2️⃣ METADATEN + OCR
                    // =======================================================
                    // =======================================================
                    // 2️⃣ METADATEN + OCR
                    // =======================================================
                    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

                    if (ext is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" or ".txt" or ".doc" or ".docx" or ".xls" or ".xlsx")
                    {
                        if (file.Length > 20 * 1024 * 1024)
                        {
                            using var pdfStream = file.OpenReadStream();
                            // Only try PDF reader for actual PDFs
                            if (ext == ".pdf") 
                            { 
                                try 
                                {
                                    using var reader = new iText.Kernel.Pdf.PdfReader(pdfStream);
                                    using var pdfDoc = new iText.Kernel.Pdf.PdfDocument(reader);
                                    var info = pdfDoc.GetDocumentInfo();
                                    vm.Metadata.Titel = info.GetTitle() ?? Path.GetFileNameWithoutExtension(file.FileName);
                                    vm.Metadata.PdfAutor = info.GetAuthor();
                                    vm.Metadata.PdfBetreff = info.GetSubject();
                                    vm.Metadata.PdfSchluesselwoerter = info.GetKeywords();
                                }
                                catch
                                {
                                     vm.Metadata.Titel = Path.GetFileNameWithoutExtension(file.FileName);
                                }
                            }
                            else
                            {
                                 vm.Metadata.Titel = Path.GetFileNameWithoutExtension(file.FileName);
                            }
                            
                            vm.Metadata.Beschreibung = "Große Datei (>20MB) – nur Basis-Metadaten.";
                            await ApplyDetectedMetadataAsync(vm, file.FileName, "");
                        }
                        else
                        {
                             // A) AZURE SUPPORTED (PDF / IMAGES)
                             if (ext is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff")
                             {
                                try
                                {
                                    using var ocrStream = file.OpenReadStream();
                                    var res = await _ocrService.AnalyzeInvoiceAsync(ocrStream);
                                    var doc = res.Documents.FirstOrDefault();

                                    var fullText = string.Join(" ", res.Pages.SelectMany(p => p.Lines).Select(l => l.Content));
                                    await ApplyDetectedMetadataAsync(vm, file.FileName, fullText);
                                    vm.Metadata.Titel = Path.GetFileNameWithoutExtension(file.FileName);
                                    vm.Metadata.OCRText = fullText;
                                    vm.Metadata.Beschreibung = "Metadaten automatisch extrahiert (Azure + Regex)";

                                    if (doc != null)
                                    {
                                        var fields = doc.Fields;
                                        string Extract(string key) => fields.GetValueOrDefault(key)?.Content?.Trim();

                                        // 🧩 1️⃣ Azure Recognizer
                                        vm.Metadata.Rechnungsnummer = Extract("InvoiceId");
                                        vm.Metadata.Rechnungsdatum = Extract("InvoiceDate");
                                        vm.Metadata.Faelligkeitsdatum = Extract("DueDate");
                                        vm.Metadata.Nettobetrag = SanitizeEuroValue(Extract("SubTotal"));
                                        vm.Metadata.Gesamtpreis = SanitizeEuroValue(Extract("TotalAmount"));
                                        vm.Metadata.Steuerbetrag = SanitizeEuroValue(Extract("TotalTax"));
                                        vm.Metadata.IBAN = Extract("IBAN");

                                        // ➕ Optionale Zusatzfelder
                                        vm.Metadata.Adresse ??= Extract("CustomerAddress");
                                        vm.Metadata.Kategorie ??= "Rechnungen";
                                        vm.Metadata.AnsprechPartner ??= Extract("CustomerName");
                                        vm.Metadata.Lieferdatum ??= Extract("ServiceStartDate");
                                        vm.Metadata.Zeitraum ??= $"{Extract("ServiceStartDate")} bis {Extract("ServiceEndDate")}";
                                        vm.Metadata.SteuerNr ??= Extract("VendorTaxId");
                                        vm.Metadata.Beschreibung ??= Extract("PaymentTerms");
                                        vm.Metadata.Zahlungsbedingungen ??= Extract("PaymentTerms");
                                        vm.Metadata.AbsenderAdresse ??= Extract("VendorAddress");
                                    }

                                    // 🧠 2️⃣ Fallback + Ergänzung aus Regex-Service
                                    var metaExtra = OcrMetadataExtractorService.Extract(fullText);
                                    if (metaExtra != null)
                                    {
                                        vm.Metadata.Rechnungsnummer ??= metaExtra.Rechnungsnummer;
                                        vm.Metadata.Rechnungsdatum ??= metaExtra.Rechnungsdatum;
                                        vm.Metadata.Faelligkeitsdatum ??= metaExtra.Faelligkeitsdatum;
                                        vm.Metadata.Lieferdatum ??= metaExtra.Lieferdatum;
                                        vm.Metadata.Zeitraum ??= metaExtra.Zeitraum;
                                        vm.Metadata.Nettobetrag ??= metaExtra.Nettobetrag;
                                        vm.Metadata.Steuerbetrag ??= metaExtra.Steuerbetrag;
                                        vm.Metadata.Gesamtpreis ??= metaExtra.Gesamtpreis;
                                        vm.Metadata.IBAN ??= metaExtra.IBAN;
                                        vm.Metadata.BIC ??= metaExtra.BIC;
                                        vm.Metadata.Adresse ??= metaExtra.Adresse;
                                        vm.Metadata.SteuerNr ??= metaExtra.SteuerNr;
                                        vm.Metadata.AnsprechPartner ??= metaExtra.AnsprechPartner;
                                        vm.Metadata.Email ??= metaExtra.Email;
                                        vm.Metadata.Telefon ??= metaExtra.Telefon;
                                        vm.Metadata.Bankverbindung ??= metaExtra.Bankverbindung;
                                        vm.Metadata.Website ??= metaExtra.Website;
                                        vm.Metadata.Zahlungsbedingungen ??= metaExtra.Zahlungsbedingungen;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"⚠️ OCR Warnung für {file.FileName}: {ex.Message}");
                                    vm.Metadata.Beschreibung = "Metadaten konnten nicht extrahiert werden (Datei wird trotzdem gespeichert).";
                                    vm.Metadata.Titel = Path.GetFileNameWithoutExtension(file.FileName);
                                    await ApplyDetectedMetadataAsync(vm, file.FileName, "");
                                }
                             }
                             // B) TEXT DATEIEN
                             else if (ext == ".txt")
                             {
                                using var reader = new StreamReader(file.OpenReadStream());
                                var content = await reader.ReadToEndAsync();
                                
                                vm.Metadata.Titel = Path.GetFileNameWithoutExtension(file.FileName);
                                vm.Metadata.OCRText = content;
                                vm.Metadata.Beschreibung = "Metadaten aus Textdatei extrahiert";
                                await ApplyDetectedMetadataAsync(vm, file.FileName, content);

                                var metaExtra = OcrMetadataExtractorService.Extract(content);
                                if (metaExtra != null)
                                {
                                    vm.Metadata.Rechnungsnummer = metaExtra.Rechnungsnummer;
                                    vm.Metadata.Rechnungsdatum = metaExtra.Rechnungsdatum;
                                    vm.Metadata.Gesamtpreis = metaExtra.Gesamtpreis;
                                    vm.Metadata.IBAN = metaExtra.IBAN;
                                    vm.Metadata.Faelligkeitsdatum = metaExtra.Faelligkeitsdatum;
                                    vm.Metadata.SteuerNr = metaExtra.SteuerNr;
                                    // ... weitere Zuweisungen nach Bedarf
                                }
                             }
                             // C) WORD (.docx)
                             else if (ext == ".docx")
                             {
                                vm.Metadata.Titel = Path.GetFileNameWithoutExtension(file.FileName);
                                vm.Metadata.Beschreibung = "Word-Dokument erkannt.";
                                try 
                                {
                                    using var ms = new MemoryStream();
                                    await file.CopyToAsync(ms);
                                    using var wordDoc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(ms, false);
                                    var body = wordDoc.MainDocumentPart.Document.Body;
                                    var text = body.InnerText; 
                                    
                                    vm.Metadata.OCRText = text;
                                    vm.Metadata.Beschreibung += " (Text analysiert)";
                                    await ApplyDetectedMetadataAsync(vm, file.FileName, text);
                                    
                                    var metaExtra = OcrMetadataExtractorService.Extract(text);
                                    if (metaExtra != null)
                                    {
                                         vm.Metadata.Rechnungsnummer = metaExtra.Rechnungsnummer;
                                         vm.Metadata.Rechnungsdatum = metaExtra.Rechnungsdatum;
                                         vm.Metadata.Gesamtpreis = metaExtra.Gesamtpreis;
                                         vm.Metadata.IBAN = metaExtra.IBAN;
                                    }
                                }
                                catch
                                {
                                    // Fallback
                                }
                             }
                             // D) FALLBACK (EXCEL, DOC etc.)
                             else 
                             {
                                vm.Metadata.Titel = Path.GetFileNameWithoutExtension(file.FileName);
                                vm.Metadata.Beschreibung = "Dateiformat unterstützt (Metadaten manuell prüfen).";
                                await ApplyDetectedMetadataAsync(vm, file.FileName, "");
                             }

                        }



                        // =======================================================
                        // 3️⃣ TEMPORÄRER UPLOAD (mit korrekter Reihenfolge)
                        // =======================================================
                        Guid dokumentId = Guid.NewGuid();
                        var abtName = "temp";
                        var katName = "analyze";

                        Console.WriteLine($"🧩 Vorbereitung Upload → {file.FileName}");
                        Console.WriteLine($"📁 TEMP Ziel: dokumente/{firma}/{abtName}/{katName}/chunks/{dokumentId}");

                        // ✅ 1. D'abord créer le Dokument-Eintrag
                        var tempDoc = new Dokumente
                        {
                            Id = dokumentId,
                            ApplicationUserId = userId,
                            KundeId = kunde.Id,
                            Dateiname = file.FileName,
                            Kategorie = katName,
                            FileHash = hash,
                            FileSizeBytes = file.Length,
                            HochgeladenAm = DateTime.UtcNow,
                            IsChunked = file.Length > 20 * 1024 * 1024,
                            ObjectPath = $"dokumente/{firma}/{abtName}/{katName}/" +
                                         (file.Length > 20 * 1024 * 1024 ? $"chunks/{dokumentId}/" : file.FileName),
                            Beschreibung = "Temporärer Upload (WebDAV)"
                        };

                        _db.Dokumente.Add(tempDoc);
                        await _db.SaveChangesAsync();
                        Console.WriteLine($"✅ Dokument in DB erstellt (ID: {tempDoc.Id})");

                        // ✅ 2. Ensuite upload réel
                        if (file.Length > 20 * 1024 * 1024)
                        {
                            using var fileStream = file.OpenReadStream();
                            var chunks = await _chunkService.SaveFileAsChunksToWebDavAsync(
                                fileStream,
                                dokumentId,
                                firma,
                                abtName,
                                katName
                            );

                            Console.WriteLine($"✅ Chunked Upload TEMP abgeschlossen ({chunks.Count} Stück).");
                            vm.ObjectPath = $"chunked://{dokumentId}";
                            vm.Metadata.Beschreibung = $"📦 Datei in {chunks.Count} Chunks gespeichert (TEMP WebDAV).";
                        }
                        else
                        {
                            using var fileStream = file.OpenReadStream();
                            string objectPath = await _Webdavstorage.UploadForUserAsync(file, firma, abtName, katName);

                            vm.ObjectPath = objectPath;
                            vm.Metadata.Beschreibung = "📦 Datei erfolgreich TEMP zu WebDAV hochgeladen.";
                            Console.WriteLine($"✅ Normaler Upload TEMP erfolgreich: {objectPath}");
                        }

                        vm.Status = DokumentStatus.Analyzed;
                        vm.Progress = 100;
                    }
                }
                catch (Exception ex)
                {
                    vm.Metadata.Beschreibung = $"❌ Fehler: {ex.Message}";
                    vm.Status = DokumentStatus.Analyzed;
                    Console.WriteLine($"❌ Fehler bei {vm.FileName}: {ex.Message}");
                }

                Dokumente.Add(vm);
            }

            // =======================================================
            // 4️⃣ SERIALISIERUNG
            // =======================================================
            var serialisiert = Dokumente.Select(d => new DokumentSerialisiert
            {
                FileName = d.FileName,
                Metadata = d.Metadata,
                Status = d.Status,
                ObjectPath = d.ObjectPath
            }).ToList();

            DokumenteJson = JsonSerializer.Serialize(serialisiert);
            TempData[nameof(DokumenteJson)] = DokumenteJson;
            TempData.Keep(nameof(DokumenteJson));

            Console.WriteLine($"✅ Analyse abgeschlossen ({Dokumente.Count} Dateien verarbeitet).");
            Console.WriteLine("========================================");

            return RedirectToPage(new { analyzed = true });
        }



        private static string NormalizeSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "allgemein";

            value = value.Trim().Replace(" ", "_");

            foreach (var c in new[] { "/", "\\", "#", "?", "%", "&", ":", "*", "\"", "<", ">", "|" })
                value = value.Replace(c, "_");

            var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder();
            foreach (var ch in normalized)
            {
                var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }

            value = sb.ToString().ToLowerInvariant();

            return string.IsNullOrWhiteSpace(value) ? "allgemein" : value;
        }



        private static string SanitizeEuroValue(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            // Entfernt "EUR", Leerzeichen, Punkt als Tausendertrennzeichen und ersetzt Komma mit Punkt
            var cleaned = input.Replace("EUR", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("€", "")
                               .Replace(" ", "")
                               .Replace(".", "")
                               .Replace(",", ".")
                               .Trim();

            // Validiert ob Zahl, sonst null
            return decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _)
                ? cleaned
                : null;
        }
        private async Task UpsertDokumentIndexAsync(Guid dokumentId, MetadataModel meta, bool save = true)
        {
            var index = await _db.DokumentIndex.FirstOrDefaultAsync(x => x.DokumentId == dokumentId);
            if (index == null)
            {
                index = new DokumentIndex { DokumentId = dokumentId };
                _db.DokumentIndex.Add(index);
            }

            index.Kategorie = meta.Kategorie;
            index.Beschreibung = meta.Beschreibung;
            index.Titel = meta.Titel;
            index.Rechnungsnummer = meta.Rechnungsnummer;
            index.Kundennummer = meta.Kundennummer;
            index.Rechnungsbetrag = decimal.TryParse(meta.Rechnungsbetrag, out var rb) ? rb : null;
            index.Nettobetrag = decimal.TryParse(meta.Nettobetrag, out var nb) ? nb : null;
            index.Gesamtbetrag = decimal.TryParse(meta.Gesamtpreis, out var gp) ? gp : null;
            index.Steuerbetrag = decimal.TryParse(meta.Steuerbetrag, out var sb) ? sb : null;
            index.Rechnungsdatum = DateTime.TryParse(meta.Rechnungsdatum, out var rd) ? rd : null;
            index.Lieferdatum = DateTime.TryParse(meta.Lieferdatum, out var ld) ? ld : null;
            index.Faelligkeitsdatum = DateTime.TryParse(meta.Faelligkeitsdatum, out var fd) ? fd : null;
            index.Zahlungsbedingungen = meta.Zahlungsbedingungen;
            index.lieferart = meta.Lieferart;
            index.ArtikelAnzahl = int.TryParse(meta.ArtikelAnzahl, out var aa) ? aa : null;
            index.Email = meta.Email;
            index.Telefon = meta.Telefon;
            index.Telefax = meta.Telefax;
            index.IBAN = meta.IBAN;
            index.BIC = meta.BIC;
            index.Bankverbindung = meta.Bankverbindung;
            index.SteuerNr = meta.SteuerNr;
            index.UIDNummer = meta.UIDNummer;
            index.Adresse = meta.Adresse;
            index.AbsenderAdresse = meta.AbsenderAdresse;
            index.AnsprechPartner = meta.AnsprechPartner;
            index.Zeitraum = meta.Zeitraum;
            index.Autor = meta.PdfAutor;
            index.Betreff = meta.PdfBetreff;
            index.Schluesselwoerter = meta.PdfSchluesselwoerter;
            index.Website = meta.Website;
            index.OCRText = meta.OCRText;
            index.ObjectPath = meta.ObjectPath;

            if (save)
            {
                await _db.SaveChangesAsync();
            }
        }



        /// <summary>
        /// Détection finale Abteilung + Kategorie (priorité : user > auto-detect > fallback)
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> OnPostSaveAllAsync()
        {
            Console.WriteLine("📥 [START] Speichern aller Dokumente gestartet");

            // 🧩 Validation initiale
            if (!TempData.TryGetValue(nameof(DokumenteJson), out var obj)
                || obj is not string json || string.IsNullOrWhiteSpace(json))
            {
                TempData["Error"] = "⚠️ Keine Dokumente gefunden!";
                return RedirectToPage(new { analyzed = true });
            }

            var list = JsonSerializer.Deserialize<List<DokumentSerialisiert>>(json);
            if (list == null || list.Count == 0)
            {
                TempData["Error"] = "⚠️ Keine Dokumente analysiert!";
                return RedirectToPage(new { analyzed = true });
            }

            // 🧠 Contexte utilisateur
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _db.Users.FindAsync(userId);
            var firma = user?.FirmenName?.Trim().ToLowerInvariant();
            var kunde = await ResolveKundeForUserAsync(user);

            if (string.IsNullOrWhiteSpace(firma) || kunde == null)
            {
                TempData["Error"] = "❌ Benutzer/Kunde ungültig.";
                return RedirectToPage(new { analyzed = true });
            }

            Console.WriteLine($"👤 Benutzer: {user?.UserName}, Firma: {firma}");
            Console.WriteLine($"📊 {list.Count} Dokumente werden gespeichert (inkl. Chunk-Moves)");

            var savedDocs = new List<object>();
            int counter = 0;
            string webDavBaseUrl = "https://mikroplus.dscloud.me:5006/DmsDaten";

            // === Dossier temporaire commun
            string tempAnalyzeFolder = $"dokumente/{firma}/temp/analyze";

            foreach (var meta in list)
            {
                _db.ChangeTracker.Clear();

                if (meta.Status != DokumentStatus.Analyzed)
                {
                    Console.WriteLine($"⚠️ [Skip] {meta.FileName} → Status={meta.Status}");
                    continue;
                }

                var m = meta.Metadata ?? new MetadataModel();
                m.Kategorie = m.Kategorie?.Trim().ToLowerInvariant() ?? "allgemein";

                // 🔹 Catégorie & Abteilung
                int? abteilungId = null;
                string abtName = "allgemein";

                string katKey = $"Dokumente[{counter}].Metadata.Kategorie";
                string abtKey = $"Dokumente[{counter}].Metadata.AbteilungId";

                if (Request.Form.ContainsKey(katKey))
                {
                    string katValue = Request.Form[katKey].ToString();
                    if (!string.IsNullOrWhiteSpace(katValue))
                        m.Kategorie = katValue.Trim().ToLowerInvariant();
                }

                if (!User.IsInRole("Admin") && !User.IsInRole("SuperAdmin"))
                {
                    abteilungId = user.AbteilungId;
                    var abt = await _db.Abteilungen.FindAsync(abteilungId);
                    abtName = abt?.Name?.ToLowerInvariant() ?? "allgemein";
                }
                else if (Request.Form.ContainsKey(abtKey))
                {
                    string abtValue = Request.Form[abtKey].ToString();
                    if (int.TryParse(abtValue, out var abtId))
                    {
                        var abt = await _db.Abteilungen.FindAsync(abtId);
                        abteilungId = abt?.Id;
                        abtName = abt?.Name?.ToLowerInvariant() ?? "allgemein";
                    }
                }

                if (abteilungId == null)
                {
                    var defaultAbt = await _db.Abteilungen.FirstOrDefaultAsync(a => a.Name.ToLower() == "allgemein");
                    abteilungId = defaultAbt?.Id;
                    abtName = defaultAbt?.Name?.ToLowerInvariant() ?? "allgemein";
                }

                Console.WriteLine($"📂 Ziel: {abtName}/{m.Kategorie}");

                bool isChunked = meta.ObjectPath?.StartsWith("chunked://") == true;
                Guid docId = isChunked
                    ? Guid.Parse(meta.ObjectPath.Replace("chunked://", ""))
                    : Guid.NewGuid();

                var (finalPath, finalAbteilungId) = DocumentPathHelper.BuildFinalPath(
                    firma, meta.FileName, m.Kategorie, abteilungId, abtName);

                string oldPath = isChunked
                    ? $"{tempAnalyzeFolder}/chunks/{docId}"
                    : $"{tempAnalyzeFolder}/{meta.FileName}";

                string newPath = isChunked
                    ? $"dokumente/{firma}/{abtName}/{m.Kategorie}/chunks/{docId}"
                    : finalPath;

                // ========================== MOVE FILES ==========================
                try
                {
                    Console.WriteLine($"🚚 Verschiebe von TEMP → {newPath}");
                    await _Webdavstorage.EnsureFolderTreeExistsAsync(newPath);

                    if (isChunked)
                    {
                        var chunkFiles = await _db.DokumentChunks
                            .Where(c => c.DokumentId == docId)
                            .ToListAsync();

                        foreach (var chunk in chunkFiles)
                        {
                            var oldChunkPath = chunk.FirebasePath;
                            var fileName = Path.GetFileName(oldChunkPath);
                            var newChunkPath = $"{newPath}/{fileName}";

                            Console.WriteLine($"➡️ Chunk {fileName} verschieben...");
                            var moved = await _Webdavstorage.MoveAsync(oldChunkPath, newChunkPath);
                            if (moved)
                            {
                                chunk.FirebasePath = newChunkPath;
                                _db.DokumentChunks.Update(chunk);
                            }
                        }

                        await _db.SaveChangesAsync();
                    }
                    else
                    {
                        await _Webdavstorage.MoveAsync(oldPath, newPath);
                    }

                    Console.WriteLine($"✅ Dateien verschoben nach {newPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Fehler beim Verschieben: {ex.Message}");
                }

                // ========================== DB SAVE ==========================
                var objectPath = isChunked
                    ? $"{webDavBaseUrl}/dokumente/{firma}/{abtName}/{m.Kategorie}/chunks/{docId}"
                    : $"{webDavBaseUrl}/{newPath}";

                var existingDoc = await _db.Dokumente
                    .Include(d => d.MetadatenObjekt)
                    .FirstOrDefaultAsync(d => d.FileHash == m.FileHash);

                if (existingDoc != null)
                {
                    Console.WriteLine($"♻️ Update existierendes Dokument {existingDoc.Dateiname}");
                    existingDoc.ObjectPath = objectPath;
                    existingDoc.Dateipfad = finalPath;
                    existingDoc.Kategorie = m.Kategorie;
                    existingDoc.AbteilungId = finalAbteilungId;
                    existingDoc.dtStatus = DokumentStatus.Fertig;

                    var metaEntity = await DocumentPathHelper.CreateFullMetadataFromModelAsync(_db, existingDoc, m, "Update");
                    existingDoc.MetadatenId = metaEntity.Id;

                    _db.Dokumente.Update(existingDoc);
                    await _db.SaveChangesAsync();
                    counter++;
                    continue;
                }

                // 🆕 Neues Dokument anlegen
                var doc = new Dokumente
                {
                    Id = docId,
                    ApplicationUserId = userId,
                    KundeId = kunde.Id,
                    Dateiname = meta.FileName,
                    Dateipfad = finalPath,
                    ObjectPath = objectPath,
                    HochgeladenAm = DateTime.UtcNow,
                    Kategorie = m.Kategorie,
                    AbteilungId = finalAbteilungId,
                    DokumentStatus = Status.Aktiv,
                    dtStatus = DokumentStatus.Fertig,
                    IsIndexed = true,
                    FileHash = m.FileHash,
                    IsChunked = isChunked
                };

                var metaEntityNew = await DocumentPathHelper.CreateFullMetadataFromModelAsync(_db, doc, m, "Upload");

                doc.MetadatenId = metaEntityNew.Id;
                doc.MetadatenObjekt = metaEntityNew;

                _db.Dokumente.Add(doc);
                await _db.SaveChangesAsync();

                await UpsertDokumentIndexAsync(doc.Id, m);
                await _audit.EnregistrerAsync("Dokument gespeichert", userId, doc.Id);

                savedDocs.Add(new { doc.Id, meta.FileName });
                counter++;
            }

            try
            {
                if (await _Webdavstorage.IsFolderEmptyAsync(tempAnalyzeFolder))
                {
                    await _Webdavstorage.DeleteFolderIfExistsAsync(tempAnalyzeFolder);
                    Console.WriteLine($"🧹 Dossier temporaire supprimé: {tempAnalyzeFolder}");
                }
                else
                {
                    Console.WriteLine($"⚠️ Dossier temporaire non vide → suppression ignorée: {tempAnalyzeFolder}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Erreur lors de la suppression du dossier temporaire: {ex.Message}");
            }

            // === Résumé final
            TempData["Success"] = $"✅ {counter} Dokument(e) erfolgreich gespeichert.";
            TempData["SavedDocuments"] = JsonSerializer.Serialize(savedDocs);
            TempData.Remove(nameof(DokumenteJson));

            Console.WriteLine($"🏁 [END] {counter} Dokumente erfolgreich gespeichert.");
            return RedirectToPage(new { analyzed = true });
        }


        private async Task UpdateDocumentPathInDb(Guid dokumentId, string newObjectPath, string newFolder)
        {
            var doc = await _db.Dokumente.FirstOrDefaultAsync(d => d.Id == dokumentId);
            if (doc == null)
            {
                Console.WriteLine($"⚠️ Dokument {dokumentId} nicht gefunden für Pfadaktualisierung.");
                return;
            }

            doc.ObjectPath = newObjectPath;
            doc.Dateipfad = newFolder;
            _db.Dokumente.Update(doc);
            await _db.SaveChangesAsync();

            Console.WriteLine($"✅ ObjectPath/Dateipfad aktualisiert → {newObjectPath}");
        }


        private void ApplyMetadataToDocument(Dokumente doc, MetadataModel m)
        {
            if (doc == null || m == null)
                return;

            // 🧠 Sicherstellen, dass Metadatenobjekt existiert
            if (doc.MetadatenObjekt == null)
                doc.MetadatenObjekt = new Metadaten();

            var meta = doc.MetadatenObjekt;

            // 🔹 Textfelder
            meta.Titel = m.Titel;
            meta.Beschreibung = m.Beschreibung;
            meta.Kategorie = m.Kategorie ?? meta.Kategorie;
            meta.Stichworte = m.PdfSchluesselwoerter;

            // 🔹 Rechnungsdaten
            meta.Rechnungsnummer = m.Rechnungsnummer;
            meta.Kundennummer = m.Kundennummer;

            meta.Rechnungsbetrag = decimal.TryParse(m.Rechnungsbetrag, out var rb) ? rb : null;
            meta.Nettobetrag = decimal.TryParse(m.Nettobetrag, out var nb) ? nb : null;
            meta.Gesamtpreis = decimal.TryParse(m.Gesamtpreis, out var gp) ? gp : null;
            meta.Steuerbetrag = decimal.TryParse(m.Steuerbetrag, out var sb) ? sb : null;

            meta.Rechnungsdatum = DateTime.TryParse(m.Rechnungsdatum, out var rd) ? rd : null;
            meta.Lieferdatum = DateTime.TryParse(m.Lieferdatum, out var ld) ? ld : null;
            meta.Faelligkeitsdatum = DateTime.TryParse(m.Faelligkeitsdatum, out var fd) ? fd : null;

            meta.Zahlungsbedingungen = m.Zahlungsbedingungen;
            meta.Lieferart = m.Lieferart;

            meta.ArtikelAnzahl = int.TryParse(m.ArtikelAnzahl, out var aa) ? aa : null;

            // 🔹 Kontakt & Bankdaten
            meta.Email = m.Email;
            meta.Telefon = m.Telefon;
            meta.Telefax = m.Telefax;
            meta.IBAN = m.IBAN;
            meta.BIC = m.BIC;
            meta.Bankverbindung = m.Bankverbindung;
            meta.SteuerNr = m.SteuerNr;
            meta.UIDNummer = m.UIDNummer;

            // 🔹 Adresse & Personeninfos
            meta.Adresse = m.Adresse;
            meta.AbsenderAdresse = m.AbsenderAdresse;
            meta.AnsprechPartner = m.AnsprechPartner;
            meta.Zeitraum = m.Zeitraum;

            // 🔹 PDF-Infos
            meta.PdfAutor = m.PdfAutor;
            meta.PdfBetreff = m.PdfBetreff;
            meta.PdfSchluesselwoerter = m.PdfSchluesselwoerter;
            meta.Website = m.Website;
            meta.OCRText = m.OCRText;

            // 🔹 Dokument-Hauptfelder aktualisieren (nur bei Bedarf)
            doc.Kategorie = meta.Kategorie;
            doc.Beschreibung = meta.Beschreibung;
            doc.Titel = meta.Titel;
        }

        private async Task<Kunden?> ResolveKundeForUserAsync(ApplicationUser user)
        {
            // Cas 1 : Normaler User → CreatedByAdminId nutzen
            if (!User.IsInRole("Admin") && !User.IsInRole("SuperAdmin"))
            {
                if (user.CreatedByAdminId != null)
                {
                    var kundenBenutzer = await _db.KundeBenutzer
                        .Include(k => k.Kunden)
                        .FirstOrDefaultAsync(k => k.ApplicationUserId == user.CreatedByAdminId);

                    return kundenBenutzer?.Kunden;
                }
                return null;
            }

            // Cas 2 : Admin / SuperAdmin → über FirmenName
            if (!string.IsNullOrWhiteSpace(user.FirmenName))
            {
                return await _db.Kunden
                    .FirstOrDefaultAsync(k => k.FirmenName.ToLower() == user.FirmenName.ToLower());
            }

            return null;
        }

        public async Task<IActionResult> OnPostSaveAsync(int index)
        {
            Console.WriteLine($"💾 [START] Speichern einzelnes Dokument index={index}");

            if (!TempData.TryGetValue(nameof(DokumenteJson), out var obj)
                || obj is not string json || string.IsNullOrWhiteSpace(json))
            {
                TempData["Error"] = "⚠️ Keine Dokumente gefunden!";
                return RedirectToPage(new { analyzed = true });
            }

            var list = JsonSerializer.Deserialize<List<DokumentSerialisiert>>(json);
            if (list == null || list.Count <= index)
            {
                TempData["Error"] = "⚠️ Dokument nicht gefunden!";
                return RedirectToPage(new { analyzed = true });
            }

            var meta = list[index];
            if (meta.Status != DokumentStatus.Analyzed)
            {
                TempData["Error"] = "⚠️ Dokument nicht analysiert!";
                return RedirectToPage(new { analyzed = true });
            }

            var m = meta.Metadata;
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _db.Users.FindAsync(userId);
            var firma = user?.FirmenName?.Trim()?.ToLowerInvariant();
            var kunde = await ResolveKundeForUserAsync(user);

            if (string.IsNullOrWhiteSpace(firma) || kunde == null)
            {
                TempData["Error"] = "❌ Benutzer/Kunde ungültig.";
                return RedirectToPage(new { analyzed = true });
            }

            // ======================== Abteilung ========================
            int? abteilungId = null;
            string abtName = "allgemein";

            if (!User.IsInRole("Admin") && !User.IsInRole("SuperAdmin"))
            {
                abteilungId = user.AbteilungId;
                var abt = await _db.Abteilungen.FindAsync(abteilungId);
                abtName = abt?.Name?.ToLowerInvariant() ?? "allgemein";
            }
            else
            {
                string abtKey = $"Dokumente[{index}].Metadata.AbteilungId";
                if (Request.Form.ContainsKey(abtKey))
                {
                    var abtValue = Request.Form[abtKey].ToString();
                    if (int.TryParse(abtValue, out var abtId))
                    {
                        var abt = await _db.Abteilungen.FindAsync(abtId);
                        abteilungId = abt?.Id;
                        abtName = abt?.Name?.ToLowerInvariant() ?? "allgemein";
                        Console.WriteLine($"✅ Abteilung gewählt: {abtName}");
                    }
                }

                if (abteilungId == null)
                {
                    TempData["Error"] = "❌ Bitte wählen Sie eine Abteilung aus.";
                    return RedirectToPage(new { analyzed = true });
                }
            }

            // ======================== Kategorie ========================
            string katKey = $"Dokumente[{index}].Metadata.Kategorie";
            string katValue = Request.Form.ContainsKey(katKey)
                ? Request.Form[katKey].ToString()
                : null;

            m.Kategorie = !string.IsNullOrWhiteSpace(katValue)
                ? katValue.Trim().ToLowerInvariant()
                : m.Kategorie?.Trim()?.ToLowerInvariant() ?? "allgemein";

            // ======================== Pfade vorbereiten ========================
            bool isChunked = meta.ObjectPath?.StartsWith("chunked://") == true;
            Guid docId = isChunked
                ? Guid.Parse(meta.ObjectPath.Replace("chunked://", ""))
                : Guid.NewGuid();

            var (finalPath, finalAbteilungId) = DocumentPathHelper.BuildFinalPath(
                firma, meta.FileName, m.Kategorie, abteilungId, abtName);

            string newFolder = isChunked
                ? $"dokumente/{firma}/{abtName}/{m.Kategorie}/chunks/{docId}"
                : finalPath;

            await _Webdavstorage.EnsureFolderTreeExistsAsync(newFolder);

            // ======================== Datei verschieben ========================
            try
            {
                Console.WriteLine($"🚚 Verschiebe von TEMP → {newFolder}");
                if (isChunked)
                {
                    var chunkFiles = await _db.DokumentChunks
                        .Where(c => c.DokumentId == docId)
                        .ToListAsync();

                    foreach (var chunk in chunkFiles)
                    {
                        var oldPath = chunk.FirebasePath;
                        var fileName = Path.GetFileName(oldPath);
                        var newFilePath = $"{newFolder}/{fileName}";
                        var moved = await _Webdavstorage.MoveAsync(oldPath, newFilePath);
                        if (moved)
                        {
                            chunk.FirebasePath = newFilePath;
                            _db.DokumentChunks.Update(chunk);
                        }
                    }
                    await _db.SaveChangesAsync();
                }
                else
                {
                    // Entferne BaseUrl falls vorhanden
                    string sourcePath = meta.ObjectPath
                        .Replace("https://mikroplus.dscloud.me:5006/DmsDaten/", "")
                        .Replace("https://mikroplus.dscloud.me:5006/DmsDaten", "")
                        .TrimStart('/');

                    Console.WriteLine($"[MOVE] {sourcePath} → {newFolder}");
                    await _Webdavstorage.MoveAsync(sourcePath, newFolder);

                }

                await _Webdavstorage.DeleteFolderIfExistsAsync($"dokumente/{firma}/temp/analyze");
                Console.WriteLine($"✅ Datei verschoben: {meta.FileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Verschieben: {ex.Message}");
            }

            // ======================== DB-Speicherung ========================
            string webDavBaseUrl = "https://mikroplus.dscloud.me:5006/DmsDaten";
            var objectPath = isChunked
                ? $"{webDavBaseUrl}/dokumente/{firma}/{abtName}/{m.Kategorie}/chunks/{docId}"
                : $"{webDavBaseUrl}/{newFolder}";

            // Prüfen, ob bereits vorhanden
            var existingDoc = await _db.Dokumente
                .Include(d => d.MetadatenObjekt)
                .FirstOrDefaultAsync(d => d.FileHash == m.FileHash);

            if (existingDoc != null)
            {
                Console.WriteLine($"♻️ Update existierendes Dokument: {existingDoc.Dateiname}");
                existingDoc.ObjectPath = objectPath;
                existingDoc.Dateipfad = finalPath;
                existingDoc.Kategorie = m.Kategorie;
                existingDoc.AbteilungId = finalAbteilungId;
                existingDoc.dtStatus = DokumentStatus.Fertig;

                var metaEntity = await DocumentPathHelper.CreateFullMetadataFromModelAsync(_db, existingDoc, m, "Update");
                existingDoc.MetadatenId = metaEntity.Id;
                _db.Dokumente.Update(existingDoc);
                await _db.SaveChangesAsync();

                await UpsertDokumentIndexAsync(existingDoc.Id, m);
                await _audit.EnregistrerAsync("Dokument aktualisiert", userId, existingDoc.Id);
                TempData["Success"] = $"✅ Dokument aktualisiert: {existingDoc.Dateiname}";
                return RedirectToPage(new { analyzed = true });
            }

            // Neues Dokument anlegen
            var doc = new Dokumente
            {
                Id = docId,
                ApplicationUserId = userId,
                KundeId = kunde.Id,
                Dateiname = meta.FileName,
                Dateipfad = finalPath,
                ObjectPath = objectPath,
                HochgeladenAm = DateTime.UtcNow,
                Kategorie = m.Kategorie,
                AbteilungId = finalAbteilungId,
                DokumentStatus = Status.Aktiv,
                dtStatus = DokumentStatus.Fertig,
                IsIndexed = true,
                FileHash = m.FileHash,
                IsChunked = isChunked
            };

            var metaEntityNew = await DocumentPathHelper.CreateFullMetadataFromModelAsync(_db, doc, m, "Upload");
            doc.MetadatenId = metaEntityNew.Id;
            doc.MetadatenObjekt = metaEntityNew;

            _db.Dokumente.Add(doc);
            await _db.SaveChangesAsync();

            await UpsertDokumentIndexAsync(doc.Id, m);
            await _audit.EnregistrerAsync("Dokument gespeichert", userId, doc.Id);

            TempData["Success"] = $"✅ Neues Dokument gespeichert: {doc.Dateiname}";
            Console.WriteLine($"✅ Neues Dokument gespeichert: {doc.Dateiname}");

            return RedirectToPage(new { analyzed = true });
        }


        public async Task<IActionResult> OnPostRemoveAsync(int index)
        {
            if (!TempData.TryGetValue(nameof(DokumenteJson), out var obj)
                || obj is not string json || string.IsNullOrWhiteSpace(json))
            {
                TempData["Error"] = "⚠️ Keine Dokumente gefunden!";
                return RedirectToPage(new { analyzed = true });
            }
            var list = JsonSerializer.Deserialize<List<DokumentSerialisiert>>(json);
            if (list == null || list.Count <= index)
            {
                TempData["Error"] = "⚠️ Dokument nicht gefunden!";
                return RedirectToPage(new { analyzed = true });
            }
            var removed = list[index];
            list.RemoveAt(index);

            // Liste neu serialisieren und speichern
            TempData[nameof(DokumenteJson)] = JsonSerializer.Serialize(list);
            TempData["Success"] = $"❌ Dokument gelöscht: {removed.FileName}";
            TempData.Keep(nameof(DokumenteJson));
            return RedirectToPage(new { analyzed = true });
        }

        private async Task ApplyDetectedMetadataAsync(DokumentViewModel vm, string fileName, string textContent)
        {
            var (erkannteKategorie, erkannteAbteilung) = DetectKategorieUndAbteilung(fileName, textContent);
            vm.Metadata.Kategorie = erkannteKategorie ?? "allgemein";

            if (!string.IsNullOrWhiteSpace(erkannteAbteilung))
            {
                var abt = await _db.Abteilungen
                    .FirstOrDefaultAsync(a => a.Name.ToLower().Trim() == erkannteAbteilung.ToLower().Trim());
                if (abt != null)
                {
                    vm.Metadata.AbteilungId = abt.Id;
                    vm.Metadata.AbteilungName = abt.Name;
                }
            }
        }

        private (string Kategorie, string Abteilung) DetectKategorieUndAbteilung(string fileName, string ocrText)
        {
            if (string.IsNullOrWhiteSpace(fileName) && string.IsNullOrWhiteSpace(ocrText))
                return ("Unbekannt", "Allgemein");

            string content = (fileName + " " + ocrText).ToLowerInvariant();

            // ================================
            // 📚 Règles (en Allemand uniquement)
            // ================================
            var rules = new List<(string Kategorie, string Abteilung, string[] Keywords)>
    {
        // 👨‍💼 HR
        ("Gehaltsabrechnungen", "HR", new[] { "gehaltsabrechnung", "lohnabrechnung" }),
        ("Arbeitsverträge", "HR", new[] { "arbeitsvertrag" }),
        ("Mitarbeiterakten", "HR", new[] { "personalakte" }),
        ("Arbeitszeugnisse", "HR", new[] { "arbeitszeugnis" }),
        ("Schulungsunterlagen", "HR", new[] { "schulung", "weiterbildung" }),

        // 🎓 Studium / Ausbildung
        ("Diplome", "Studium", new[] { "diplom", "abschlusszeugnis" }),
        ("Bachelor", "Studium", new[] { "bachelor" }),
        ("Master", "Studium", new[] { "master" }),
        ("Zertifikate", "Studium", new[] { "zertifikat", "bescheinigung", "urkunde" }),
        ("Notenübersicht", "Studium", new[] { "notenübersicht", "transkript" }),
        ("Abschlussarbeiten", "Studium", new[] { "abschlussarbeit", "thesis", "dissertation" }),

        // 💰 Finanzen
        ("Rechnungen", "Finanzen", new[] { "rechnung", "invoice" }),
        ("Gutschriften", "Finanzen", new[] { "gutschrift" }),
        ("Steuerunterlagen", "Finanzen", new[] { "steuer", "umsatzsteuer", "einkommensteuer" }),
        ("Bankunterlagen", "Finanzen", new[] { "kontoauszug", "bank" }),
        ("Jahresabschlüsse", "Finanzen", new[] { "jahresabschluss", "bilanz" }),
        ("Budgets", "Finanzen", new[] { "budget", "planung" }),
        ("Spesenabrechnungen", "Finanzen", new[] { "spesenabrechnung" }),
        ("Finanzberichte", "Finanzen", new[] { "finanzbericht" }),

        // 📑 Recht
        ("Verträge", "Recht", new[] { "vertrag" }),
        ("Genehmigungen", "Recht", new[] { "genehmigung", "bewilligung" }),
        ("Compliance", "Recht", new[] { "compliance" }),
        ("Rechtsstreitigkeiten", "Recht", new[] { "klage", "prozess", "gericht" }),

        // 🛒 Einkauf
        ("Bestellungen", "Einkauf", new[] { "bestellung" }),
        ("Angebote", "Einkauf", new[] { "angebot", "offerte" }),
        ("Lieferverträge", "Einkauf", new[] { "liefervertrag" }),
        ("Lieferscheine", "Einkauf", new[] { "lieferschein" }),

        // 💼 Verkauf
        ("Kundenaufträge", "Verkauf", new[] { "kundenauftrag" }),
        ("Sales Reports", "Verkauf", new[] { "umsatzbericht" }),

        // 🚚 Logistik
        ("Frachtbriefe", "Logistik", new[] { "frachtbrief" }),
        ("Zolldokumente", "Logistik", new[] { "zoll" }),
        ("Inventar", "Logistik", new[] { "inventar" }),

        // 🛠️ IT / Support
        ("Technische Zeichnungen", "Technik", new[] { "zeichnung", "konstruktionsplan" }),
        ("Handbücher", "Support", new[] { "handbuch" }),
        ("Softwaredokumentation", "IT", new[] { "dokumentation", "benutzerhandbuch" }),
        ("Lizenzen", "IT", new[] { "lizenz" }),
        ("IT Audits", "IT", new[] { "it audit", "sicherheitsaudit" }),

        // 📊 Management
        ("Projektpläne", "Projektmanagement", new[] { "projektplan" }),
        ("Projektberichte", "Projektmanagement", new[] { "projektbericht" }),
        ("Protokolle", "Management", new[] { "protokoll" }),
        ("KPI Reports", "Management", new[] { "kpi", "kennzahlen" }),

        // 📣 Marketing
        ("Marketingunterlagen", "Marketing", new[] { "marketing", "kampagne", "werbung" }),
        ("Broschüren", "Marketing", new[] { "broschüre", "flyer" }),
        ("Pressemitteilungen", "Marketing", new[] { "pressemitteilung" }),

        // ⚙️ Qualität
        ("ISO-Zertifikate", "Qualität", new[] { "iso", "zertifikat" }),
        ("Qualitätsberichte", "Qualität", new[] { "qualitätsbericht" }),
        ("Audits", "Qualität", new[] { "auditplan" }),

        // 🏢 Verwaltung
        ("Memos", "Verwaltung", new[] { "memo", "notiz" }),
        ("Richtlinien", "Verwaltung", new[] { "richtlinie" }),
        ("Allgemeine Dokumente", "Allgemein", new[] { "divers", "misc" }),
    };

            // ================================
            // 📊 Scoring intelligent
            // ================================
            var bestMatch = ("Unbekannt", "Allgemein");
            int bestScore = 0;

            foreach (var rule in rules)
            {
                int score = rule.Keywords.Count(k =>
                    Regex.IsMatch(content, $@"(?<![a-z0-9]){Regex.Escape(k)}(?![a-z0-9])", RegexOptions.IgnoreCase));

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = (rule.Kategorie, rule.Abteilung);
                }
            }

            return bestMatch;
        }

        private async Task<bool> HasAccessToFolderAsync(ApplicationUser user, string targetFolder)
        {
            var claims = await _db.UserClaims
                .Where(c => c.UserId == user.Id && c.ClaimType == "FolderAccess")
                .Select(c => c.ClaimValue)
                .ToListAsync();

            // 🔹 Wenn KEINE Claims → Default = Vollzugriff
            if (claims == null || claims.Count == 0)
                return true;

            // 🔹 Prüfen ob Zielordner von den Claims abgedeckt ist
            return claims.Any(c =>
                targetFolder.StartsWith(c, StringComparison.OrdinalIgnoreCase));
        }

        public static string GetBadgeClass(DokumentStatus status) => status switch
        {
            DokumentStatus.Analyzed => "info",
            DokumentStatus.Fertig => "success",
            DokumentStatus.Pending => "secondary",
            _ => "dark"
        };


    }
    public class DokumentSerialisiert
    {
        public Guid? Id { get; set; }
        public string FileName { get; set; }
        public MetadataModel Metadata { get; set; }
        public DokumentStatus Status { get; set; }
        public string ObjectPath { get; set; }
        public DateTime HochgeladenAm { get; set; }
        public bool IsGrossDoc { get; set; }
        public string AbteilungName { get; set; }
        public int? AbteilungId { get; set; }
    }

}
