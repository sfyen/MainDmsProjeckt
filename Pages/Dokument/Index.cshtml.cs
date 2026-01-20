using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using DinkToPdf;
using DinkToPdf.Contracts;
using DmsProjeckt.Controllers;
using DmsProjeckt.Data;
using DmsProjeckt.Helpers;
using DmsProjeckt.Service;
using DmsProjeckt.Services;
using DocumentFormat.OpenXml.InkML;
using DocumentFormat.OpenXml.Office2016.Drawing.ChartDrawing;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Firebase.Database;
using Google.Api;
using Google.Cloud.Storage.V1;
using iText.Commons.Actions.Contexts;
using iText.Kernel.Utils.Objectpathitems;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using OfficeOpenXml.Table;
using Org.BouncyCastle.Ocsp;
using System;
using System.ComponentModel.DataAnnotations.Schema;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using static DmsProjeckt.Pages.Dokument.AlleVersionenModel;
using static DmsProjeckt.Pages.Tests.UploadMultiModel;
using DrawingColor = System.Drawing.Color;

namespace DmsProjeckt.Pages.Dokument
{
    [IgnoreAntiforgeryToken]
    // [Authorize(Roles = "Admin,SuperAdmin")]
    public class IndexModel : PageModel
    {
        // ===========================
        //    Services / Constructor
        // ===========================
        private readonly ApplicationDbContext _db;
        private readonly WebDavStorageService _WebDav ;
        private readonly IConverter _pdfConverter;
        private readonly IRazorViewToStringRenderer _viewRenderer;
        private readonly ILogger<IndexModel> _logger;
        private readonly AuditLogDokumentService _auditLogDokumentService;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly EmailService _emailService;
        private readonly DbContextOptions<ApplicationDbContext> _dbOptions;
        private readonly ChunkService _chunkService;
        public List<Abteilung> AlleAbteilungen { get; set; }


        public IndexModel(
            ApplicationDbContext db,
            IConverter pdfConverter,
            IRazorViewToStringRenderer viewRenderer,
            UserManager<ApplicationUser> userManager,
            WebDavStorageService WebDav,
            ILogger<IndexModel> logger,
            AuditLogDokumentService auditLogDokumentService,
            EmailService emailService,
            DbContextOptions<ApplicationDbContext> dbOptions,
            ChunkService chunkService)
        {
            _db = db;
            _pdfConverter = pdfConverter;
            _viewRenderer = viewRenderer;
            _WebDav = WebDav;
            _userManager = userManager;
            _logger = logger;
            _auditLogDokumentService = auditLogDokumentService;
            _emailService = emailService;
            _dbOptions = dbOptions;
            _chunkService = chunkService;
        }

        // ===========================
        //     UI & Filter Bindings
        // ===========================
        [BindProperty(SupportsGet = true)] public string Typ { get; set; }
        [BindProperty(SupportsGet = true)] public string Kategorie { get; set; }
        [BindProperty(SupportsGet = true)] public string Status { get; set; }
        [BindProperty(SupportsGet = true)] public string Benutzer { get; set; }
        [BindProperty(SupportsGet = true)] public string BenutzerId { get; set; }
        [BindProperty(SupportsGet = true)] public string Dateiname { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? Von { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? Bis { get; set; }
        [BindProperty(SupportsGet = true)] public string Rechnungsnummer { get; set; }
        [BindProperty(SupportsGet = true)] public string Kundennummer { get; set; }
        [BindProperty(SupportsGet = true)] public string PdfAutor { get; set; }
        [BindProperty(SupportsGet = true)] public string OCRText { get; set; }
        [BindProperty(SupportsGet = true)] public string Query { get; set; }
        [BindProperty(SupportsGet = true)] public string? SelectedFolder { get; set; }
        [BindProperty(SupportsGet = false)] public string NewFolder { get; set; }
        [NotMapped] public string Unterschrieben { get; set; }

        public int PageSize { get; set; } = 20;
        public int PageNumber { get; set; } = 1;
        public int TotalCount { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);

        // ===========================
        //     Data Models/Listen
        // ===========================
        public List<Dokumente> DokumentListe { get; set; } = new();
        public List<Dokumente> GefundeneDokumente { get; set; } = new();
        public List<Dokumente> SignierteDokumente { get; set; }
        public List<Dokumente> NichtSignierteDokumente { get; set; }
        public List<BenutzerMetadaten> MetaListe { get; set; }
        public List<FolderItem> FolderListe { get; set; } = new();
        public List<AuditLogDokument> AuditLogs { get; set; } = new();
        public List<string> TypListe { get; set; } = new();
        public List<string> AlleKategorien { get; set; } = new();
        public List<(string Name, string Path)> AlleOrdner { get; set; } = new();
        public List<ApplicationUser> AlleBenutzer { get; set; } = new();
        public List<DmsFolder> ExplorerTree { get; set; } = new();
        public Dictionary<Guid, int> DokumentVersionenMap { get; set; } = new();
        // public string Firma { get; set; }
        public bool IsVersion { get; set; } = false;
        public string RowCssClass { get; set; }


        [BindProperty] public List<IFormFile> Files { get; set; } = new();
        public List<DokumentIndex> IndexListe { get; set; }
        public List<Guid> DokumenteAvecLogs { get; set; } = new();

        public DmsFolder RootFolder { get; set; }


        public async Task<IActionResult> OnPostUploadAsync(IFormFile uploadedFile, Dokumente dokument)
        {
            if (uploadedFile == null || uploadedFile.Length == 0)
            {
                TempData["Error"] = "⚠️ Keine Datei ausgewählt.";
                return RedirectToPage();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _userManager.FindByIdAsync(userId);

            if (user == null)
            {
                TempData["Error"] = "⚠️ Benutzer nicht gefunden.";
                return RedirectToPage();
            }

            var firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";

            var category = !string.IsNullOrWhiteSpace(dokument.Kategorie)
                ? dokument.Kategorie.Trim()
                : DetectCategoryFromFileName(uploadedFile.FileName);

            if (string.IsNullOrWhiteSpace(category))
                category = "ohne_kategorie";

            var categoryFolderPath = $"dokumente/{firma}/{category}/";

            // 🔹 Récupère la liste des sous-dossiers existants
            var existingFolders = await _WebDav.ListFoldersAsync($"dokumente/{firma}/");

            // ✅ Compare directement les chaînes (pas .Name)
            if (!existingFolders.Any(f => string.Equals(f, category, StringComparison.OrdinalIgnoreCase)))
            {
                await _WebDav.EnsureFolderTreeExistsAsync(categoryFolderPath);
            }

            var fileName = Path.GetFileName(uploadedFile.FileName);
            var fullPath = $"{categoryFolderPath}{fileName}";

            using var stream = uploadedFile.OpenReadStream();
            await _WebDav.UploadStreamAsync(stream, fullPath, uploadedFile.ContentType);

            // 🔹 Sauvegarde dans la base
            dokument.Id = Guid.NewGuid();
            dokument.ApplicationUserId = userId;
            dokument.Dateiname = fileName;
            dokument.Kategorie = category;
            dokument.Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{fullPath}";
            dokument.ObjectPath = fullPath;
            dokument.HochgeladenAm = DateTime.UtcNow;
            dokument.IsVersion = false;
            dokument.OriginalId = Guid.Empty;

            _db.Dokumente.Add(dokument);
            await _db.SaveChangesAsync();

            TempData["Success"] = "✅ Datei erfolgreich hochgeladen!";
            return RedirectToPage();
        }


        private string DetectCategoryFromFileName(string filename)
        {
            filename = filename.ToLowerInvariant();

            if (filename.Contains("rechnung") || filename.Contains("invoice") || filename.Contains("facture"))
                return "rechnungen";
            if (filename.Contains("angebot") || filename.Contains("offre") || filename.Contains("offer"))
                return "angebote";
            if (filename.Contains("bestellung") || filename.Contains("order") || filename.Contains("purchase"))
                return "bestellungen";
            if (filename.Contains("quittung") || filename.Contains("receipt"))
                return "quittungen";
            if (filename.Contains("gebühr") || filename.Contains("gebuehr") || filename.Contains("fee"))
                return "gebuehren";
            if (filename.Contains("gutschrift") || filename.Contains("credit"))
                return "gutschriften";

            if (filename.Contains("korres") || filename.Contains("mail") || filename.Contains("brief") || filename.Contains("letter"))
                return "korrespondenz";

            if (filename.Contains("lebenslauf") || filename.Contains("cv") || filename.Contains("bewerbung") || filename.Contains("application"))
                return "bewerbungen";
            if (filename.Contains("vertrag") || filename.Contains("contract") || filename.Contains("agreement"))
                return "vertraege";
            if (filename.Contains("zeugnis") || filename.Contains("certificate") || filename.Contains("diplom"))
                return "zeugnisse";

            if (filename.Contains("lizenz") || filename.Contains("license"))
                return "lizenzen";
            if (filename.Contains("versicherung") || filename.Contains("insurance"))
                return "versicherungen";
            if (filename.Contains("genehmigung") || filename.Contains("permit") || filename.Contains("authorisation"))
                return "genehmigungen";

            if (filename.Contains("steuer") || filename.Contains("tax"))
                return "steuerunterlagen";
            if (filename.Contains("bilanz") || filename.Contains("balance"))
                return "bilanzen";

            if (filename.Contains("projekt") || filename.Contains("project"))
                return "projekte";
            if (filename.Contains("handbuch") || filename.Contains("manual") || filename.Contains("guide"))
                return "handbuecher";
            if (filename.Contains("plan") || filename.Contains("drawing") || filename.Contains("blueprint"))
                return "plaene";

            if (filename.Contains("bericht") || filename.Contains("report"))
                return "berichte";
            if (filename.Contains("foto") || filename.Contains("photo") || filename.Contains("image") || filename.Contains("bild"))
                return "bilder";

            // Fallback
            return "sonstige"; 
        }



        public List<Dokumente> OriginaleDokumente { get; set; }
        public List<Dokumente> VersionierteDokumente { get; set; }
        public List<Dokumente> ArchivierteDokumente { get; set; }
        public List<Dokumente> SonstigeDokumente { get; set; }


        public class DokumentViewModel
        {
            public Dokumente Dokument { get; set; }
            public int CommentCount { get; set; }
            public string CommentSummary { get; set; }
        }
        public List<DokumentViewModel> DokumenteMitKommentare { get; set; } = new();
        public class VersionItem
        {
            public string OriginalName { get; set; }
            public string Dateiname { get; set; }
            public string SasUrl { get; set; }
            public string ObjectPath { get; set; }
            public DateTime HochgeladenAm { get; set; }
            public Guid DokumentId { get; set; }
            public string Kategorie { get; set; }
            public int CommentCount { get; set; }
            public string CommentSummary { get; set; }
        }
        public List<VersionGroup> GruppierteVersionen { get; set; } = new();
        public class VersionGroup
        {
            public string OriginalName { get; set; }
            public List<VersionItem> Versions { get; set; }
        }
        public string? Selected { get; set; }
        public string? InitialFolderPath { get; set; }
        public string Firma { get; set; }
        public string FirmaName { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string AbteilungName { get; set; } = string.Empty;
        public string ProfilbildUrl { get; set; } = "/images/default-avatar.png";
        public string UserRoles { get; set; } = string.Empty;
        
        public List<string> AbteilungenMitDocs { get; set; } = new();
        public async Task OnGetAsync(
            string? SelectedFolder,
            string? Kategorie,
            DateTime? Von,
            DateTime? Bis,
            string? fromFileId = null,
            string? fileName = null,
            string? filePath = null,
            int pageNumber = 1,
            int pageSize = 15)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // ================= BENUTZERDATEN =================
            var user = await _userManager.Users
                .Include(u => u.Abteilung)
                .FirstOrDefaultAsync(u => u.Id == userId);

            if (user == null)
            {
                Console.WriteLine("⚠️ Kein Benutzer gefunden.");
                return;
            }

            PageNumber = pageNumber;
            PageSize = pageSize;

            Firma = user.FirmenName?.Trim().ToLowerInvariant() ?? "";
            FirmaName = user.FirmenName ?? "Meine Firma GmbH";
            FullName = user.FullName ?? "";
            AbteilungName = user.Abteilung?.Name ?? "Unbekannt";
            ProfilbildUrl = string.IsNullOrEmpty(user.ProfilbildUrl)
                ? "/images/default-avatar.png"
                : $"/Profile/GetAvatar?v={DateTime.Now.Ticks}";

            var roles = await _userManager.GetRolesAsync(user);
            UserRoles = roles.Any() ? string.Join(", ", roles) : "Keine Rolle";

            if (string.IsNullOrWhiteSpace(Firma))
                return;

            // ================= PDF-Modal =================
            if (!string.IsNullOrEmpty(fromFileId))
            {
                ViewData["ShowCreateModal"] = true;
                ViewData["AttachedFileId"] = fromFileId;
                ViewData["AttachedFileName"] = fileName ?? "";
                ViewData["AttachedFilePath"] = filePath ?? "";
            }
            else
            {
                ViewData["ShowCreateModal"] = false;
            }

            // ================= CLAIMS & ROOT =================
            var firstClaim = User.FindFirst("FolderAccess")?.Value;
            if (!string.IsNullOrEmpty(firstClaim))
            {
                var parts = firstClaim.Split('/');
                if (parts.Length > 1)
                {
                    Firma = parts[1].Trim().ToLowerInvariant();
                    Console.WriteLine($"🧭 Firma aus Claim erkannt: {Firma}");
                }
            }

            var rootPath = $"dokumente/{Firma}";

            // ================= ORIGINALE =================
            var originale = await _db.Dokumente
                .Include(d => d.Abteilung)
                .Include(d => d.MetadatenObjekt)
                .Include(d => d.ApplicationUser)
                .Where(d =>
                    d.Abteilung != null &&
                    !string.IsNullOrEmpty(d.Dateipfad) &&
                    !string.IsNullOrEmpty(d.Dateiname) &&
                    (
                        (d.ApplicationUser != null && d.ApplicationUser.FirmenName.ToLower() == Firma) ||
                        (d.ObjectPath != null && d.ObjectPath.ToLower().Contains(Firma))
                    ))
                .ToListAsync();

            originale = originale
                .Where(d => User.HasAccess($"dokumente/{Firma}/{d.Abteilung?.Name}/*"))
                .ToList();

            // ================= VERSIONEN =================
            var versionen = await _db.DokumentVersionen
                .Include(v => v.Abteilung)
                .Include(v => v.ApplicationUser)
                .Where(v =>
                    v.Abteilung != null &&
                    (
                        (v.ApplicationUser != null && v.ApplicationUser.FirmenName.ToLower() == Firma) ||
                        (v.ObjectPath != null && v.ObjectPath.ToLower().Contains(Firma))
                    ))
                .ToListAsync();

            versionen = versionen
                .Where(v =>
                    User.HasAccess($"dokumente/{Firma}/{v.Abteilung?.Name}/*") ||
                    User.HasAccess($"dokumente/{Firma}/{v.Abteilung?.Name}/versionen/*"))
                .ToList();

            // ================= VERSIONEN ALS DOKUMENTE =================
            var versionDocs = versionen.Select(v =>
            {
                var meta = DeserializeMetadata(v.MetadataJson);
                return new Dokumente
                {
                    Id = v.Id,
                    OriginalId = v.OriginalId,
                    Dateiname = v.Dateiname,
                    Kategorie = meta.Kategorie ?? "versionen",
                    ObjectPath = v.ObjectPath,
                    Dateipfad = v.Dateipfad,
                    ApplicationUserId = v.ApplicationUserId,
                    ApplicationUser = v.ApplicationUser,
                    AbteilungId = v.AbteilungId,
                    Abteilung = v.Abteilung,
                    HochgeladenAm = v.HochgeladenAm,
                    IsVersion = true,
                    EstSigne = v.EstSigne,
                    IsIndexed = false,
                    MetadatenObjekt = meta
                };
            }).ToList();

            // ================= 🔧 CORRECTION : relier les versions aux originaux =================
            Console.WriteLine("=== 🔍 LIAISON VERSION → ORIGINAL ===");
            foreach (var v in versionDocs)
            {
                if (v.OriginalId == Guid.Empty || v.OriginalId == null)
                {
                    // On essaie d'associer via le nom
                    var baseName = Path.GetFileNameWithoutExtension(v.Dateiname)
                        .Replace("_V", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("-V", "", StringComparison.OrdinalIgnoreCase)
                        .Trim();

                    var originalMatch = originale.FirstOrDefault(o =>
                        Path.GetFileNameWithoutExtension(o.Dateiname)
                        .Equals(baseName, StringComparison.OrdinalIgnoreCase));

                    if (originalMatch != null)
                    {
                        v.OriginalId = originalMatch.Id;
                        Console.WriteLine($"✅ Version '{v.Dateiname}' liée à Original '{originalMatch.Dateiname}'");
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ Aucune correspondance trouvée pour {v.Dateiname}");
                    }
                }
                else
                {
                    Console.WriteLine($"🔗 Version '{v.Dateiname}' → OriginalId déjà défini = {v.OriginalId}");
                }
            }

            // ================= GRUPPIERUNG: ORIGINAL + VERSIONEN =================
            var alleDocs = new List<Dokumente>();

            foreach (var original in originale.OrderByDescending(d => d.HochgeladenAm))
            {
                alleDocs.Add(original);

                var versions = versionDocs
                    .Where(v => v.OriginalId == original.Id)
                    .OrderByDescending(v => v.HochgeladenAm)
                    .ToList();

                foreach (var v in versions)
                {
                    v.Kategorie = original.Kategorie;
                    v.Abteilung = original.Abteilung;
                    v.IsVersion = true;

                    if (v.MetadatenObjekt == null && original.MetadatenObjekt != null)
                    {
                        var cloneJson = JsonSerializer.Serialize(original.MetadatenObjekt);
                        v.MetadatenObjekt = JsonSerializer.Deserialize<Metadaten>(cloneJson);
                    }

                    alleDocs.Add(v);
                }
            }

            // Nettoyage final
            alleDocs = alleDocs
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .ToList();

            foreach (var doc in alleDocs)
            {
                doc.HasVersions = alleDocs.Any(v => v.OriginalId == doc.Id && v.IsVersion);
            }

            // ================= EXPLORER TREE =================
            var explorerDocs = originale.Concat(versionDocs).ToList();
            ExplorerTree = await _WebDav.BuildExplorerTreeAsync(rootPath, explorerDocs);

            // 🛠️ FIX: Leere Ordner explizit hinzufügen
            try
            {
                var physicalFolderNames = await _WebDav.ListFoldersAsync(rootPath);
                foreach (var folderName in physicalFolderNames)
                {
                    if (string.IsNullOrWhiteSpace(folderName)) continue;
                    
                    // 🛡️ Filter: "temp" und den eigenen Firmen-/Email-Ordner ausblenden
                    if (folderName.Equals("temp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (folderName.Equals(Firma, StringComparison.OrdinalIgnoreCase)) continue; // Versteckt sfyen.br@gmail.com
                    
                    // 1. Pfad korrekt konstruieren
                    var fullFolderPath = $"{rootPath.TrimEnd('/')}/{folderName}";

                    var existingFolder = ExplorerTree.FirstOrDefault(f => f.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase));
                    if (existingFolder == null)
                    {
                        // Neuer, leerer Ordner (Abteilung)
                        existingFolder = new DmsFolder
                        {
                            Name = folderName,
                            Path = fullFolderPath, // WICHTIG: Voller Pfad für UI-Links
                            IsAbteilung = true,
                            Icon = "fas fa-building text-info",
                            Files = new List<DmsFile>(),
                            SubFolders = new List<DmsFolder>()
                        };
                        ExplorerTree.Add(existingFolder);
                    }

                    // 2. Unterordner (Kategorien) scannen mit korrektem Pfad
                    var subFolderNames = await _WebDav.ListFoldersAsync(fullFolderPath);
                    foreach (var subName in subFolderNames)
                    {
                         if (string.IsNullOrWhiteSpace(subName) || 
                             subName.Equals("versionen", StringComparison.OrdinalIgnoreCase) ||
                             subName.Equals(folderName, StringComparison.OrdinalIgnoreCase)) // 🛡️ Fix: Parent-Ordner (Duplikat) ignorieren
                             continue;

                         if (!existingFolder.SubFolders.Any(s => s.Name.Equals(subName, StringComparison.OrdinalIgnoreCase)))
                         {
                             var fullSubFolderPath = $"{fullFolderPath.TrimEnd('/')}/{subName}";
                             
                             existingFolder.SubFolders.Add(new DmsFolder
                             {
                                 Name = subName,
                                 Path = fullSubFolderPath,
                                 IsAbteilung = false,
                                 Icon = "bi bi-folder-fill text-warning",
                                 Files = new List<DmsFile>(),
                                 SubFolders = new List<DmsFolder>()
                             });
                         }
                    }
                }
                
                // Sortierung aktualisieren
                ExplorerTree = ExplorerTree.OrderBy(f => f.Name).ToList();
                foreach (var f in ExplorerTree)
                {
                    f.SubFolders = f.SubFolders.OrderBy(s => s.Name).ToList();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Fehler beim Laden leerer Ordner: {ex.Message}");
            }

            ExplorerTree = ExplorerTree
                .Where(folder =>
                    User.HasAccess($"dokumente/{Firma}/{folder.Name}/*") ||
                    folder.SubFolders.Any(sf =>
                        User.HasAccess($"dokumente/{Firma}/{folder.Name}/{sf.Name}/*") ||
                        sf.Name.Equals("versionen", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // ================= FILTER =================
            if (!string.IsNullOrEmpty(SelectedFolder))
            {
                if (SelectedFolder.Equals("Archiv", StringComparison.OrdinalIgnoreCase))
                {
                    // Spezialfall: Archiv ist keine echte Abteilung
                     alleDocs = alleDocs
                        .Where(d => d.ObjectPath.Contains("/archiv/", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                }
                else
                {
                    alleDocs = alleDocs
                        .Where(d => d.Abteilung?.Name.Equals(SelectedFolder, StringComparison.OrdinalIgnoreCase) == true)
                        .ToList();
                }
            }

            if (!string.IsNullOrEmpty(Typ) && Typ.Equals("archiviert", StringComparison.OrdinalIgnoreCase))
            {
                 alleDocs = alleDocs
                    .Where(d => d.ObjectPath.Contains("/archiv/", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (!string.IsNullOrEmpty(Kategorie))
                alleDocs = alleDocs
                    .Where(d => d.Kategorie?.Equals(Kategorie, StringComparison.OrdinalIgnoreCase) == true)
                    .ToList();

            if (Von.HasValue)
                alleDocs = alleDocs.Where(d => d.HochgeladenAm >= Von.Value).ToList();

            if (Bis.HasValue)
            {
                var bisEndOfDay = Bis.Value.Date.AddDays(1).AddTicks(-1);
                alleDocs = alleDocs.Where(d => d.HochgeladenAm <= bisEndOfDay).ToList();
            }

            // ================= PAGINATION =================
            TotalCount = alleDocs.Count;
            DokumentListe = alleDocs
                .Skip((PageNumber - 1) * PageSize)
                .Take(PageSize)
                .ToList();

            // ================= ICONS =================
            foreach (var d in DokumentListe)
            {
                string ext = Path.GetExtension(d.Dateiname ?? "").ToLowerInvariant();
                string icon = ext switch
                {
                    ".pdf" => "bi bi-file-earmark-pdf text-danger",
                    ".png" or ".jpg" or ".jpeg" => "bi bi-file-image text-info",
                    ".doc" or ".docx" => "bi bi-file-earmark-word text-primary",
                    ".xls" or ".xlsx" => "bi bi-file-earmark-excel text-success",
                    ".txt" or ".csv" => "bi bi-file-earmark-text text-secondary",
                    ".zip" or ".rar" => "bi bi-file-earmark-zip text-warning",
                    _ => "bi bi-file-earmark text-secondary"
                };

                if (!string.IsNullOrEmpty(d.ObjectPath))
                {
                    if (d.ObjectPath.Contains("/versionen/", StringComparison.OrdinalIgnoreCase))
                        icon = "bi bi-layers text-success";
                    else if (d.ObjectPath.Contains("/reconstructed/", StringComparison.OrdinalIgnoreCase))
                        icon = "bi bi-arrow-repeat text-warning";
                }

                d.Icon = icon;
            }

            // ================= SAS URLS =================
            foreach (var d in DokumentListe)
            {
                if (!string.IsNullOrEmpty(d.ObjectPath))
                    d.SasUrl = _WebDav.GenerateSignedUrl(d.ObjectPath, 15);
            }

            // ================= INDEXIERUNG =================
            var indexedIds = await _db.DokumentIndex.Select(x => x.DokumentId).ToListAsync();
            foreach (var d in DokumentListe)
                d.IsIndexed = indexedIds.Contains(d.Id);

            // ================= AUDIT LOGS =================
            AuditLogs = await _auditLogDokumentService.ObtenirHistoriquePourBenutzerAsync(userId);

            // ================= ABTEILUNGEN & KATEGORIEN =================
            var alleAbteilungenDb = await _db.Abteilungen.OrderBy(a => a.Name).ToListAsync();
            AlleAbteilungen = alleAbteilungenDb
                .Where(a => User.HasAccess($"dokumente/{Firma}/{a.Name}/*"))
                .ToList();

            AlleKategorien = alleDocs
                .Where(d => !string.IsNullOrWhiteSpace(d.Kategorie))
                .Select(d => d.Kategorie)
                .Distinct()
                .OrderBy(k => k)
                .ToList();
        }


        // ✅ Hilfsmethode für Metadaten-Deserialisierung
        private Metadaten DeserializeMetadata(string? metadataJson)
        {
            if (string.IsNullOrWhiteSpace(metadataJson))
                return new Metadaten();

            try
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson);
                if (dict == null)
                    return new Metadaten();

                var meta = new Metadaten
                {
                    Titel = dict.GetValueOrDefault("Titel")?.ToString(),
                    Beschreibung = dict.GetValueOrDefault("Beschreibung")?.ToString(),
                    Kategorie = dict.GetValueOrDefault("Kategorie")?.ToString(),
                    Rechnungsnummer = dict.GetValueOrDefault("Rechnungsnummer")?.ToString(),
                    Kundennummer = dict.GetValueOrDefault("Kundennummer")?.ToString(),
                    Email = dict.GetValueOrDefault("Email")?.ToString(),
                    Telefon = dict.GetValueOrDefault("Telefon")?.ToString(),
                    IBAN = dict.GetValueOrDefault("IBAN")?.ToString(),
                    BIC = dict.GetValueOrDefault("BIC")?.ToString(),
                    PdfAutor = dict.GetValueOrDefault("PdfAutor")?.ToString(),
                    PdfBetreff = dict.GetValueOrDefault("PdfBetreff")?.ToString(),
                    PdfSchluesselwoerter = dict.GetValueOrDefault("PdfSchluesselwoerter")?.ToString()
                };

                if (decimal.TryParse(dict.GetValueOrDefault("Rechnungsbetrag")?.ToString(), out var rb)) meta.Rechnungsbetrag = rb;
                if (decimal.TryParse(dict.GetValueOrDefault("Nettobetrag")?.ToString(), out var nb)) meta.Nettobetrag = nb;
                if (decimal.TryParse(dict.GetValueOrDefault("Gesamtpreis")?.ToString(), out var gp)) meta.Gesamtpreis = gp;
                if (decimal.TryParse(dict.GetValueOrDefault("Steuerbetrag")?.ToString(), out var sb)) meta.Steuerbetrag = sb;

                if (DateTime.TryParse(dict.GetValueOrDefault("Rechnungsdatum")?.ToString(), out var rd)) meta.Rechnungsdatum = rd;
                if (DateTime.TryParse(dict.GetValueOrDefault("Faelligkeitsdatum")?.ToString(), out var fd)) meta.Faelligkeitsdatum = fd;

                return meta;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Fehler beim Lesen der Metadaten: {ex.Message}");
                return new Metadaten();
            }
        }




        // Comparateur custom pour Union
        public class AbteilungNameComparer : IEqualityComparer<Abteilung>
        {
            public bool Equals(Abteilung? x, Abteilung? y)
                => string.Equals(x?.Name, y?.Name, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(Abteilung obj)
                => obj.Name.ToLowerInvariant().GetHashCode();
        }


        private bool IsBase64String(string s)
        {
            // Mini-Schutz, damit kein Leerstring crasht
            if (string.IsNullOrWhiteSpace(s)) return false;
            Span<byte> buffer = new Span<byte>(new byte[s.Length]);
            return Convert.TryFromBase64String(
                s.Replace('-', '+').Replace('_', '/'),
                buffer,
                out int bytesParsed);
        }




        public static string ExtractRelativePathFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

     
            var marker = "/dokumente/";
            var idx = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? url.Substring(idx + 1) : null;
        }


        private string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            // Entferne WebDAV-Basis-URL (falls sie enthalten ist)
            var baseUrl = _WebDav.BaseUrl?.TrimEnd('/') ?? "";

            if (!string.IsNullOrEmpty(baseUrl) && path.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(baseUrl.Length).TrimStart('/');

            // Ersetze Backslashes durch normale Slashes
            return path.Replace("\\", "/");
        }



        public static string GetStatusBadgeClass(DokumentStatus status) => status switch
        {
            DokumentStatus.Neu => "bg-secondary",
            DokumentStatus.InBearbeitung => "bg-warning text-dark",
            DokumentStatus.Fertig => "bg-success",
            DokumentStatus.Fehlerhaft => "bg-danger",
            _ => "bg-dark"
        };


        public async Task<IActionResult> OnGetExportPdfAsync()
        {
            if (DokumentListe == null || !DokumentListe.Any())
                await LoadDokumente();


            var htmlTemplatePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "templates", "pdf-template.html");

            if (!System.IO.File.Exists(htmlTemplatePath))
                return Content("❌ Template HTML introuvable : " + htmlTemplatePath);

            var htmlTemplate = await System.IO.File.ReadAllTextAsync(htmlTemplatePath, Encoding.UTF8);

 
            var sb = new StringBuilder();

            foreach (var d in DokumentListe)
            {
                sb.AppendLine("<tr>");
                sb.AppendLine($"<td>{WebUtility.HtmlEncode(d.Dateiname)}</td>");
                sb.AppendLine($"<td>{WebUtility.HtmlEncode(d.Kategorie)}</td>");
                sb.AppendLine($"<td>{WebUtility.HtmlEncode(d.ApplicationUserId)}</td>");
                sb.AppendLine($"<td>{d.HochgeladenAm:dd.MM.yyyy}</td>");
                sb.AppendLine($"<td>{WebUtility.HtmlEncode(d.dtStatus.ToString())}</td>");
                sb.AppendLine("</tr>");
            }

            var html = htmlTemplate.Replace("{{rows}}", sb.ToString());

   
            var doc = new HtmlToPdfDocument
            {
                GlobalSettings = new GlobalSettings
                {
                    PaperSize = PaperKind.A4,
                    Orientation = DinkToPdf.Orientation.Landscape,  
                    DocumentTitle = $"Dokumentenliste - {DateTime.Now:yyyy-MM-dd}"
                },

                Objects = {
            new ObjectSettings
            {
                HtmlContent = html,
                WebSettings = { DefaultEncoding = "utf-8" }
            }
        }
            };

            var pdfBytes = _pdfConverter.Convert(doc);
            var user = await _userManager.GetUserAsync(User);
            if (user != null)
            {
                await _auditLogDokumentService.EnregistrerAsync("📤 Dokumentliste exportiert (PDF/CSV/Excel)", user.Id, Guid.Empty);
            }


            return File(pdfBytes, "application/pdf", $"Dokumente_{DateTime.Now:yyyyMMdd}.pdf");
        }

        public async Task<IActionResult> OnGetFilterByFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Partial("_DokumentRowList", new List<DmsProjeckt.Data.DmsFile>());

            var docs = await _WebDav.GetDocumentsByFolderAsync(path);

            if (docs == null || !docs.Any())
                return Partial("_DokumentRowList", new List<DmsProjeckt.Data.DmsFile>());

            return Partial("_DokumentRowList", docs);
        }



        public async Task<IActionResult> OnGetExportCsvAsync()
        {
            if (DokumentListe == null || !DokumentListe.Any())
                await LoadDokumente();

            var sb = new StringBuilder();
            sb.AppendLine("Dateiname;Kategorie;Rechnungsnummer;Rechnungsdatum;Gesamtpreis;Email;Telefon;Adresse;UIDNummer;IBAN;BIC;Bankverbindung;Status");

            foreach (var d in DokumentListe)
            {
                var meta = d.MetadatenObjekt;

                sb.AppendLine(string.Join(";", new[]
                {
            Quote(d.Dateiname),
            Quote(meta?.Kategorie ?? d.Kategorie),
            Quote(meta?.Rechnungsnummer),
            Quote(meta?.Rechnungsdatum?.ToString("yyyy-MM-dd")),
            Quote(meta?.Gesamtpreis?.ToString("F2")),
            Quote(meta?.Email),
            Quote(meta?.Telefon),
            Quote(meta?.Adresse),
            Quote(meta?.UIDNummer),
            Quote(meta?.IBAN),
            Quote(meta?.BIC),
            Quote(meta?.Bankverbindung),
            Quote(d.dtStatus.ToString())
        }));
            }

            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var user = await _userManager.GetUserAsync(User);
            if (user != null)
            {
                await _auditLogDokumentService.EnregistrerAsync("📤 Dokumentliste exportiert (PDF/CSV/Excel)", user.Id, Guid.Empty);
            }
            return File(bytes, "text/csv", $"Dokumente_{DateTime.Now:yyyyMMdd}.csv");
         

            string Quote(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "\"\"";
                return $"\"{s.Replace("\"", "\"\"")}\"";
            }
        }

        public async Task<IActionResult> OnGetExportExcelAsync()
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            if (DokumentListe == null || !DokumentListe.Any())
                await LoadDokumente();

            using var package = new ExcelPackage();
            var sheet = package.Workbook.Worksheets.Add("Dokumente");

            // 🧠 Spaltenüberschriften
            var headers = new[]
            {
        "Dateiname", "Kategorie", "Rechnungsnummer", "Rechnungsdatum", "Gesamtpreis", "Email",
        "Telefon", "Adresse", "UIDNummer", "IBAN", "BIC", "Bankverbindung", "Status"
    };

            // 🧾 Kopfzeile
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = sheet.Cells[1, i + 1];
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                cell.Style.Border.BorderAround(ExcelBorderStyle.Thin);
                cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
            }

            int row = 2;

            foreach (var d in DokumentListe)
            {
                var meta = d.MetadatenObjekt;

                sheet.Cells[row, 1].Value = d.Dateiname;
                sheet.Cells[row, 2].Value = meta?.Kategorie ?? d.Kategorie;
                sheet.Cells[row, 3].Value = meta?.Rechnungsnummer;
                sheet.Cells[row, 4].Value = meta?.Rechnungsdatum?.ToString("yyyy-MM-dd");

                if (double.TryParse(meta?.Gesamtpreis?.ToString(), out double preis))
                {
                    sheet.Cells[row, 5].Value = preis;
                    sheet.Cells[row, 5].Style.Numberformat.Format = "#,##0.00 €";
                }

                sheet.Cells[row, 6].Value = meta?.Email;
                sheet.Cells[row, 7].Value = meta?.Telefon;
                sheet.Cells[row, 8].Value = meta?.Adresse;
                sheet.Cells[row, 9].Value = meta?.UIDNummer;
                sheet.Cells[row, 10].Value = meta?.IBAN;
                sheet.Cells[row, 11].Value = meta?.BIC;
                sheet.Cells[row, 12].Value = meta?.Bankverbindung;
                sheet.Cells[row, 13].Value = d.dtStatus.ToString();

                // 🎨 Status-Farben
                var status = d.dtStatus.ToString();
                var statusCell = sheet.Cells[row, 13];
                statusCell.Style.Fill.PatternType = ExcelFillStyle.Solid;

                switch (status)
                {
                    case "Fertig":
                        statusCell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGreen);
                        break;
                    case "InBearbeitung":
                        statusCell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.Khaki);
                        break;
                    case "Fehlerhaft":
                        statusCell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightCoral);
                        break;
                    default:
                        statusCell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                        break;
                }

                row++;
            }

            // 📊 Tabelle + Formatierung
            var dataRange = sheet.Cells[1, 1, row - 1, headers.Length];
            var table = sheet.Tables.Add(dataRange, "DokumenteTabelle");
            table.ShowHeader = true;
            table.TableStyle = OfficeOpenXml.Table.TableStyles.Medium2;

            sheet.Cells[sheet.Dimension.Address].AutoFitColumns();

            var fileBytes = package.GetAsByteArray();
            var user = await _userManager.GetUserAsync(User);
            if (user != null)
            {
                await _auditLogDokumentService.EnregistrerAsync("📤 Dokumentliste exportiert (PDF/CSV/Excel)", user.Id, Guid.Empty);
            }

            return File(fileBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Dokumente_PRO_{DateTime.Now:yyyyMMdd}.xlsx");
        }

        private async Task LoadDokumente()
        {
            DokumentListe = await _db.Dokumente.ToListAsync();
        }
        [BindProperty]
        public MoveRequest MoveRequestModel { get; set; }

        public async Task<IActionResult> OnPostDownloadFileAsync([FromBody] MoveRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Source)) return BadRequest("Missing path");
            var url = await _WebDav.GetDownloadUrlAsync(req.Source);
            if (url == null) return NotFound("File not found");
            return new JsonResult(new { url });
        }

        public async Task<IActionResult> OnPostCopyPathAsync([FromBody] MoveRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Source)) return BadRequest();
            return new JsonResult(new { path = req.Source });
        }





        public async Task<IActionResult> OnPostGetPropertiesAsync([FromBody] MoveRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Source)) return BadRequest("Missing path");
            var props = await _WebDav.GetPropertiesAsync(req.Source);
            if (props == null) return NotFound();
            return new JsonResult(new { success = true, properties = props });
        }

        [HttpPost]
        public async Task<IActionResult> OnPostRenameFileAsync([FromBody] RenameRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.TargetPath))
                    return new JsonResult(new { success = false, message = "❌ Ungültige Anfrage." });

                // 🔍 Dokument aus DB laden (nach Source/ObjectPath)
                var doc = await _db.Dokumente.FirstOrDefaultAsync(d => d.ObjectPath == req.SourcePath);

                // 🔄 FALLBACK: Prüfen, ob es sich um eine VERSION handelt
                DokumentVersionen versionDoc = null;
                if (doc == null)
                {
                    versionDoc = await _db.DokumentVersionen.FirstOrDefaultAsync(v => v.ObjectPath == req.SourcePath);
                }

                if (doc == null && versionDoc == null)
                    return new JsonResult(new { success = false, message = "❌ Datei nicht gefunden." });

                // 🔧 Pfade normalisieren
                var src = req.SourcePath.Trim().Replace("%2F", "/");
                var dest = req.TargetPath.Trim().Replace("%2F", "/");

                Console.WriteLine($"✏️ Rename requested: {src} → {dest}");

                // ✅ 1️⃣ Télécharger le fichier source depuis WebDAV
                using var sourceStream = await _WebDav.DownloadStreamAsync(src);
                if (sourceStream == null)
                    return new JsonResult(new { success = false, message = $"❌ Quelle nicht gefunden: {src}" });

                // ✅ 2️⃣ Ré-upload sous le nouveau nom
                await _WebDav.UploadStreamAsync(sourceStream, dest, "application/pdf");

                // ✅ 3️⃣ Supprimer l’ancien fichier
                await _WebDav.DeleteFileAsync(src);

                // ✅ 4️⃣ Mettre à jour la base de données
                if (doc != null)
                {
                     // Hauptdokument
                    doc.ObjectPath = dest;
                    doc.Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{dest}";
                    doc.Dateiname = System.IO.Path.GetFileName(dest);
                }
                else if (versionDoc != null)
                {
                    // Version
                    versionDoc.ObjectPath = dest;
                    versionDoc.Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{dest}";
                    versionDoc.Dateiname = System.IO.Path.GetFileName(dest);
                    _db.DokumentVersionen.Update(versionDoc); // explizit update markieren
                }

                await _db.SaveChangesAsync();

                Console.WriteLine($"✅ Datei erfolgreich umbenannt: {dest}");

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Umbenennen: {ex.Message}");
                return new JsonResult(new { success = false, message = "Fehler: " + ex.Message });
            }
        }
        [HttpPost]
        public async Task<IActionResult> OnPostMoveFileAsync([FromBody] MoveRequest req)
        {
            _logger.LogInformation("📦 Verschiebe-Datei gestartet: {Source} → {Target}", req.Source, req.Target);

            if (string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Target))
                return BadRequest(new { success = false, message = "❌ Quelle oder Ziel fehlt." });

            try
            {
                // =====================================================
                // 🔧 Pfad normalisieren (URL → relativer Pfad)
                // =====================================================
                string NormalizePath(string path)
                {
                    if (string.IsNullOrWhiteSpace(path)) return "";
                    path = Uri.UnescapeDataString(path);
                    path = path.Replace("\\", "/");
                    if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = path.IndexOf("/DmsDaten/", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                            path = path.Substring(idx + "/DmsDaten/".Length);
                    }
                    return path.Trim('/');
                }

                bool PathsMatch(string a, string b)
                {
                    if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                        return false;
                    a = Uri.UnescapeDataString(a).Trim().Replace("\\", "/").ToLowerInvariant();
                    b = Uri.UnescapeDataString(b).Trim().Replace("\\", "/").ToLowerInvariant();
                    return a.Equals(b) || a.EndsWith(b) || b.EndsWith(a);
                }

                // =====================================================
                // 🔍 Pfade normalisieren & Dokument finden
                // =====================================================
                req.Source = NormalizePath(req.Source);
                req.Target = NormalizePath(req.Target);
                _logger.LogInformation("🔍 Normalisierter Quellpfad: {Source}", req.Source);

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                bool isAdmin = User.IsInRole("Admin") || User.IsInRole("SuperAdmin");

                var alleDokumente = await _db.Dokumente
                    .Include(d => d.MetadatenObjekt)
                    .Include(d => d.Abteilung)
                    .Include(d => d.ApplicationUser)
                    .ToListAsync();

                var sourceDoc = alleDokumente.FirstOrDefault(d =>
                    PathsMatch(d.ObjectPath, req.Source) &&
                    (isAdmin || d.ApplicationUserId == userId));

                if (sourceDoc == null)
                {
                    _logger.LogWarning("❌ Quelldokument nicht gefunden oder keine Berechtigung: {Source}", req.Source);
                    return new JsonResult(new { success = false, message = "Quelldokument nicht gefunden oder keine Berechtigung." });
                }

                // =====================================================
                // 🏢 Firmenname, Abteilung & Kategorie bestimmen
                // =====================================================
                string firma = sourceDoc.ApplicationUser?.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";
                string abteilungName = req.Abteilung?.Trim().ToLowerInvariant()
                    ?? sourceDoc.Abteilung?.Name?.ToLowerInvariant()
                    ?? "allgemein";

                int? abteilungId = req.AbteilungId;
                if (abteilungId == null)
                {
                    var abt = await _db.Abteilungen.FirstOrDefaultAsync(a => a.Name.ToLower() == abteilungName);
                    abteilungId = abt?.Id;
                }

                string kategorie = req.Kategorie?.Trim().ToLowerInvariant()
                    ?? sourceDoc.Kategorie?.ToLowerInvariant()
                    ?? "allgemein";

                // =====================================================
                // 📂 Zielpfad vorbereiten
                // =====================================================
                string fileName = Path.GetFileName(sourceDoc.Dateiname);
                string targetPath = $"dokumente/{firma}/{abteilungName}/{kategorie}/{fileName}";

                if (sourceDoc.ObjectPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("⚠️ Quelle und Ziel sind identisch. Abbruch.");
                    return new JsonResult(new { success = false, message = "Quelle und Ziel sind identisch." });
                }

                // =====================================================
                // 🔁 MOVE über WebDAV
                // =====================================================
                _logger.LogInformation("🚚 Verschiebe Datei über WebDAV: {Source} → {Target}", sourceDoc.ObjectPath, targetPath);

                using (var stream = await _WebDav.DownloadStreamAsync(sourceDoc.ObjectPath))
                {
                    if (stream == null)
                    {
                        _logger.LogWarning("❌ Quelle konnte nicht heruntergeladen werden: {Path}", sourceDoc.ObjectPath);
                        return new JsonResult(new { success = false, message = "Quelldatei konnte nicht geladen werden." });
                    }

                    await _WebDav.UploadStreamAsync(stream, targetPath, "application/octet-stream");
                }

                await _WebDav.DeleteFileAsync(sourceDoc.ObjectPath);

                // =====================================================
                // 🔄 Dokument aktualisieren
                // =====================================================
                sourceDoc.ObjectPath = targetPath;
                sourceDoc.Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{targetPath}";
                sourceDoc.AbteilungId = abteilungId;
                sourceDoc.Kategorie = kategorie;
                sourceDoc.HochgeladenAm = DateTime.UtcNow;
                sourceDoc.IsUpdated = true;
                sourceDoc.dtStatus = DokumentStatus.Fertig;
                sourceDoc.Beschreibung = $"Verschoben nach '{kategorie}' am {DateTime.UtcNow:dd.MM.yyyy HH:mm}";

                // =====================================================
                // 🧠 Metadaten aktualisieren oder neu erstellen
                // =====================================================
                var neueMetadaten = await DocumentPathHelper.CreateFullMetadataFromModelAsync(
                    _db,
                    sourceDoc,
                    new DmsProjeckt.Pages.Tests.UploadMultiModel.MetadataModel
                    {
                        Titel = sourceDoc.MetadatenObjekt?.Titel ?? sourceDoc.Dateiname,
                        Kategorie = kategorie,
                        Beschreibung = $"Verschoben nach '{kategorie}' am {DateTime.UtcNow:dd.MM.yyyy HH:mm}",
                        Rechnungsnummer = sourceDoc.MetadatenObjekt?.Rechnungsnummer,
                        Kundennummer = sourceDoc.MetadatenObjekt?.Kundennummer,
                        Email = sourceDoc.MetadatenObjekt?.Email,
                        Telefon = sourceDoc.MetadatenObjekt?.Telefon,
                        IBAN = sourceDoc.MetadatenObjekt?.IBAN,
                        BIC = sourceDoc.MetadatenObjekt?.BIC,
                        Adresse = sourceDoc.MetadatenObjekt?.Adresse,
                        Website = sourceDoc.MetadatenObjekt?.Website,
                        OCRText = sourceDoc.MetadatenObjekt?.OCRText
                    },
                    "Move",
                    sourceDoc.MetadatenObjekt
                );

                sourceDoc.MetadatenId = neueMetadaten.Id;
                sourceDoc.MetadatenObjekt = neueMetadaten;

                _db.Dokumente.Update(sourceDoc);
                await _db.SaveChangesAsync();

                _logger.LogInformation("✅ Datei erfolgreich verschoben: {Target}", targetPath);
                var user = await _userManager.GetUserAsync(User);
                if (user != null)
                {
                    await _auditLogDokumentService.EnregistrerAsync($"🚚 Datei verschoben → {targetPath}", user.Id, sourceDoc.Id);
                }


                return new JsonResult(new
                {
                    success = true,
                    message = "✅ Datei erfolgreich verschoben.",
                    movedFile = new
                    {
                        id = sourceDoc.Id,
                        name = sourceDoc.Dateiname,
                        abteilung = abteilungName,
                        kategorie = kategorie,
                        path = sourceDoc.ObjectPath,
                        uploaded = sourceDoc.HochgeladenAm.ToString("dd.MM.yy HH:mm"),
                        status = sourceDoc.dtStatus.ToString()
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Verschieben der Datei.");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }


        public async Task<IActionResult> OnPostCreateExplorerAsync([FromBody] CreateExplorerRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.NewFolder))
                return new JsonResult(new { success = false, message = "Nom de dossier invalide." });

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await _userManager.FindByIdAsync(userId);
            var firma = user?.FirmenName?.Trim().ToLowerInvariant();

            _logger.LogInformation("CreateExplorer: Firmenname = '{Firma}'", firma);

            if (string.IsNullOrWhiteSpace(firma))
                return new JsonResult(new { success = false, message = "Firmenname non défini." });

    
            var cleanedPath = req.NewFolder
                .Trim()
                .Replace("\\", "/")
                .Trim('/')
                .ToLowerInvariant();

            if (cleanedPath.Contains(".."))
                return new JsonResult(new { success = false, message = "Chemin invalide." });

            var fullPath = $"dokumente/{firma}/{cleanedPath}";
            _logger.LogInformation("CreateExplorer: FullPath = {Path}", fullPath);

            // ✅ Berechtigungsprüfung (wichtig!)
            // Wir prüfen, ob der User Zugriff auf den zu erstellenden Pfad (bzw. dessen Parent) hätte.
            // Da HasAccess checkt "starts with prefix", sollte HasAccess(fullPath) true sein,
            // wenn der User z.B. Zugriff auf "dokumente/firma/abteilung/*" hat und wir einen Unterordner erstellen.
            if (!User.HasAccess(fullPath))
            {
                 _logger.LogWarning("❌ Keine Berechtigung für Pfad: {Path}", fullPath);
                 return new JsonResult(new { success = false, message = "❌ Sie haben keine Berechtigung, hier einen Ordner zu erstellen." });
            }

            try
            {
           
                await _WebDav.EnsureFolderTreeExistsAsync(fullPath);
                _logger.LogInformation("Dossier créé: {FullPath}", fullPath);

    
                var abteilungName = cleanedPath.Split('/').Last();

                var exists = await _db.Abteilungen
                    .AnyAsync(a => a.Name.ToLower() == abteilungName.ToLower());

                if (!exists)
                {
                    _db.Abteilungen.Add(new Abteilung
                    {
                        Name = abteilungName  
                    });
                    await _db.SaveChangesAsync();
                    _logger.LogInformation("Abteilung ajoutée: {Abteilung}", abteilungName);
                }

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erreur création dossier {FullPath}", fullPath);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
        [HttpPost]
        public async Task<IActionResult> OnPostCopyFileAsync([FromBody] MoveRequest req)
        {
            _logger.LogInformation("📄 Kopiervorgang gestartet: {Source} → {Target}", req.Source, req.Target);

            if (string.IsNullOrWhiteSpace(req.Source))
                return BadRequest(new { success = false, message = "❌ Quelle fehlt." });

            try
            {
                // =====================================================
                // 🔧 Pfad normalisieren (URL → relativer Pfad)
                // =====================================================
                string NormalizePath(string path)
                {
                    if (string.IsNullOrWhiteSpace(path)) return "";
                    path = Uri.UnescapeDataString(path);
                    path = path.Replace("\\", "/");
                    if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = path.IndexOf("/DmsDaten/", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                            path = path.Substring(idx + "/DmsDaten/".Length);
                    }
                    return path.Trim('/');
                }

                bool PathsMatch(string a, string b)
                {
                    if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                        return false;
                    a = Uri.UnescapeDataString(a).Trim().Replace("\\", "/").ToLowerInvariant();
                    b = Uri.UnescapeDataString(b).Trim().Replace("\\", "/").ToLowerInvariant();
                    return a.Equals(b) || a.EndsWith(b) || b.EndsWith(a);
                }

                // =====================================================
                // 🔍 Pfad normalisieren & Dokument finden
                // =====================================================
                req.Source = NormalizePath(req.Source);
                req.Target = NormalizePath(req.Target);
                _logger.LogInformation("🔍 Normalisierter Quellpfad: {Source}", req.Source);

                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                bool isAdmin = User.IsInRole("Admin") || User.IsInRole("SuperAdmin");

                var alleDokumente = await _db.Dokumente
                    .Include(d => d.MetadatenObjekt)
                    .Include(d => d.Abteilung)
                    .Include(d => d.ApplicationUser)
                    .AsNoTracking()
                    .ToListAsync();

                var sourceDoc = alleDokumente.FirstOrDefault(d =>
                    PathsMatch(d.ObjectPath, req.Source) &&
                    (isAdmin || d.ApplicationUserId == userId));

                if (sourceDoc == null)
                {
                    _logger.LogWarning("❌ Quelldokument nicht gefunden oder keine Berechtigung: {Source}", req.Source);
                    return new JsonResult(new { success = false, message = "Quelldokument nicht gefunden oder keine Berechtigung." });
                }

                // =====================================================
                // 🏢 Firmenname, Abteilung & Kategorie bestimmen
                // =====================================================
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                string firma = user?.FirmenName?.ToLowerInvariant() ?? "unbekannt";

                string abteilungName = req.Abteilung?.Trim().ToLowerInvariant()
                    ?? sourceDoc.Abteilung?.Name?.ToLowerInvariant()
                    ?? "allgemein";

                int? abteilungId = req.AbteilungId;
                if (abteilungId == null)
                {
                    var abt = await _db.Abteilungen.FirstOrDefaultAsync(a => a.Name.ToLower() == abteilungName);
                    abteilungId = abt?.Id;
                }

                string kategorie = req.Kategorie?.Trim().ToLowerInvariant()
                    ?? sourceDoc.Kategorie?.ToLowerInvariant()
                    ?? "allgemein";

                // =====================================================
                // 📂 Zielpfad (Dateiname unverändert lassen)
                // =====================================================
                string fileName = Path.GetFileName(sourceDoc.Dateiname);
                string targetPath = $"dokumente/{firma}/{abteilungName}/{kategorie}/{fileName}";

                _logger.LogInformation("📋 Kopiere nach {Target}", targetPath);

                // =====================================================
                // 🔁 Datei über WebDAV kopieren
                // =====================================================
                using var sourceStream = await _WebDav.DownloadStreamAsync(sourceDoc.ObjectPath);
                if (sourceStream == null)
                {
                    _logger.LogWarning("❌ Quelldatei konnte nicht geladen werden: {Path}", sourceDoc.ObjectPath);
                    return new JsonResult(new { success = false, message = "Quelldatei konnte nicht geladen werden." });
                }

                await _WebDav.UploadStreamAsync(sourceStream, targetPath, "application/octet-stream");

                // =====================================================
                // 🆕 Neues Dokument zuerst speichern
                // =====================================================
                var newDoc = new Dokumente
                {
                    Id = Guid.NewGuid(),
                    ApplicationUserId = userId,
                    KundeId = sourceDoc.KundeId,
                    Titel = sourceDoc.Titel,
                    Dateiname = Path.GetFileName(targetPath),
                    Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{targetPath}",
                    ObjectPath = targetPath,
                    Kategorie = kategorie,
                    AbteilungId = abteilungId,
                    Beschreibung = $"Kopie erstellt am {DateTime.UtcNow:dd.MM.yyyy HH:mm}",
                    HochgeladenAm = DateTime.UtcNow,
                    dtStatus = DokumentStatus.Fertig,
                    IsUpdated = true,
                    IsChunked = false
                };

                _db.Dokumente.Add(newDoc);
                await _db.SaveChangesAsync(); // ✅ Wichtig: zuerst speichern

                // =====================================================
                // 🧠 Dann Metadaten klonen und verknüpfen
                // =====================================================
                var neueMetadaten = await DocumentPathHelper.CreateFullMetadataFromModelAsync(
                    _db,
                    newDoc,
                    new DmsProjeckt.Pages.Tests.UploadMultiModel.MetadataModel
                    {
                        Titel = sourceDoc.MetadatenObjekt?.Titel ?? sourceDoc.Dateiname,
                        Kategorie = kategorie,
                        Beschreibung = $"Kopie von '{sourceDoc.MetadatenObjekt?.Titel ?? sourceDoc.Dateiname}' am {DateTime.UtcNow:dd.MM.yyyy HH:mm}",
                        Rechnungsnummer = sourceDoc.MetadatenObjekt?.Rechnungsnummer,
                        Kundennummer = sourceDoc.MetadatenObjekt?.Kundennummer,
                        Email = sourceDoc.MetadatenObjekt?.Email,
                        Telefon = sourceDoc.MetadatenObjekt?.Telefon,
                        IBAN = sourceDoc.MetadatenObjekt?.IBAN,
                        BIC = sourceDoc.MetadatenObjekt?.BIC,
                        Adresse = sourceDoc.MetadatenObjekt?.Adresse,
                        Website = sourceDoc.MetadatenObjekt?.Website,
                        OCRText = sourceDoc.MetadatenObjekt?.OCRText
                    },
                    "Kopie",
                    sourceDoc.MetadatenObjekt
                );

                newDoc.MetadatenId = neueMetadaten.Id;
                newDoc.MetadatenObjekt = neueMetadaten;

                _db.Dokumente.Update(newDoc);
                await _db.SaveChangesAsync();
               
                var use = await _userManager.GetUserAsync(User);

                if (use != null)
                {
                    await _auditLogDokumentService.EnregistrerAsync($"📄 Dokument kopiert → {targetPath}", user.Id, newDoc.Id);
                }


                _logger.LogInformation("✅ Datei erfolgreich kopiert: {Target}", targetPath);

                // =====================================================
                // 🧾 Antwort
                // =====================================================
                return new JsonResult(new
                {
                    success = true,
                    message = "✅ Dokument erfolgreich kopiert.",
                    newFile = new
                    {
                        id = newDoc.Id,
                        name = newDoc.Dateiname,
                        abteilung = abteilungName,
                        kategorie = kategorie,
                        path = newDoc.ObjectPath,
                        uploaded = newDoc.HochgeladenAm.ToString("dd.MM.yy HH:mm"),
                        status = newDoc.dtStatus.ToString()
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Kopieren der Datei.");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> OnPostGetBlobPropertiesAsync([FromBody] MoveRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.Source))
                return BadRequest(new { success = false, message = "Chemin invalide." });

            try
            {
                var props = await _WebDav.GetPropertiesAsync(req.Source);
                if (props == null)
                    return NotFound(new { success = false, message = "Fichier introuvable." });

                return new JsonResult(new
                {
                    success = true,
                    properties = new
                    {
                        nom = props.GetValueOrDefault("Nom"),
                        taille = props.GetValueOrDefault("Taille"),
                        type = props.GetValueOrDefault("ContentType"),
                        créé = props.GetValueOrDefault("Créé"),
                        modifié = props.GetValueOrDefault("Modifié"),
                        lien = props.GetValueOrDefault("Lien"),
                        accessTier = props.GetValueOrDefault("Tier")
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Erreur lors de la récupération des propriétés.");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }



        private string ExtraireKategorieDepuisTarget(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return "divers";

            var parts = targetPath.Split('/');
            return parts.Length >= 3 ? parts[2] : "divers"; // Ex: dokumente/microplus/**<categorie>**
        }
        private static string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "";

            return path.EndsWith("/") ? path : path + "/";
        }


        public async Task<JsonResult> OnPostDeleteFolderAsync([FromBody] DeleteFolderRequest req)
        {
            Console.WriteLine($"📁 Reçu folderPath: '{req.folderPath}'");

            if (string.IsNullOrWhiteSpace(req.folderPath))
                return new JsonResult(new { success = false, message = "Pfad ist leer." });

            var folderPrefix = EnsureTrailingSlash(req.folderPath);
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            try
            {
     
                // 🔹 FIX: Benutze DeleteFolderAsync statt DeleteFileAsync für Ordner!
                await _WebDav.DeleteFolderAsync(folderPrefix);


                var docs = await _db.Dokumente
                    .Where(d => d.ApplicationUserId == userId && d.ObjectPath.StartsWith(folderPrefix))
                    .ToListAsync();
                _db.Dokumente.RemoveRange(docs);


                var versions = await _db.DokumentVersionen
                    .Where(v => v.ApplicationUserId == userId && v.Dateipfad.StartsWith(folderPrefix))
                    .ToListAsync();
                _db.DokumentVersionen.RemoveRange(versions);

                await _db.SaveChangesAsync();

                Console.WriteLine($"🧹 {docs.Count} Dokumente supprimés.");
                Console.WriteLine($"🧹 {versions.Count} Versionen supprimées.");

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Erreur DeleteFolderAsync : " + ex.Message);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public static string ExtraireCheminRelatif(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;

            int idx = fullPath.IndexOf("/dokumente/");
            return (idx >= 0) ? fullPath.Substring(idx + 1) : null;
        }
        public async Task CopyFolderMetaAsync(string sourceFolder, string targetFolder, string? newName = null)
        {
            // 1️⃣ Zielbasis vorbereiten
            var baseTarget = targetFolder;
            if (!baseTarget.EndsWith("/")) baseTarget += "/";
            if (!string.IsNullOrWhiteSpace(newName))
                baseTarget += newName.TrimEnd('/') + "/";

            // 2️⃣ Alle Dokumente im Quellordner laden (inkl. Metadaten)
            var docs = await _db.Dokumente
                .Include(d => d.MetadatenObjekt)
                .Where(d => d.ObjectPath.StartsWith(sourceFolder))
                .ToListAsync();

            if (docs.Count == 0)
            {
                _logger.LogWarning("⚠️ Keine Dokumente im Quellordner {Folder} gefunden.", sourceFolder);
                return;
            }

            // 3️⃣ Mapping AltId -> NeuId für Versionen-Zuordnung
            var idMapping = new Dictionary<Guid, Guid>();

            foreach (var doc in docs)
            {
                // 🔹 Neuen Speicherpfad berechnen
                var relativePath = doc.ObjectPath.Substring(sourceFolder.Length).TrimStart('/');
                var newObjectPath = baseTarget + relativePath;
                var newId = Guid.NewGuid();
                idMapping[doc.Id] = newId;

                // 🔹 Neues Metadatenobjekt klonen
                Metadaten? newMeta = null;
                if (doc.MetadatenObjekt != null)
                {
                    var meta = doc.MetadatenObjekt;
                    newMeta = new Metadaten
                    {
                        Titel = meta.Titel,
                        Beschreibung = meta.Beschreibung,
                        Kategorie = meta.Kategorie,
                        Stichworte = meta.Stichworte,
                        Rechnungsnummer = meta.Rechnungsnummer,
                        Kundennummer = meta.Kundennummer,
                        Rechnungsbetrag = meta.Rechnungsbetrag,
                        Nettobetrag = meta.Nettobetrag,
                        Steuerbetrag = meta.Steuerbetrag,
                        Gesamtpreis = meta.Gesamtpreis,
                        Rechnungsdatum = meta.Rechnungsdatum,
                        Lieferdatum = meta.Lieferdatum,
                        Faelligkeitsdatum = meta.Faelligkeitsdatum,
                        Zahlungsbedingungen = meta.Zahlungsbedingungen,
                        Lieferart = meta.Lieferart,
                        ArtikelAnzahl = meta.ArtikelAnzahl,
                        SteuerNr = meta.SteuerNr,
                        UIDNummer = meta.UIDNummer,
                        Email = meta.Email,
                        Telefon = meta.Telefon,
                        Telefax = meta.Telefax,
                        IBAN = meta.IBAN,
                        BIC = meta.BIC,
                        Bankverbindung = meta.Bankverbindung,
                        Adresse = meta.Adresse,
                        AbsenderAdresse = meta.AbsenderAdresse,
                        AnsprechPartner = meta.AnsprechPartner,
                        Zeitraum = meta.Zeitraum,
                        PdfAutor = meta.PdfAutor,
                        PdfBetreff = meta.PdfBetreff,
                        PdfSchluesselwoerter = meta.PdfSchluesselwoerter,
                        Website = meta.Website,
                        OCRText = meta.OCRText
                    };
                    _db.Metadaten.Add(newMeta);
                }

                // 🔹 Neues Dokument erstellen
                var newDoc = new Dokumente
                {
                    Id = newId,
                    ApplicationUserId = doc.ApplicationUserId,
                    KundeId = doc.KundeId,
                    Kategorie = doc.Kategorie,
                    Beschreibung = doc.Beschreibung,
                    Titel = doc.Titel,
                    ObjectPath = newObjectPath,
                    Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{newObjectPath}".Replace("//", "/").Replace(":/", "://"),
                    Dateiname = Path.GetFileName(newObjectPath),
                    HochgeladenAm = DateTime.UtcNow,
                    dtStatus = doc.dtStatus,
                    IsIndexed = doc.IsIndexed,
                    IsVersion = doc.IsVersion,
                    OriginalId = doc.OriginalId,
                    EstSigne = doc.EstSigne,
                    AbteilungId = doc.AbteilungId,
                    IsUpdated = true,
                    MetadatenObjekt = newMeta // 🔗 Verknüpfung herstellen
                };

                _db.Dokumente.Add(newDoc);
            }

            // 4️⃣ Versionen kopieren
            var versionen = await _db.DokumentVersionen
                .Where(v => docs.Select(d => d.Id).Contains(v.DokumentId))
                .ToListAsync();

            foreach (var ver in versionen)
            {
                if (!idMapping.TryGetValue(ver.DokumentId, out var newDocId))
                    continue; // sollte nicht vorkommen

                // 🔹 Pfad für die neue Version bestimmen
                var relativePath = ver.Dateipfad.StartsWith(sourceFolder)
                    ? ver.Dateipfad.Substring(sourceFolder.Length).TrimStart('/')
                    : ver.Dateipfad;

                var newVersionPath = baseTarget + relativePath;

                var newVer = new DokumentVersionen
                {
                    DokumentId = newDocId,
                    ApplicationUserId = ver.ApplicationUserId,
                    Dateiname = ver.Dateiname,
                    Dateipfad = newVersionPath,
                    HochgeladenAm = DateTime.UtcNow,
                    EstSigne = ver.EstSigne
                };
                _db.DokumentVersionen.Add(newVer);
            }

            await _db.SaveChangesAsync();
            _logger.LogInformation("✅ {Count} Dokumente mit Metadaten erfolgreich kopiert nach {Target}.", docs.Count, targetFolder);
        }



        public async Task<JsonResult> OnPostCopyFolderAsync([FromBody] CopyRequest req)
        {
            try
            {
             
                await _WebDav.CopyFolderAsync(req.SourcePath, req.TargetPath);

           
                await CopyFolderMetaAsync(req.SourcePath, req.TargetPath, req.NewName);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }


        public async Task<JsonResult> OnPostMoveFolderAsync([FromBody] CopyRequest req)
        {
            try
            {
                await _WebDav.MoveFolderAsync(req.SourcePath, req.TargetPath);
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> OnPostArchiveFileAsync([FromBody] ArchiveRequest req)
        {
            _logger.LogInformation("📦 Archivierung gestartet für Quelle: {Source}", req.Source);

            if (string.IsNullOrWhiteSpace(req.Source))
                return BadRequest(new { success = false, message = "❌ Pfad fehlt." });

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            try
            {
                // =====================================================
                // 🔧 Hilfsfunktionen
                // =====================================================
                string NormalizePath(string path)
                {
                    if (string.IsNullOrWhiteSpace(path)) return "";
                    path = Uri.UnescapeDataString(path);
                    path = path.Replace("\\", "/");

                    // 1. Remove base URL prefix if present (http...)
                    if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = path.IndexOf("/DmsDaten/", StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                            path = path.Substring(idx + "/DmsDaten/".Length);
                    }

                    // 2. Extra check: Remove "DmsDaten" segment if it exists (for non-http paths)
                    int dmsIndex = path.IndexOf("DmsDaten/", StringComparison.OrdinalIgnoreCase);
                    if (dmsIndex >= 0)
                    {
                        path = path.Substring(dmsIndex + "DmsDaten/".Length);
                    }

                    return path.Trim('/');
                }

                bool PathsMatch(string a, string b)
                {
                    if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                        return false;

                    a = Uri.UnescapeDataString(a).Trim().Replace("\\", "/").ToLowerInvariant();
                    b = Uri.UnescapeDataString(b).Trim().Replace("\\", "/").ToLowerInvariant();

                    return a.Equals(b) || a.EndsWith(b) || b.EndsWith(a);
                }

                // =====================================================
                // 🔍 Pfad normalisieren & Dokument suchen
                // =====================================================
                req.Source = NormalizePath(req.Source);
                _logger.LogInformation("🔍 Normalisierter Quellpfad: {Source}", req.Source);

                bool isAdmin = User.IsInRole("Admin") || User.IsInRole("SuperAdmin");

                var alleDokumente = await _db.Dokumente
                    .Include(d => d.Abteilung)
                    .Include(d => d.MetadatenObjekt)
                    .Include(d => d.ApplicationUser)
                    .ToListAsync();

                var doc = alleDokumente.FirstOrDefault(d =>
                    PathsMatch(d.ObjectPath, req.Source) &&
                    (isAdmin || d.ApplicationUserId == userId));

                if (doc == null)
                {
                    _logger.LogWarning("❌ Quelldokument nicht gefunden oder keine Berechtigung: {Source}", req.Source);
                    return new JsonResult(new { success = false, message = "Quelldokument nicht gefunden oder keine Berechtigung." });
                }

                // =====================================================
                // 🏢 Kontextdaten
                // =====================================================
                var firma = doc.ApplicationUser?.FirmenName?.ToLowerInvariant() ?? "unbekannt";
                var abteilungName = doc.Abteilung?.Name?.ToLowerInvariant() ?? "allgemein";
                var kategorie = doc.Kategorie?.ToLowerInvariant() ?? "allgemein";

                _logger.LogInformation("📦 Archivierung gestartet für {Pfad} ({Firma}/{Abteilung}/{Kategorie})",
                    doc.ObjectPath, firma, abteilungName, kategorie);

                // =====================================================
                // 📂 Zielpfad für Archiv
                // =====================================================
                string fileName = Path.GetFileNameWithoutExtension(doc.Dateiname);
                string ext = Path.GetExtension(doc.Dateiname);
                string archivFileName = $"{fileName}_archiviert_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
                string newPath = $"dokumente/{firma}/{abteilungName}/archiv/{archivFileName}";

                _logger.LogInformation("📁 Neuer Archivpfad: {Path}", newPath);

                var sourceMeta = await _db.Metadaten.FirstOrDefaultAsync(m => m.Id == doc.MetadatenId);

                // =====================================================
                // 🧩 Datei verschieben/kopieren
                // =====================================================
                if (!doc.IsChunked)
                {
                    _logger.LogInformation("📄 Archivierung einer normalen Datei: {Source} → {Target}", doc.ObjectPath, newPath);

                    using var stream = await _WebDav.DownloadStreamAsync(doc.ObjectPath);
                    if (stream == null)
                    {
                        _logger.LogWarning("❌ Quelldatei konnte nicht heruntergeladen werden: {Path}", doc.ObjectPath);
                        return new JsonResult(new { success = false, message = "Quelldatei konnte nicht geladen werden." });
                    }

                    stream.Position = 0;

                    await _WebDav.UploadWithMetadataAsync(stream, newPath, "application/pdf", sourceMeta);
                    await _WebDav.DeleteFileAsync(doc.ObjectPath);

                    doc.ObjectPath = newPath;
                    doc.Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{newPath}";
                }
                else
                {
                    string chunkBase = doc.ObjectPath.Replace("chunked://", "").Trim('/');
                    string originalChunksPath = $"dokumente/{firma}/{abteilungName}/{kategorie}/chunks/{chunkBase}";
                    string archiveChunksPath = $"dokumente/{firma}/{abteilungName}/archiv/chunks/{chunkBase}";

                    _logger.LogInformation("🧩 Chunk-Archivierung: {Source} → {Target}", originalChunksPath, archiveChunksPath);

                    var chunkFiles = await _WebDav.ListFilesAsync(originalChunksPath);
                    foreach (var chunkFile in chunkFiles)
                    {
                        try
                        {
                            string newChunkPath = $"{archiveChunksPath}/{Path.GetFileName(chunkFile)}";
                            using var chunkStream = await _WebDav.DownloadStreamAsync($"{originalChunksPath}/{Path.GetFileName(chunkFile)}");
                            await _WebDav.UploadStreamAsync(chunkStream, newChunkPath, "application/octet-stream");

                            bool deleted = await _WebDav.DeleteFileAsync($"{originalChunksPath}/{Path.GetFileName(chunkFile)}");
                            if (!deleted)
                                _logger.LogWarning("⚠️ Chunk konnte nicht gelöscht werden: {Chunk}", chunkFile);
                            else
                                _logger.LogInformation("📦 Chunk verschoben: {Old} → {New}", chunkFile, newChunkPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("⚠️ Fehler beim Archivieren von Chunk {Chunk}: {Msg}", chunkFile, ex.Message);
                        }
                    }

                    doc.ObjectPath = $"chunked://{chunkBase}";
                    doc.Kategorie = "archiv";
                    doc.Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{archiveChunksPath}/chunk_0.bin";
                }

                // =====================================================
                // 🧠 Metadaten überprüfen und klonen
                // =====================================================
                _logger.LogInformation("🧩 Bestehende Metadaten-ID: {MetaId}", doc.MetadatenId);
                if (sourceMeta != null)
                    _logger.LogInformation("🧠 Bestehende Metadaten: {Titel}, {Kategorie}", sourceMeta.Titel, sourceMeta.Kategorie);

                var neueMetadaten = await DocumentPathHelper.CreateFullMetadataFromModelAsync(
                    _db,
                    doc,
                    new DmsProjeckt.Pages.Tests.UploadMultiModel.MetadataModel
                    {
                        Titel = sourceMeta?.Titel ?? doc.Dateiname,
                        Kategorie = "archiv",
                        Beschreibung = $"Archiviert am {DateTime.UtcNow:dd.MM.yyyy HH:mm}",
                        Rechnungsnummer = sourceMeta?.Rechnungsnummer,
                        Kundennummer = sourceMeta?.Kundennummer,
                        Email = sourceMeta?.Email,
                        Telefon = sourceMeta?.Telefon,
                        IBAN = sourceMeta?.IBAN,
                        BIC = sourceMeta?.BIC,
                        Adresse = sourceMeta?.Adresse,
                        Website = sourceMeta?.Website,
                        OCRText = sourceMeta?.OCRText
                    },
                    "Archiv",
                    sourceMeta
                );

                doc.MetadatenId = neueMetadaten.Id;
                doc.MetadatenObjekt = neueMetadaten;

                // =====================================================
                // 🗃️ Archiv-Eintrag in DB
                // =====================================================
                var archivEntity = new Archive
                {
                    DokumentId = doc.Id,
                    ArchivName = doc.Dateiname,
                    ArchivPfad = doc.Dateipfad,
                    FileSizeBytes = doc.FileSizeBytes,
                    ArchivDatum = DateTime.UtcNow,
                    BenutzerId = userId,
                    Grund = "Manuelle Archivierung",
                    MetadatenJson = JsonSerializer.Serialize(neueMetadaten),
                    IstAktiv = false
                };

                _db.Archive.Add(archivEntity);

                // =====================================================
                // 🧾 Dokument aktualisieren
                // =====================================================
                doc.Kategorie = "archiv";
                doc.DokumentStatus = DmsProjeckt.Data.Status.Archiviert;
                doc.dtStatus = DmsProjeckt.Data.DokumentStatus.Fertig;
                doc.IsIndexed = false;
                doc.IsUpdated = true;

                _db.Dokumente.Update(doc);
                await _db.SaveChangesAsync();

                _logger.LogInformation("✅ Dokument erfolgreich archiviert mit Metadaten-ID: {MetaId}", neueMetadaten.DokumentId);
                var user = await _userManager.GetUserAsync(User);
                if (user != null)
                {
                    await _auditLogDokumentService.EnregistrerAsync("📦 Dokument archiviert", user.Id, doc.Id);
                }
                return new JsonResult(new { success = true, message = "✅ Dokument erfolgreich archiviert." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Archivierung fehlgeschlagen für {Source}", req.Source);
        
             
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }


        public async Task<IActionResult> OnPostToggleFavoritAsync([FromBody] FavoriteRequest req)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (req == null || req.DokumentId == Guid.Empty)
                return new JsonResult(new { success = false, message = "Ungültige Anfrage." });
            if (string.IsNullOrEmpty(userId))
                return new JsonResult(new { success = true, message = "User ist leer" });
            var fav = await _db.UserFavoritDokumente
                .FirstOrDefaultAsync(f => f.ApplicationUserId == userId && f.DokumentId == req.DokumentId);

            bool isNowFavorit;

            if (fav == null)
            {
                _db.UserFavoritDokumente.Add(new UserFavoritDokument
                {
                    ApplicationUserId = userId,
                    DokumentId = req.DokumentId,
                });
                isNowFavorit = true;
            }
            else
            {
                _db.UserFavoritDokumente.Remove(fav);
                isNowFavorit = false;
            }

            await _db.SaveChangesAsync();
            return new JsonResult(new { success = true, isFavorit = isNowFavorit });
        }

        public async Task<IActionResult> OnGetGetUsersFromCompanyAsync()
        {
            var currentUserId = _userManager.GetUserId(User);
            var currentUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null || string.IsNullOrEmpty(currentUser.FirmenName))
                return new JsonResult(new { error = "Kein Firmenname gefunden." });

            // Hole alle User mit gleichem Firmennamen (und schließe den aktuellen User aus)
            var users = await _db.Users
                .Where(u => u.FirmenName == currentUser.FirmenName && u.Id != currentUser.Id)
                .Select(u => new { u.Id, Name = u.Vorname + " " + u.Nachname, u.Email })
                .ToListAsync();

            return new JsonResult(users);
        }

        // Diese Methode wird via POST aufgerufen, wenn Dateien geteilt werden sollen
        public async Task<IActionResult> OnPostShareDocumentAsync([FromBody] ShareDocumentInput input)
        {
            var byUserId = _userManager.GetUserId(User);
            if (input == null) return BadRequest("Input ist NULL");
            if (input.UserIds == null) return BadRequest("UserIds ist NULL");
            if (input.DokumentId == null) return BadRequest("DokumentId ist NULL oder leer!");

            var dokument = await _db.Dokumente.FindAsync(input.DokumentId);
            if (dokument == null) return NotFound($"Dokument mit ID '{input.DokumentId}' nicht gefunden.");

            var user = await _db.Users.FindAsync(byUserId);
            if (user == null) return NotFound($"User mit ID '{byUserId}' nicht gefunden.");

            foreach (var userId in input.UserIds)
            {
                var alreadyExists = await _db.UserSharedDocuments
                    .AnyAsync(x => x.DokumentId == input.DokumentId && x.SharedToUserId == userId);

                if (!alreadyExists)
                {
                    _db.UserSharedDocuments.Add(new UserSharedDocument
                    {
                        DokumentId = input.DokumentId,
                        SharedToUserId = userId,
                        SharedAt = DateTime.Now,
                        SharedByUserId = byUserId
                    });
                }

                var notificationType = await _db.NotificationTypes
                    .FirstOrDefaultAsync(n => n.Name == "Doc shared");

                if (notificationType == null) continue; // Oder Fehler werfen

                var setting = await _db.UserNotificationSettings
                    .FirstOrDefaultAsync(s => s.UserId == userId && s.NotificationTypeId == notificationType.Id);

                if (setting == null || setting.Enabled)
                {
                    var notification = new Notification
                    {
                        Title = "Dokument geteilt",
                        Content = $"Dokument \"{dokument.Titel}\" wurde von {user.Vorname} {user.Nachname} mit dir geteilt.",
                        CreatedAt = DateTime.UtcNow,
                        NotificationTypeId = notificationType.Id
                    };
                    _db.Notifications.Add(notification);
                    await _db.SaveChangesAsync();

                    var userNotification = new UserNotification
                    {
                        UserId = userId,
                        NotificationId = notification.Id,
                        IsRead = false,
                        ReceivedAt = DateTime.UtcNow
                    };
                    _db.UserNotifications.Add(userNotification);
                    await _db.SaveChangesAsync();
                }
            }

            await _db.SaveChangesAsync();

            return new JsonResult(new { success = true });
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

        [IgnoreAntiforgeryToken] 
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

        public async Task<JsonResult> OnPostDeleteFile([FromBody] DeleteFileRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.source))
                return new JsonResult(new { success = false, message = "Pfad ist leer." });

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            try
            {
                // 🔧 Normaliser la source
                string normalizedSource = req.source.Trim().Replace("\\", "/").TrimEnd('/');
                _logger.LogInformation("🔍 Suche Dokument mit Source={Source}", normalizedSource);

                // 🔎 Dokument laden
                var dokument = await _db.Dokumente
                    .Include(d => d.Abteilung)
                    .Include(d => d.ApplicationUser)
                    .Where(d => d.ApplicationUserId == userId)
                    .FirstOrDefaultAsync(d =>
                        d.ObjectPath != null &&
                        (d.ObjectPath == normalizedSource ||
                         normalizedSource.Contains(d.ObjectPath) ||
                         d.ObjectPath.Contains(normalizedSource)));

                if (dokument == null)
                {
                    // 🔄 FALLBACK: Prüfen, ob es sich um eine VERSION handelt
                    var versionDoc = await _db.DokumentVersionen
                        .FirstOrDefaultAsync(v => v.ObjectPath == normalizedSource && v.ApplicationUserId == userId);

                    if (versionDoc != null)
                    {
                        _logger.LogInformation("🗑️ Lösche Version: {Path}", versionDoc.ObjectPath);
                        
                        // 1. WebDAV löschen
                        bool deleted = await _WebDav.DeleteFileAsync(versionDoc.ObjectPath);
                        if (!deleted)
                            _logger.LogWarning("⚠️ Version konnte nicht von WebDAV gelöscht werden: {Path}", versionDoc.ObjectPath);

                        // 2. DB löschen
                        _db.DokumentVersionen.Remove(versionDoc);
                        await _db.SaveChangesAsync();

                        _logger.LogInformation("✅ Version erfolgreich gelöscht.");
                        return new JsonResult(new { success = true });
                    }

                    _logger.LogWarning("❌ Dokument nicht gefunden oder keine Berechtigung für {Path}", normalizedSource);
                    return new JsonResult(new { success = false, message = "❌ Dokument nicht gefunden oder keine Berechtigung." });
                }

                // ===============================
                // 🧩 CHUNKED DATEIEN
                // ===============================
                if (dokument.IsChunked || dokument.ObjectPath.StartsWith("chunked://"))
                {
                    string chunkBase = dokument.ObjectPath
                        .Replace("chunked://", "")
                        .Trim('/')
                        .TrimStart('/');

                    _logger.LogInformation("🧩 Lösche alle Chunks unter: {ChunkBase}", chunkBase);

                    var chunkPath = $"dokumente/{chunkBase}";
                    var chunks = await _WebDav.ListFilesAsync(chunkPath);

                    int deletedCount = 0;
                    foreach (var chunk in chunks)
                    {
                        try
                        {
                            string fullChunkPath = $"{chunkPath}/{chunk}".Replace("//", "/");
                            bool deleted = await _WebDav.DeleteFileAsync(fullChunkPath);

                            if (deleted)
                            {
                                deletedCount++;
                                _logger.LogInformation("✅ Chunk gelöscht: {Chunk}", fullChunkPath);
                            }
                            else
                            {
                                _logger.LogWarning("⚠️ Konnte Chunk nicht löschen: {Chunk}", fullChunkPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("⚠️ Fehler beim Löschen von Chunk {Chunk}: {Msg}", chunk, ex.Message);
                        }
                    }

                    _logger.LogInformation("✅ {Count} Chunks gelöscht für {ChunkBase}", deletedCount, chunkBase);

                    // 🔥 Optional : löschen du dossier chunks
                    await _WebDav.DeleteFolderAsync(chunkPath);
                }
                else
                {
                    // ===============================
                    // 🧾 NORMALE DATEI
                    // ===============================
                    _logger.LogInformation("🗑️ Lösche normale Datei: {Path}", dokument.ObjectPath);
                    bool deleted = await _WebDav.DeleteFileAsync(dokument.ObjectPath);

                    if (deleted)
                        _logger.LogInformation("✅ Datei gelöscht: {Path}", dokument.ObjectPath);
                    else
                        _logger.LogWarning("⚠️ Datei konnte nicht gelöscht werden: {Path}", dokument.ObjectPath);
                }

                // ===============================
                // 🗃️ DB-EINTRÄGE LÖSCHEN
                // ===============================
                _db.Dokumente.Remove(dokument);

                var versionen = await _db.DokumentVersionen
                    .Where(v => v.ApplicationUserId == userId && v.DokumentId == dokument.Id)
                    .ToListAsync();

                if (versionen.Any())
                {
                    _db.DokumentVersionen.RemoveRange(versionen);
                    _logger.LogInformation("🧹 {Count} Version(en) gelöscht für Dokument {Id}", versionen.Count, dokument.Id);
                }

                await _db.SaveChangesAsync();

                _logger.LogInformation("✅ Dokument erfolgreich gelöscht: {Pfad}", dokument.ObjectPath);
                var user = await _userManager.GetUserAsync(User);
                if (user != null)
                {
                    await _auditLogDokumentService.EnregistrerAsync("🗑️ Dokument gelöscht", user.Id, dokument.Id);
                }

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Löschen der Datei {Path}", req.source);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostRenameFolderAsync([FromBody] RenameFolderRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.SourcePath) || string.IsNullOrWhiteSpace(req.TargetPath))
                return new JsonResult(new { success = false, message = "Ungültige Anfrage." });

            try
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

                string sourcePath = req.SourcePath.Trim().Replace("\\", "/").TrimEnd('/');
                string targetPath = req.TargetPath.Trim().Replace("\\", "/").TrimEnd('/');

                _logger.LogInformation("📂 Starte Umbenennung: {Source} → {Target}", sourcePath, targetPath);

                // 1️⃣ Kopieren des gesamten Ordners (rekursiv)
                await _WebDav.CopyFolderAsync(sourcePath, targetPath);
                _logger.LogInformation("✅ Ordnerinhalt kopiert: {Source} → {Target}", sourcePath, targetPath);

                // 2️⃣ Löschen des alten Ordners
                bool deleted = await _WebDav.DeleteFolderAsync(sourcePath);
                if (deleted)
                    _logger.LogInformation("🗑️ Alter Ordner gelöscht: {Source}", sourcePath);
                else
                    _logger.LogWarning("⚠️ Konnte alten Ordner nicht löschen: {Source}", sourcePath);

                // 3️⃣ DB-Pfade anpassen
                var docsToUpdate = await _db.Dokumente
                    .Where(d => d.ApplicationUserId == userId && d.ObjectPath.StartsWith(sourcePath))
                    .ToListAsync();

                foreach (var doc in docsToUpdate)
                {
                    // 🔄 Pfad aktualisieren
                    string relativePart = doc.ObjectPath.Substring(sourcePath.Length).TrimStart('/');
                    doc.ObjectPath = $"{targetPath}/{relativePart}".Replace("//", "/");

                    // 🔗 Dateipfad (WebDAV URL)
                    doc.Dateipfad = $"{_WebDav.BaseUrl.TrimEnd('/')}/{doc.ObjectPath}".Replace("//", "/").Replace(":/", "://");

                    _logger.LogInformation("🔁 DB aktualisiert: {OldPath} → {NewPath}", sourcePath, doc.ObjectPath);
                }

                await _db.SaveChangesAsync();

                _logger.LogInformation("✅ Ordner erfolgreich umbenannt: {Source} → {Target}", sourcePath, targetPath);
                return new JsonResult(new { success = true, message = "Ordner erfolgreich umbenannt." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Fehler beim Umbenennen des Ordners: {Message}", ex.Message);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        // ✅ Benutzer aus gleicher Firma holen (für Checkboxen im Modal)
        public async Task<IActionResult> OnGetGetSignableUsersAsync()
        {
            var currentUserId = _userManager.GetUserId(User);
            var currentUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == currentUserId);
            if (currentUser == null || string.IsNullOrEmpty(currentUser.FirmenName))
                return new JsonResult(new { error = "Kein Firmenname gefunden." });

            // Hole alle User mit gleichem Firmennamen (außer mich selbst)
            var users = await _db.Users
                .Where(u => u.FirmenName == currentUser.FirmenName && u.Id != currentUser.Id)
                .Select(u => new UserDto
                {
                    Id = u.Id,
                    FullName = (u.Vorname + " " + u.Nachname).Trim(),
                    Email = u.Email ?? ""
                })
                .ToListAsync();

            return new JsonResult(users);
        }

        // ✅ Signaturanfrage erstellen
        [HttpPost]
        public async Task<IActionResult> OnPostRequestSignatureAsync([FromBody] SignatureRequestDto dto)
        {
            var byUserId = _userManager.GetUserId(User);

            if (dto.FileId == null || dto.UserIds == null || !dto.UserIds.Any())
                return new JsonResult(new { success = false, message = "Ungültige Anfrage." });

            foreach (var userId in dto.UserIds)
            {
                var request = new SignatureRequest
                {
                    FileId = dto.FileId,
                    RequestedUserId = userId,
                    RequestedByUserId = byUserId,
                    RequestedAt = DateTime.UtcNow,
                    Status = "Pending"
                };
                _db.SignatureRequests.Add(request);

                var notificationType = await _db.NotificationTypes
                    .FirstOrDefaultAsync(n => n.Name == "SignRq");

                var setting = await _db.UserNotificationSettings
                    .FirstOrDefaultAsync(n => n.UserId == request.RequestedUserId && n.NotificationTypeId == notificationType.Id);

                var notificationTypeEmail = await _db.NotificationTypes
                    .FirstOrDefaultAsync(n => n.Name == "SignRqEm");

                var settingEmail = await _db.UserNotificationSettings
                    .FirstOrDefaultAsync(n => n.UserId == request.RequestedUserId && n.NotificationTypeId == notificationTypeEmail.Id);


                var doc = await _db.Dokumente
                    .FirstOrDefaultAsync(d => d.Id == dto.FileId);
                var byUser = await _userManager.FindByIdAsync(byUserId);
                if (setting == null || setting.Enabled)
                {

                    var notification = new Notification
                    {
                        Title = "Dokument signieren",
                        Content = $"Für das Dokument \"{doc.Dateiname}\" wurde von {byUser.Vorname} {byUser.Nachname} die Anfrage gestellt, es von Ihnen zu signieren.",
                        CreatedAt = DateTime.UtcNow,
                        NotificationTypeId = notificationType.Id,
                        ActionLink = $"/Signieren"
                    };
                    _db.Notifications.Add(notification);
                    await _db.SaveChangesAsync();

                    var userNotification = new UserNotification
                    {
                        UserId = request.RequestedUserId,
                        NotificationId = notification.Id,
                        IsRead = false,
                        ReceivedAt = DateTime.UtcNow
                    };
                    _db.UserNotifications.Add(userNotification);
                    await _db.SaveChangesAsync();

                }
                if (settingEmail == null || settingEmail.Enabled)
                {
                    var userTo = await _db.Users.FindAsync(request.RequestedUserId);

                    string subject = "Dokument signiert";
                    string body = $@"
                <p>Hallo {userTo.Vorname},</p>
                <p>Für das Dokument <b>""{doc.Dateiname}""</b> wurde von <b>{byUser.Vorname} {byUser.Nachname}</b> die Anfrage gestellt, es von Ihnen zu signieren.</p>
                            < p >< a href = '' > Ansehen </ a ></ p >
            
                            < p > Viele Grüße,< br /> Dein Team </ p > ";

                    await _emailService.SendEmailAsync(userTo.Email, subject, body);
                }
            }

            await _db.SaveChangesAsync();
            return new JsonResult(new { success = true, message = "Signaturanfrage erstellt." });
        }
        [HttpGet]
        public async Task<IActionResult> ChunkedPreview(Guid id)
        {
            var doc = await _db.Dokumente
                .Include(d => d.Abteilung)
                .FirstOrDefaultAsync(d => d.Id == id);

            if (doc == null)
                return NotFound("Dokument nicht gefunden.");

            var user = await _userManager.GetUserAsync(User);
            if (user != null)
            {
                await _auditLogDokumentService.EnregistrerAsync("📄 Dokument geöffnet (Vorschau)", user.Id, doc.Id);
            }

            if (!doc.IsChunked)
                return RedirectToAction("Preview", new { id });


            var reconstructedPath = await _chunkService.ReconstructFileFromWebDavAsync(doc.Id);


            if (string.IsNullOrWhiteSpace(reconstructedPath) || !System.IO.File.Exists(reconstructedPath))
                return Content("❌ Chunked-Datei konnte nicht rekonstruiert werden.");

            var fileStream = System.IO.File.OpenRead(reconstructedPath);
            return File(fileStream, "application/pdf", doc.Dateiname ?? "chunked_document.pdf");
        }


    }
    public class RenameFolderRequest
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
    }
    public class RenameRequest
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string Passwort { get; set; }
    }

    public class CopyRequest
    {
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public string NewName { get; set; } 
    }
    public class FolderItem
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
    }
    public class DeleteFolderRequest
    {
        [JsonPropertyName("folderPath")]
        public string folderPath { get; set; }
    }
    public class ArchiveRequest
    {
        public string Source { get; set; }

    }

    public class FavoriteRequest
    {
        public Guid DokumentId { get; set; }
    }
    public class ShareDocumentInput
    {
        public Guid DokumentId { get; set; }
        public List<string> UserIds { get; set; }
    }
    public class DeleteFileRequest
    {
        public string source { get; set; }
    }
    public class DokumentModel
    {
        public string Kategorie { get; set; }
        public string ObjectPath { get; set; }
        public string Status { get; set; }
        public string BenutzerId { get; set; }
        public DateTime HochgeladenAm { get; set; }
        public string Dateiname { get; set; }
        // Weitere Properties nach Bedarf
    }
    public class CreateExplorerRequest
    {
        public string NewFolder { get; set; }
    }


    public class MoveRequest
    {
        public string Source { get; set; }
        public string Target { get; set; }
        public int? AbteilungId { get; set; }  
        public string? Abteilung { get; set; }
        public string Kategorie { get; set; }  
    }
    public class CopyFileRequest
    {
        public Guid DokumentId { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public int AbteilungId { get; set; }
        public string Kategorie { get; set; }
    }
    public class ExplorerFolder
    {
        public string Name { get; set; }         
        public string Path { get; set; }         
        public List<DmsFile> Files { get; set; } = new(); 
        public List<ExplorerFolder> SubFolders { get; set; } = new(); 
        public bool IsAbteilung { get; set; }


        public IEnumerable<ExplorerFolder> Flatten()
        {
            yield return this;
            foreach (var sub in SubFolders.SelectMany(s => s.Flatten()))
                yield return sub;
        }

    }
    public static class ClaimsExtensions
    {
        public static bool HasAccess(this ClaimsPrincipal user, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // 🔑 Admins dürfen immer alles
            if (user.IsInRole("Admin") || user.IsInRole("SuperAdmin"))
                return true;

            // Normalisieren
            path = path.Trim().Replace("\\", "/");

            var claims = user.FindAll("FolderAccess").Select(c => c.Value);

            foreach (var claim in claims)
            {
                var normalizedClaim = claim.Trim().Replace("\\", "/");

                if (normalizedClaim.EndsWith("/*", StringComparison.OrdinalIgnoreCase))
                {
                    var prefix = normalizedClaim[..^2]; // alles außer "/*"
                    
                    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    if (string.Equals(path, normalizedClaim, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                Console.WriteLine("Rechte", normalizedClaim);
            }

            return false;
        }
    }




    // Models/UserDto.cs
    public class UserDto
    {
        public string Id { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Email { get; set; } = "";
    }

    // Models/SignatureRequestDto.cs
    public class SignatureRequestDto
    {
        public Guid FileId { get; set; } 
        public List<string> UserIds { get; set; } = new();
    }

}
