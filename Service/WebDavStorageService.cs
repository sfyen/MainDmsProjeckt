using DmsProjeckt.Data;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Service
{
    public class WebDavStorageService
    {
        private readonly string _baseUrl;
        private readonly NetworkCredential _credentials;
        private readonly string _username;
        private readonly string _password;
        private readonly HttpClient _client;
        private readonly ApplicationDbContext _db;


        // ✅ Propriété publique : équivalent du "Bucket" Firebase
        public string BaseUrl => _baseUrl;


        public WebDavStorageService(IConfiguration config, ApplicationDbContext db)
        {
            _baseUrl = config["WebDav:BaseUrl"]?.TrimEnd('/')
                ?? throw new ArgumentNullException("WebDav:BaseUrl", "⚠️ WebDav:BaseUrl fehlt in appsettings.json");

            _username = config["WebDav:Username"]
                ?? throw new ArgumentNullException("WebDav:Username", "⚠️ WebDav:Username fehlt in appsettings.json");

            _password = config["WebDav:Password"]
                ?? throw new ArgumentNullException("WebDav:Password", "⚠️ WebDav:Password fehlt in appsettings.json");


            _credentials = new NetworkCredential(_username, _password);
            _db = db;

            // ✅ Initialisation correcte de HttpClient avec authentification WebDAV
            var handler = new HttpClientHandler
            {
                Credentials = _credentials,
                PreAuthenticate = true
            };

            _client = new HttpClient(handler)
            {
                BaseAddress = new Uri(_baseUrl)
            };

            // (Optionnel) Ajouter un User-Agent pour compatibilité WebDAV
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("DMS-Project-WebDavClient/1.0");

            Console.WriteLine($"✅ WebDavStorageService mit Basis-URL initialisiert: {_baseUrl}");

        }

        public async Task<List<DmsFolder>> BuildExplorerTreeAsync(string rootPath, List<Dokumente> allDocs)
        {
            Console.WriteLine($"🚀 [START] BuildExplorerTreeAsync (WebDAV) | rootPath={rootPath}");

            var explorerTree = new List<DmsFolder>();
            int totalFiles = 0, linkedMeta = 0;

            // ✅ Root normalisieren
            rootPath = string.IsNullOrWhiteSpace(rootPath)
                ? "dokumente"
                : rootPath.Trim('/', '\\');

            _db.ChangeTracker.Clear();

            // 🧩 Metadaten laden
            var dokumentIds = allDocs.Select(d => d.Id).ToList();
            var allMetas = await _db.Metadaten
                .AsNoTracking()
                .Where(m => m.DokumentId != null && dokumentIds.Contains(m.DokumentId.Value))
                .ToListAsync();

            var metaDict = allMetas
                .GroupBy(m => m.DokumentId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.Id).First());



            foreach (var doc in allDocs)
            {
                if (metaDict.TryGetValue(doc.Id, out var meta))
                {
                    doc.MetadatenObjekt = meta;
                    linkedMeta++;
                }
            }

            Console.WriteLine($"✅ Metadaten erfolgreich verknüpft: {linkedMeta}/{allDocs.Count}");

            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ❗ Zeige ALLE Dateien, keine Filterbeschränkung
            allDocs = allDocs
                .Where(d => !string.IsNullOrEmpty(d.ObjectPath))
                .ToList();

            foreach (var doc in allDocs)
            {
                if (string.IsNullOrWhiteSpace(doc.ObjectPath))
                    continue;

                // 🔧 Pfad normalisieren (robust)
                var normalizedPath = doc.ObjectPath
                    .Replace("\\", "/")
                    .TrimStart('/');

                if (normalizedPath.StartsWith(_baseUrl, StringComparison.OrdinalIgnoreCase))
                    normalizedPath = normalizedPath.Substring(_baseUrl.Length).TrimStart('/');

                int idx = normalizedPath.IndexOf("dokumente/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    normalizedPath = normalizedPath.Substring(idx);

                if (!normalizedPath.StartsWith("dokumente/", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"⚠️ Überspringe ungültigen Pfad: {normalizedPath}");
                    continue;
                }

                if (!seenPaths.Add(normalizedPath))
                    continue;

                string fileName = Path.GetFileName(normalizedPath);
                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                // 🔧 Kategorie "versionen" automatisch korrigieren
                if (doc.Kategorie?.Equals("versionen", StringComparison.OrdinalIgnoreCase) == true && doc.OriginalId != null)
                {
                    var original = await _db.Dokumente.AsNoTracking().FirstOrDefaultAsync(d => d.Id == doc.OriginalId);
                    if (original != null && !string.IsNullOrEmpty(original.Kategorie))
                    {
                        doc.Kategorie = original.Kategorie;
                        Console.WriteLine($"📂 Kategorie korrigiert: versionen → {original.Kategorie}");
                    }
                }

                // 🧠 Struktur erkennen (firma/abteilung/kategorie)
                string firma = "unbekannt";
                string abteilung = "allgemein";
                string kategorie = "ohne_kategorie";

                var parts = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

                // 🧩 Reconstructed-Ordner entfernen
                if (normalizedPath.Contains("/reconstructed/", StringComparison.OrdinalIgnoreCase))
                {
                    var reconstructedIndex = Array.IndexOf(parts, "reconstructed");
                    if (reconstructedIndex > 0)
                    {
                        parts = parts.Where((p, idx2) => idx2 != reconstructedIndex).ToArray();
                        normalizedPath = string.Join('/', parts);
                    }
                }

                if (parts.Length >= 3 && parts[0].Equals("dokumente", StringComparison.OrdinalIgnoreCase))
                {
                    firma = parts.ElementAtOrDefault(1) ?? firma;
                    abteilung = parts.ElementAtOrDefault(2) ?? abteilung;
                    kategorie = parts.ElementAtOrDefault(3) ?? "ohne_kategorie";
                }

                string abteilungPath = $"dokumente/{firma}/{abteilung}";

                // ⚙️ Versionen-Ordner erkennen
                bool isVersionFolder = normalizedPath.Contains("/versionen/", StringComparison.OrdinalIgnoreCase);
                string parentPath = isVersionFolder ? abteilungPath : $"{abteilungPath}/{kategorie}";

                // 🏗️ 1️⃣ Abteilung
                var abteilungFolder = explorerTree
                    .FirstOrDefault(f => f.Path.Equals(abteilungPath, StringComparison.OrdinalIgnoreCase));
                if (abteilungFolder == null)
                {
                    abteilungFolder = new DmsFolder
                    {
                        Name = abteilung,
                        Path = abteilungPath,
                        IsAbteilung = true,
                        Icon = "fas fa-building text-info",
                        Files = new List<DmsFile>(),
                        SubFolders = new List<DmsFolder>()
                    };
                    explorerTree.Add(abteilungFolder);
                }

                // 🏗️ 2️⃣ Kategorie oder Versionen-Ordner
                var parentFolder = abteilungFolder.SubFolders
                    .FirstOrDefault(f => f.Path.Equals(parentPath, StringComparison.OrdinalIgnoreCase));
                if (parentFolder == null)
                {
                    parentFolder = new DmsFolder
                    {
                        Name = isVersionFolder ? "Versionen" : kategorie,
                        Path = parentPath,
                        IsAbteilung = false,
                        Icon = isVersionFolder ? "bi bi-layers text-success" : "bi bi-folder-fill text-warning",
                        Files = new List<DmsFile>(),
                        SubFolders = new List<DmsFolder>()
                    };
                    abteilungFolder.SubFolders.Add(parentFolder);
                }

                // 🧩 Datei hinzufügen
                var meta = doc.MetadatenObjekt;
                string extension = Path.GetExtension(fileName).ToLowerInvariant();

                // 🎨 Dynamische Icons pro Dateityp
                string icon = extension switch
                {
                    ".pdf" => "bi bi-file-earmark-pdf text-danger",
                    ".png" => "bi bi-file-image text-info",
                    ".jpg" => "bi bi-file-image text-primary",
                    ".jpeg" => "bi bi-file-image text-primary",
                    ".doc" or ".docx" => "bi bi-file-earmark-word text-primary",
                    ".xls" or ".xlsx" => "bi bi-file-earmark-excel text-success",
                    ".txt" or ".csv" => "bi bi-file-earmark-text text-secondary",
                    ".zip" or ".rar" => "bi bi-file-earmark-zip text-warning",
                    _ => "bi bi-file-earmark text-secondary"
                };

                // 🔄 Spezielle Icons für Versionen oder Rekonstruktion
                if (normalizedPath.Contains("/versionen/", StringComparison.OrdinalIgnoreCase))
                    icon = "bi bi-layers text-success";
                if (normalizedPath.Contains("/reconstructed/", StringComparison.OrdinalIgnoreCase))
                    icon = "bi bi-arrow-repeat text-warning";

                var fileEntry = new DmsFile
                {
                    Id = doc.Id.ToString(),
                    GuidId = doc.Id,
                    Name = fileName,
                    Path = normalizedPath,
                    ObjectPath = normalizedPath,
                    Kategorie = kategorie,
                    AbteilungName = abteilung,
                    Beschreibung = meta?.Beschreibung ?? doc.Beschreibung ?? "",
                    Titel = meta?.Titel ?? doc.Titel ?? Path.GetFileNameWithoutExtension(fileName),
                    HochgeladenAm = doc.HochgeladenAm == default ? DateTime.UtcNow : doc.HochgeladenAm,
                    Status = doc.dtStatus.ToString(),
                    MetadatenObjekt = meta,
                    SasUrl = $"{_baseUrl.TrimEnd('/')}/{normalizedPath}",
                    Icon = icon
                };

                parentFolder.Files.Add(fileEntry);
                totalFiles++;

                Console.WriteLine($"📄 Datei hinzugefügt: {fileEntry.Name} ({normalizedPath})");
            }

            Console.WriteLine($"✅ Fertig! Dateien={totalFiles}");
            return explorerTree.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }


        public async Task<List<DmsFile>> GetDocumentsByFolderAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return new List<DmsFile>();

            folderPath = folderPath.TrimEnd('/') + "/"; // s'assurer du slash à la fin

            var docs = new List<DmsFile>();

            // 🔹 Charger tous les documents depuis la base pour récupérer les métadonnées et l'abteilung
            var dbDocs = await _db.Dokumente
                .Include(d => d.Abteilung)
                .ToListAsync();

            // 🔹 Créer un dictionnaire rapide pour lookup des documents
            var dbLookup = dbDocs
                .GroupBy(d => d.ObjectPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // 🔹 Récupérer la liste des fichiers depuis WebDAV
            var webDavFiles = await ListFilesAsync(folderPath);

            foreach (var fileName in webDavFiles)
            {
                // ⚠️ On ignore les dossiers (les fichiers WebDAV n'ont pas de slash à la fin)
                if (string.IsNullOrWhiteSpace(fileName) || fileName.EndsWith("/"))
                    continue;

                var fullPath = $"{folderPath}{fileName}";
                dbLookup.TryGetValue(fullPath, out var dbDoc);

                string abtName = null;
                if (dbDoc != null)
                {
                    if (dbDoc.Abteilung != null)
                        abtName = dbDoc.Abteilung.Name;
                    else if (dbDoc.AbteilungId != null)
                    {
                        var abt = await _db.Abteilungen.FindAsync(dbDoc.AbteilungId);
                        abtName = abt?.Name ?? "Allgemein";
                    }
                }

                var file = new DmsFile
                {
                    Id = dbDoc?.Id.ToString() ?? Guid.NewGuid().ToString(),
                    GuidId = dbDoc?.Id,
                    Name = Path.GetFileName(fileName),
                    Path = fullPath,
                    ObjectPath = fullPath,
                    SasUrl = GenerateSignedUrl(fullPath, 15),
                    Kategorie = dbDoc?.Kategorie,
                    AbteilungName = abtName,
                    Beschreibung = dbDoc?.Beschreibung,
                    Titel = dbDoc?.Titel,
                    HochgeladenAm = dbDoc?.HochgeladenAm,
                    Status = dbDoc?.dtStatus.ToString(),
                    IsIndexed = dbDoc?.IsIndexed,
                    IsVersion = dbDoc?.IsVersion ?? false,
                    EstSigne = dbDoc?.EstSigne ?? false
                };

                docs.Add(file);
            }

            return docs.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public string GenerateSignedUrl(string objectPath, int minutes = 10)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(objectPath))
                    return null;

                // 🔧 Nettoyer le chemin
                objectPath = objectPath.TrimStart('/');

                // 🔹 Construire l’URL complète
                var baseUrl = _baseUrl.TrimEnd('/');
                var url = $"{baseUrl}/{objectPath}";

                // 🔐 Optionnel : si ton WebDAV requiert un accès avec identifiants dans l’URL (non recommandé en production)
                // Exemple : https://username:password@server/dokumente/...
                // var credentials = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_username}:{_password}"));
                // url = $"{baseUrl}/{objectPath}?auth={credentials}";

                // 🕒 WebDAV ne gère pas d’expiration native, mais on peut ajouter un timestamp "virtuel"
                var expiration = DateTime.UtcNow.AddMinutes(minutes).ToString("yyyyMMddHHmmss");
                url += $"?expires={expiration}";

                return url;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Generieren der URL für {objectPath}: {ex.Message}");
                return null;
            }
        }

        public async Task<Stream> DownloadChunkAsync(string firebasePath)
        {
            try
            {
                // ⚙️ Verwende die bestehende Methode DownloadStreamAsync()
                var stream = await DownloadStreamAsync(firebasePath);

                if (stream == null)
                    throw new Exception($"❌ Chunk auf Firebase nicht gefunden: {firebasePath}");

                Console.WriteLine($"📥 Chunk von Firebase heruntergeladen: {firebasePath}");
                return stream;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Herunterladen des Chunks {firebasePath}: {ex.Message}");
                throw;
            }
        }


        public async Task FinalizeChunkedUploadAsync(Guid dokumentId)
        {
            // 1️⃣ Charger le document
            var dokument = await _db.Dokumente
                .Include(d => d.ApplicationUser)
                .Include(d => d.Abteilung)
                .FirstOrDefaultAsync(d => d.Id == dokumentId);

            if (dokument == null)
            {
                Console.WriteLine($"❌ Dokument mit ID {dokumentId} nicht gefunden.");
                return;
            }
            // 🧩 Nouvelle détection plus fiable
            bool isChunkedPath = !string.IsNullOrEmpty(dokument.ObjectPath) &&
                                 dokument.ObjectPath.Contains("/chunks/", StringComparison.OrdinalIgnoreCase);

            if (!dokument.IsChunked && !isChunkedPath)
            {
                Console.WriteLine($"ℹ️ Dokument {dokument.Dateiname} scheint nicht chunked zu sein ({dokument.ObjectPath}).");
                return;
            }


            string firma = dokument.ApplicationUser?.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";
            string abteilung = dokument.Abteilung?.Name?.Trim().ToLowerInvariant() ?? "allgemein";
            string category = dokument.Kategorie?.Trim().ToLowerInvariant() ?? "allgemein";

            string targetPath = $"dokumente/{firma}/{abteilung}/{category}/{dokument.Dateiname}";
            Console.WriteLine($"🔄 Starte Rekonstruktion von {dokument.Dateiname} → Ziel: {targetPath}");

            // 2️⃣ Lire les chunks depuis WebDAV
            var chunks = await _db.DokumentChunks
                .Where(c => c.DokumentId == dokumentId)
                .OrderBy(c => c.Index)
                .ToListAsync();

            if (chunks.Count == 0)
            {
                Console.WriteLine($"⚠️ Keine Chunks für {dokument.Dateiname} gefunden.");
                return;
            }

            // 3️⃣ Dossier temporaire
            string tempDir = Path.Combine(Path.GetTempPath(), "DMS_Reconstructed");
            Directory.CreateDirectory(tempDir);
            string tempFilePath = Path.Combine(tempDir, dokument.Dateiname);

            Console.WriteLine($"📂 Temporäre Datei: {tempFilePath}");

            // 4️⃣ Reconstruction locale
            await using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write))
            {
                foreach (var chunk in chunks)
                {
                    try
                    {
                        using var chunkStream = await DownloadStreamStableAsync(chunk.FirebasePath);
                        if (chunkStream == null)
                        {
                            Console.WriteLine($"⚠️ Chunk nicht gefunden: {chunk.FirebasePath}");
                            continue;
                        }

                        await chunkStream.CopyToAsync(fileStream);
                        Console.WriteLine($"🧩 Chunk {chunk.Index} hinzugefügt ({chunk.Size} Bytes)");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Fehler beim Chunk {chunk.Index}: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"✅ Lokale PDF-Rekonstruktion abgeschlossen: {tempFilePath}");

            // 5️⃣ Upload vers WebDAV
            await using (var uploadStream = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read))
            {
                bool uploadOk = await UploadStreamAsync(uploadStream, targetPath, "application/pdf");
                if (!uploadOk)
                {
                    Console.WriteLine($"❌ Upload fehlgeschlagen für {targetPath}");
                    return;
                }
            }

            // 6️⃣ Vérifier que le fichier est bien sur WebDAV
            bool fileExists = await FileExistsAsync(targetPath);
            if (!fileExists)
            {
                Console.WriteLine($"⚠️ PDF nicht gefunden auf WebDAV nach Upload: {targetPath}");
                return;
            }

            // 7️⃣ Mise à jour du document en base
            dokument.ObjectPath = targetPath;
            dokument.Dateipfad = $"{BaseUrl.TrimEnd('/')}/{targetPath}";
            dokument.IsChunked = false;
            dokument.dtStatus = DokumentStatus.Fertig;

            await _db.SaveChangesAsync();

            Console.WriteLine($"🎯 Dokument {dokument.Dateiname} aktualisiert → {dokument.ObjectPath}");
        }


        public async Task<List<string>> ListFoldersAsync(string remotePath)
        {
            var folders = new List<string>();
            try
            {
                remotePath = NormalizeWebDavPath(remotePath);

                Console.WriteLine($"🔍 [WebDAV] ListFoldersAsync → {remotePath}");

                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), $"{_baseUrl}/{remotePath}");
                request.Headers.Add("Depth", "1");

                string propfindXml = @"<?xml version=""1.0"" encoding=""utf-8"" ?>
            <propfind xmlns=""DAV:"">
                <prop>
                    <displayname />
                    <resourcetype />
                </prop>
            </propfind>";

                request.Content = new StringContent(propfindXml, Encoding.UTF8, "text/xml");

                var byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                _client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                var response = await _client.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync();
                var xdoc = XDocument.Parse(xml);
                XNamespace ns = "DAV:";

                foreach (var responseNode in xdoc.Descendants(ns + "response"))
                {
                    var href = responseNode.Descendants(ns + "href").FirstOrDefault()?.Value ?? "";
                    var name = responseNode.Descendants(ns + "displayname").FirstOrDefault()?.Value ?? "";
                    var isCollection = responseNode.Descendants(ns + "resourcetype")
                                                   .Descendants(ns + "collection").Any();

                    if (!isCollection) continue;

                    // 🛠️ Fallback: Wenn DisplayName leer ist, nehme den Namen aus href
                    if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(href))
                    {
                        var decodedHref = Uri.UnescapeDataString(href).TrimEnd('/');
                        name = Path.GetFileName(decodedHref);
                    }

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // 🛡️ Root-Ordner (sich selbst) ausschließen
                    // Wir vergleichen das Ende des Hrefs mit dem RemotePath
                    string decodedHrefForCheck = Uri.UnescapeDataString(href).TrimEnd('/');
                    string trimmedRemote = remotePath.TrimEnd('/');

                    Console.WriteLine($"🔍 Prüfe: '{decodedHrefForCheck}' gegen Remote: '{trimmedRemote}'");

                    if (decodedHrefForCheck.EndsWith(trimmedRemote, StringComparison.OrdinalIgnoreCase))
                    {
                        bool isRoot = false;
                        if (decodedHrefForCheck.Length == trimmedRemote.Length)
                            isRoot = true;
                        else if (decodedHrefForCheck.Length > trimmedRemote.Length)
                        {
                            string normHref = NormalizeWebDavPath(decodedHrefForCheck);
                            Console.WriteLine($"   ➡️ Normalisiert: '{normHref}'");
                            if (normHref.Equals(trimmedRemote, StringComparison.OrdinalIgnoreCase))
                                isRoot = true;
                        }

                        if (isRoot)
                        {
                            Console.WriteLine($"   🚫 Root erkannt und ignoriert: {name}");
                            continue;
                        }
                    }

                    // WICHTIG: Wir geben nur den Namen zurück, NICHT den vollen Pfad.
                    Console.WriteLine($"   ✅ Ordner akzeptiert: {name}");
                    folders.Add(name);
                }

                Console.WriteLine($"📂 {folders.Count} Ordner gefunden in {remotePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in ListFoldersAsync: {ex.Message}");
            }

            return folders;
        }

        public async Task<bool> UploadStreamAsync(Stream fileStream, string relativePath, string contentType = "application/octet-stream")
        {
            try
            {
                if (fileStream == null || fileStream.Length == 0)
                {
                    Console.WriteLine("⚠️ UploadStreamAsync: Leerer Stream.");
                    return false;
                }

                string uploadUrl = $"{_baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
                string folder = Path.GetDirectoryName(relativePath)?.Replace("\\", "/") ?? "";

                // 🧱 1️⃣ Übergeordnete Ordner erstellen, falls erforderlich
                await EnsureFolderTreeExistsAsync(folder);

                Console.WriteLine($"📤 Upload nach WebDAV: {uploadUrl}");

                using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                {
                    Content = new StreamContent(fileStream)
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", auth);

                // 🧠 2️⃣ Intelligenter Retry (bei 409 Conflict)
                for (int i = 0; i < 2; i++)
                {
                    var response = await _client.SendAsync(request);

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"✅ Upload erfolgreich: {uploadUrl}");
                        return true;
                    }

                    if (response.StatusCode == HttpStatusCode.Conflict)
                    {
                        Console.WriteLine("⚠️ 409-Konflikt erkannt → versuche, den Ordner erneut zu erstellen...");
                        await EnsureFolderTreeExistsAsync(folder);
                        await Task.Delay(500);
                        continue;
                    }

                    string resp = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Upload fehlgeschlagen ({response.StatusCode}): {resp}");
                    return false;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ausnahme in UploadStreamAsync: {ex.Message}");
                return false;
            }
        }



        public async Task EnsureFolderTreeExistsAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            try
            {
                var segments = folderPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                var currentPath = "";

                using var handler = new HttpClientHandler { Credentials = _credentials };
                using var client = new HttpClient(handler);

                foreach (var segment in segments)
                {
                    currentPath = string.IsNullOrEmpty(currentPath)
                        ? segment
                        : $"{currentPath}/{segment}";

                    var fullUrl = $"{_baseUrl.TrimEnd('/')}/{currentPath}"
                        .Replace("//", "/")
                        .Replace(":/", "://");

                    // Überprüfen, ob der Ordner bereits existiert
                    var headReq = new HttpRequestMessage(HttpMethod.Head, fullUrl);
                    var headResp = await client.SendAsync(headReq);

                    if (headResp.IsSuccessStatusCode)
                    {
                        continue; // Ordner existiert bereits
                    }

                    // Andernfalls versuchen wir, ihn zu erstellen
                    var mkcolReq = new HttpRequestMessage(new HttpMethod("MKCOL"), fullUrl);
                    var mkcolResp = await client.SendAsync(mkcolReq);

                    if (mkcolResp.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"📁 Ordner erstellt: {fullUrl}");
                    }
                    else if (mkcolResp.StatusCode == HttpStatusCode.MethodNotAllowed)
                    {
                        Console.WriteLine($"📂 Ordner bereits vorhanden: {fullUrl}");
                    }
                    else if (mkcolResp.StatusCode == HttpStatusCode.Conflict)
                    {
                        Console.WriteLine($"⚠️ MKCOL-Konflikt: Übergeordneter Ordner fehlt → erneuter Versuch für {fullUrl}");
                        await Task.Delay(500);
                        await EnsureFolderTreeExistsAsync(Path.GetDirectoryName(currentPath)?.Replace("\\", "/"));
                        await client.SendAsync(mkcolReq);
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ MKCOL fehlgeschlagen ({mkcolResp.StatusCode}) für {fullUrl}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in EnsureFolderTreeExistsAsync: {ex.Message}");
            }
        }






        // ============================================================
        // ✅ Générer une URL de téléchargement (non signée)
        // ============================================================
        public Task<string> GetDownloadUrlAsync(string remotePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath))
                    return Task.FromResult(string.Empty);

                remotePath = remotePath.TrimStart('/');
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/{remotePath}";
                return Task.FromResult(fullUrl);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in GetDownloadUrlAsync: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }
        public async Task<Dictionary<string, object>> GetPropertiesAsync(string path)
        {
            try
            {
                // 🧩 Vollständige URL erstellen
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

                // 🔹 WebDAV PROPFIND-Anfrage erstellen
                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), fullUrl);
                request.Headers.Add("Depth", "0");

                // 🔐 Authentifizierung
                var authHeader = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                _client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);

                // Standard-WebDAV-XML zum Anfordern von Metadaten
                string propfindXml = """
<?xml version="1.0" encoding="UTF-8"?>
<propfind xmlns="DAV:">
  <prop>
    <displayname />
    <getcontentlength />
    <getcontenttype />
    <creationdate />
    <getlastmodified />
  </prop>
</propfind>
""";

                request.Content = new StringContent(propfindXml, System.Text.Encoding.UTF8, "text/xml");

                // 🧠 Anfrage senden
                var response = await _client.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync();

                // 🔍 XML-Eigenschaften auslesen
                var result = new Dictionary<string, object>();

                // Name
                var nameMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<d:displayname>(.*?)</d:displayname>");
                if (nameMatch.Success)
                    result["Name"] = nameMatch.Groups[1].Value;

                // Größe
                var sizeMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<d:getcontentlength>(\d+)</d:getcontentlength>");
                if (sizeMatch.Success)
                    result["Größe"] = long.TryParse(sizeMatch.Groups[1].Value, out var size) ? size : 0;

                // MIME-Typ
                var typeMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<d:getcontenttype>(.*?)</d:getcontenttype>");
                result["Inhaltstyp"] = typeMatch.Success ? typeMatch.Groups[1].Value : "application/octet-stream";

                // Erstellungs- und Änderungsdatum
                var createdMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<d:creationdate>(.*?)</d:creationdate>");
                var modifiedMatch = System.Text.RegularExpressions.Regex.Match(xml, @"<d:getlastmodified>(.*?)</d:getlastmodified>");

                result["Erstellt"] = createdMatch.Success ? createdMatch.Groups[1].Value : "unbekannt";
                result["Geändert"] = modifiedMatch.Success ? modifiedMatch.Groups[1].Value : "unbekannt";

                // Zugriffslink (lokal generiert)
                result["Link"] = GenerateSignedUrl(path);

                // Optional: Speicherklasse (Platzhalter, da WebDAV keine StorageClass besitzt)
                result["Speicherklasse"] = "Standard";

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in GetPropertiesAsync für {path}: {ex.Message}");
                return null;
            }
        }

        // ===============================================
        // ✅ ROBUSTE VERSION – ListFilesAsync
        // ===============================================
        public async Task<List<string>> ListFilesAsync(string remotePath)
        {
            var files = new List<string>();
            try
            {
                remotePath = NormalizeWebDavPath(remotePath);

                // 🔹 sicherstellen, dass Ordner mit / endet
                if (!remotePath.EndsWith("/"))
                    remotePath += "/";

                var requestUrl = $"{_baseUrl.TrimEnd('/')}/{remotePath.TrimStart('/')}";
                Console.WriteLine($"🔍 [WebDAV] ListFilesAsync → {requestUrl}");

                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUrl);
                request.Headers.Add("Depth", "1");

                string propfindXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
        <propfind xmlns=""DAV:"">
            <prop>
                <displayname />
                <resourcetype />
            </prop>
        </propfind>";

                request.Content = new StringContent(propfindXml, Encoding.UTF8, "text/xml");

                var byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                _client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                var response = await _client.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var xml = await response.Content.ReadAsStringAsync();
                var xdoc = XDocument.Parse(xml);
                XNamespace ns = "DAV:";

                foreach (var responseNode in xdoc.Descendants(ns + "response"))
                {
                    var href = responseNode.Descendants(ns + "href").FirstOrDefault()?.Value ?? "";
                    var name = responseNode.Descendants(ns + "displayname").FirstOrDefault()?.Value ?? "";
                    var isCollection = responseNode.Descendants(ns + "resourcetype")
                                                   .Descendants(ns + "collection").Any();

                    // wenn displayname leer → Dateiname aus href extrahieren
                    if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(href))
                        name = Path.GetFileName(Uri.UnescapeDataString(href));

                    if (!isCollection && !string.IsNullOrWhiteSpace(name))
                    {
                        var fullPath = $"{remotePath.TrimEnd('/')}/{name}";
                        files.Add(name);

                    }
                }

                Console.WriteLine($"➡️ Vollständige URL verwendet: {requestUrl}");
                Console.WriteLine($"📄 {files.Count} Dateien gefunden in {remotePath}");
                foreach (var f in files)
                    Console.WriteLine($"   🧩 {f}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in ListFilesAsync: {ex.Message}");
            }

            return files;
        }

        private string NormalizeWebDavPath(string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
                return ""; // racine de DmsDaten

            remotePath = remotePath.TrimStart('/');

            // ❌ NE PAS préfixer par DmsDaten — _baseUrl le contient déjà
            return remotePath.TrimEnd('/');
        }




        // ✅ Löschen einer Datei
        public async Task<bool> DeleteFileAsync(string remotePath)
        {
            try
            {
                var fullUrl = $"{_baseUrl}/{remotePath}".Replace("//", "/").Replace(":/", "://");

                using var handler = new HttpClientHandler { Credentials = _credentials };
                using var client = new HttpClient(handler);

                var response = await client.DeleteAsync(fullUrl);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"🗑️ Datei gelöscht: {fullUrl}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"⚠️ Löschvorgang fehlgeschlagen ({response.StatusCode}): {fullUrl}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in DeleteFileAsync: {ex.Message}");
                return false;
            }
        }



        public async Task<bool> CopyFilesAsync(Guid dokumentId, string oldPath, string newPath)
        {
            try
            {
                var dokument = await _db.Dokumente.FirstOrDefaultAsync(d => d.Id == dokumentId);
                if (dokument == null)
                {
                    Console.WriteLine($"❌ Dokument {dokumentId} nicht gefunden.");
                    return false;
                }

                // Pfade bereinigen
                string cleanOld = oldPath.TrimStart('/');
                string cleanNew = newPath.TrimStart('/');

                string sourceUrl = $"{_baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(cleanOld).Replace("%2F", "/")}";
                string destUrl = $"{_baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(cleanNew).Replace("%2F", "/")}";

                Console.WriteLine($"📦 [COPY] {sourceUrl}");
                Console.WriteLine($"➡️  nach {destUrl}");

                using var handler = new HttpClientHandler { Credentials = _credentials };
                using var client = new HttpClient(handler);

                // 1️⃣ Quelldatei herunterladen
                var downloadResponse = await client.GetAsync(sourceUrl);
                if (!downloadResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"❌ Datei konnte nicht heruntergeladen werden {sourceUrl}: {downloadResponse.StatusCode}");
                    return false;
                }

                var fileBytes = await downloadResponse.Content.ReadAsByteArrayAsync();

                // 2️⃣ Fehlende übergeordnete Ordner vor dem Upload erstellen
                string directory = Path.GetDirectoryName(cleanNew)?.Replace("\\", "/") ?? "";
                await EnsureFolderTreeExistsAsync(directory);

                // 3️⃣ Datei hochladen
                using var content = new ByteArrayContent(fileBytes);
                var uploadResponse = await client.PutAsync(destUrl, content);

                if (!uploadResponse.IsSuccessStatusCode)
                {
                    string err = await uploadResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ Fehler beim Hochladen: {uploadResponse.StatusCode} - {err}");
                    return false;
                }

                Console.WriteLine($"✅ Datei erfolgreich nach {destUrl} kopiert");

                // 4️⃣ Datenbank aktualisieren
                dokument.ObjectPath = newPath;
                dokument.Dateipfad = destUrl;
                dokument.IsChunked = false;
                dokument.dtStatus = DokumentStatus.Fertig;

                await _db.SaveChangesAsync();
                Console.WriteLine($"🧩 Datenbank aktualisiert: {dokument.Dateipfad}");

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in CopyFilesAsync: {ex.Message}");
                return false;
            }
        }


        private string EnsureTrailingSlash(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return path.EndsWith("/") ? path : path + "/";
        }

        public async Task CopyFolderAsync(string sourcePath, string targetPath)
        {
            try
            {
                sourcePath = EnsureTrailingSlash(sourcePath);
                targetPath = EnsureTrailingSlash(targetPath);

                Console.WriteLine($"📂 [WebDAV] Kopiere Ordner: {sourcePath} → {targetPath}");

                // 🔐 Authentification Basic
                var auth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                _client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", auth);

                // 🧩 Lister tous les fichiers du dossier source
                var files = await ListFilesAsync(sourcePath);
                if (files == null || files.Count == 0)
                {
                    Console.WriteLine($"⚠️ Keine Dateien im Ordner gefunden: {sourcePath}");
                    return;
                }

                foreach (var fileName in files)
                {
                    var srcFilePath = $"{sourcePath}{fileName}";
                    var destFilePath = $"{targetPath}{fileName}";

                    Console.WriteLine($"➡️ Kopiere Datei: {srcFilePath} → {destFilePath}");

                    // 🔽 Télécharger le fichier depuis WebDAV
                    using var srcStream = await DownloadStreamAsync(srcFilePath);
                    if (srcStream == null)
                    {
                        Console.WriteLine($"⚠️ Datei konnte nicht geladen werden: {srcFilePath}");
                        continue;
                    }

                    // 🔼 Envoyer le fichier vers la destination
                    await UploadStreamAsync(srcStream, destFilePath, "application/octet-stream");
                }
                //  Console.WriteLine($"➡️ [ListFilesAsync] URL complète utilisée : {_baseUrl}/{remotePath}");


                Console.WriteLine($"✅ Ordner erfolgreich kopiert: {sourcePath} → {targetPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in CopyFolderAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Supprime un dossier WebDAV s’il existe, avec logs détaillés.
        /// </summary>
        public async Task<bool> DeleteFolderIfExistsAsync(string relativePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    Console.WriteLine("⚠️ DeleteFolderIfExistsAsync: Pfad ist leer oder ungültig.");
                    return false;
                }

                // Pfad bereinigen
                relativePath = relativePath.Replace("\\", "/").TrimStart('/');
                var targetUrl = $"{_baseUrl.TrimEnd('/')}/{relativePath}";

                // Überprüfen, ob der Ordner vor dem Löschen existiert
                var checkRequest = new HttpRequestMessage(HttpMethod.Head, targetUrl);
                var checkResponse = await _client.SendAsync(checkRequest);

                if (checkResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"ℹ️ Ordner nicht gefunden → {relativePath}");
                    return false;
                }

                Console.WriteLine($"🧹 [DELETE] Ordner → {targetUrl}");

                var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, targetUrl);
                var authHeader = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                deleteRequest.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);

                var response = await _client.SendAsync(deleteRequest);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"✅ Ordner erfolgreich gelöscht → {relativePath}");
                    return true;
                }

                Console.WriteLine($"⚠️ Löschvorgang fehlgeschlagen → {response.StatusCode}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ DeleteFolderIfExistsAsync Fehler: {ex.Message}");
                return false;
            }
        }


        public async Task<bool> UploadFileAsync(string remotePath, Stream fileStream)
        {
            try
            {
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/{remotePath.TrimStart('/')}";
                using var request = new HttpRequestMessage(HttpMethod.Put, fullUrl)
                {
                    Content = new StreamContent(fileStream)
                };

                var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);

                var response = await _client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"✅ Upload erfolgreich → {fullUrl}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"⚠️ Upload fehlgeschlagen ({response.StatusCode}) für {fullUrl}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ UploadFileAsync Fehler: {ex.Message}");
                return false;
            }
        }




        public async Task<bool> MoveAsync(string sourcePath, string destinationPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
                {
                    Console.WriteLine("⚠️ MoveAsync: sourcePath oder destinationPath ist leer.");
                    return false;
                }

                // 🧩 Normaliser et encoder les chemins pour éviter les erreurs ASCII
                sourcePath = sourcePath.Replace("\\", "/").TrimStart('/');
                destinationPath = destinationPath.Replace("\\", "/").TrimStart('/');

                var srcUrl = $"{_baseUrl.TrimEnd('/')}/{Uri.EscapeUriString(sourcePath)}";
                var destUrl = $"{_baseUrl.TrimEnd('/')}/{Uri.EscapeUriString(destinationPath)}";

                Console.WriteLine($"📦 [MOVE] {srcUrl} → {destUrl}");

                // 🧱 Vérifie/crée le dossier cible
                var destFolder = Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
                if (!string.IsNullOrWhiteSpace(destFolder))
                {
                    Console.WriteLine($"📁 Überprüfung des Zielordners: {destFolder}");

                    await EnsureFolderTreeExistsAsync(destFolder);
                }

                // 📨 Préparer la requête MOVE
                var request = new HttpRequestMessage(new HttpMethod("MOVE"), srcUrl);
                request.Headers.Add("Destination", destUrl);
                request.Headers.Add("Overwrite", "T");

                // 🔐 Authentification
                var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                _client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);

                // 🚀 Exécution
                var response = await _client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("✅ MoveAsync: Verschieben erfolgreich.");
                    return true;
                }
                else
                {
                    Console.WriteLine($"⚠️ MoveAsync fehlgeschlagen → {response.StatusCode}");
                    var details = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"ℹ️ Antwortinhalt: {details}");

                    if (response.StatusCode == HttpStatusCode.InternalServerError &&
                        details.Contains("Could not rename resource", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine("🔁 Alternativer Versuch: Quelle kopieren und anschließend löschen...");

                        var success = await CopyThenDeleteAsync(sourcePath, destinationPath);
                        return success;
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ MoveAsync Fehler: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> CopyThenDeleteAsync(string sourcePath, string destinationPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
                {
                    Console.WriteLine("⚠️ CopyThenDeleteAsync: sourcePath oder destinationPath ist leer.");
                    return false;
                }

                var srcUrl = $"{_baseUrl.TrimEnd('/')}/{sourcePath.TrimStart('/')}";
                var destUrl = $"{_baseUrl.TrimEnd('/')}/{destinationPath.TrimStart('/')}";

                Console.WriteLine($"📄 [COPY+DELETE] {srcUrl} → {destUrl}");

                // 🔐 Authentification
                var authHeader = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                _client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);

                // 🧱 Créer le dossier de destination avant de copier
                var destFolder = Path.GetDirectoryName(destinationPath)?.Replace("\\", "/");
                if (!string.IsNullOrWhiteSpace(destFolder))
                {
                    await EnsureFolderTreeExistsAsync(destFolder);
                }

                // 🔹 Étape 1 : Copier le fichier
                var copyRequest = new HttpRequestMessage(new HttpMethod("COPY"), srcUrl);
                copyRequest.Headers.Add("Destination", destUrl);
                copyRequest.Headers.Add("Overwrite", "T");

                var copyResponse = await _client.SendAsync(copyRequest);

                if (!copyResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"⚠️ COPY fehlgeschlagen → {copyResponse.StatusCode}");
                    var details = await copyResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"ℹ️ Antwortinhalt: {details}");
                    return false;
                }

                // 🔹 Étape 2 : Supprimer la source
                var deleteResponse = await _client.DeleteAsync(srcUrl);

                if (deleteResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine("✅ COPY+DELETE erfolgreich (Fallback Move).");
                    return true;
                }
                else
                {
                    Console.WriteLine($"⚠️ DELETE fehlgeschlagen → {deleteResponse.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ CopyThenDeleteAsync Fehler: {ex.Message}");
                return false;
            }
        }

        public async Task<string> UploadWithMetadataAsync(
    Stream stream,
    string objectPath,
    string contentType,
    Metadaten? meta = null)
        {
            try
            {
                // 1️⃣ Upload principal du fichier
                await UploadStreamAsync(stream, objectPath, contentType ?? "application/octet-stream");
                Console.WriteLine($"📤 [WebDAV] Datei hochgeladen: {objectPath}");

                // 2️⃣ Métadonnées de base
                var metadata = new Dictionary<string, object>
                {
                    ["UploadedAt"] = DateTime.UtcNow.ToString("s"),
                    ["System"] = "DMS-Projekt"
                };

                // 3️⃣ Ajouter les champs des métadonnées
                if (meta != null)
                {
                    void Add(string key, object? value)
                    {
                        if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                            metadata[key] = value.ToString();
                    }

                    Add("Titel", meta.Titel);
                    Add("Kategorie", meta.Kategorie);
                    Add("Rechnungsnummer", meta.Rechnungsnummer);
                    Add("Kundennummer", meta.Kundennummer);
                    Add("Rechnungsbetrag", meta.Rechnungsbetrag);
                    Add("Nettobetrag", meta.Nettobetrag);
                    Add("Gesamtpreis", meta.Gesamtpreis);
                    Add("Steuerbetrag", meta.Steuerbetrag);
                    Add("Rechnungsdatum", meta.Rechnungsdatum);
                    Add("Faelligkeitsdatum", meta.Faelligkeitsdatum);
                    Add("Zahlungsbedingungen", meta.Zahlungsbedingungen);
                    Add("Lieferart", meta.Lieferart);
                    Add("ArtikelAnzahl", meta.ArtikelAnzahl);
                    Add("Email", meta.Email);
                    Add("Telefon", meta.Telefon);
                    Add("IBAN", meta.IBAN);
                    Add("BIC", meta.BIC);
                    Add("Bankverbindung", meta.Bankverbindung);
                    Add("SteuerNr", meta.SteuerNr);
                    Add("UIDNummer", meta.UIDNummer);
                    Add("Adresse", meta.Adresse);
                }

                // 4️⃣ Convertir les métadonnées en JSON
                var metaJson = System.Text.Json.JsonSerializer.Serialize(
                    metadata,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }
                );

                // 5️⃣ Sauvegarder le JSON à côté du fichier
                string metaPath = objectPath + ".meta.json";
                using var metaStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(metaJson));
                await UploadStreamAsync(metaStream, metaPath, "application/json");

                Console.WriteLine($"🧾 [WebDAV] Metadaten gespeichert: {metaPath}");

                // 6️⃣ Retourner l’URL d’accès WebDAV
                string fileUrl = $"{_baseUrl.TrimEnd('/')}/{objectPath.TrimStart('/')}";
                return fileUrl;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler bei UploadWithMetadataAsync (WebDAV): {ex.Message}");
                throw;
            }
        }
        public async Task MoveFolderAsync(string sourcePath, string targetPath)
        {
            try
            {
                sourcePath = sourcePath.TrimEnd('/');
                targetPath = targetPath.TrimEnd('/');

                Console.WriteLine($"📁 [MoveFolder] Verschiebe Ordner: {sourcePath} → {targetPath}");

                // 🔹 Récupérer tous les fichiers du dossier source
                var files = await ListFilesAsync(sourcePath);

                if (files.Count == 0)
                {
                    Console.WriteLine($"⚠️ Kein Inhalt im Ordner {sourcePath} gefunden.");
                    return;
                }

                // 🔁 Copier chaque fichier dans le nouveau dossier
                foreach (var file in files)
                {
                    string fileName = Path.GetFileName(file);
                    string sourceFilePath = $"{sourcePath}/{fileName}";
                    string destFilePath = $"{targetPath}/{fileName}";

                    Console.WriteLine($"📦 Verschiebe Datei: {sourceFilePath} → {destFilePath}");

                    using var fileStream = await DownloadStreamAsync(sourceFilePath);
                    await UploadStreamAsync(fileStream, destFilePath, "application/octet-stream");

                    // ✅ Supprimer après copie
                    bool deleted = await DeleteFileAsync(sourceFilePath);
                    if (!deleted)
                        Console.WriteLine($"⚠️ Datei konnte nicht gelöscht werden: {sourceFilePath}");
                }

                // ✅ Supprimer le dossier source (si vide)
                await DeleteFolderAsync(sourcePath);

                Console.WriteLine($"✅ Ordner erfolgreich verschoben: {sourcePath} → {targetPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Verschieben des Ordners {sourcePath}: {ex.Message}");
                throw;
            }
        }
        public async Task<bool> DeleteFolderAsync(string folderPath)
        {
            try
            {
                folderPath = folderPath.TrimEnd('/');
                Console.WriteLine($"🗑️ [WebDAV] Lösche Ordner: {folderPath}");

                // 🔹 1️⃣ Récupérer tous les fichiers dans le dossier
                var files = await ListFilesAsync(folderPath);
                foreach (var file in files)
                {
                    try
                    {
                        string fullFilePath = file.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase)
     ? file
     : $"{folderPath}/{file}".Replace("//", "/");

                        bool deleted = await DeleteFileAsync(fullFilePath);
                        Console.WriteLine(deleted
                            ? $"✅ Datei gelöscht: {fullFilePath}"
                            : $"⚠️ Datei konnte nicht gelöscht werden: {fullFilePath}");
                    }
                    catch (Exception exFile)
                    {
                        Console.WriteLine($"⚠️ Fehler beim Löschen der Datei im Ordner {file}: {exFile.Message}");
                    }
                }

                // 🔹 2️⃣ Supprimer les sous-dossiers
                var subFolders = await ListFoldersAsync(folderPath);
                foreach (var sub in subFolders)
                {
                    string subPath = $"{folderPath}/{sub}".Replace("//", "/");
                    await DeleteFolderAsync(subPath);
                }

                // 🔹 3️⃣ Enfin, supprimer le dossier lui-même
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/{folderPath}".Replace("//", "/").Replace(":/", "://");

                using var handler = new HttpClientHandler { Credentials = _credentials };
                using var client = new HttpClient(handler);

                var response = await client.DeleteAsync(fullUrl);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"🗑️ Ordner gelöscht: {folderPath}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"⚠️ Konnte Ordner nicht löschen ({response.StatusCode}): {folderPath}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Löschen des Ordners {folderPath}: {ex.Message}");
                return false;
            }
        }
        public async Task DownloadToStreamAsync(string remotePath, Stream destination)
        {
            try
            {
                // ✅ Construire l’URL complète du fichier sur le serveur WebDAV
                var fullUrl = $"{_baseUrl.TrimEnd('/')}/{remotePath.TrimStart('/')}"
                    .Replace("//", "/")
                    .Replace(":/", "://");

                using var handler = new HttpClientHandler { Credentials = _credentials };
                using var client = new HttpClient(handler);

                // 📥 Télécharger le contenu distant
                var response = await client.GetAsync(fullUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await response.Content.CopyToAsync(destination);
                destination.Position = 0;

                Console.WriteLine($"📥 Datei von WebDAV heruntergeladen: {remotePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in DownloadToStreamAsync: {ex.Message}");
                throw;
            }
        }
        // ============================================================
        // ✅ Simplified Upload for User (équivalent Firebase UploadForUserAsync)
        // ============================================================
        public async Task<string> UploadForUserAsync(
            IFormFile file,
            string firma,
            string abteilung,
            string kategorie)
        {
            try
            {
                // 1️⃣ Nettoyage des paramètres
                firma = string.IsNullOrWhiteSpace(firma) ? "unbekannt" : firma.Trim().ToLowerInvariant();
                abteilung = string.IsNullOrWhiteSpace(abteilung) ? "allgemein" : abteilung.Trim().ToLowerInvariant();
                kategorie = string.IsNullOrWhiteSpace(kategorie) ? "ohne_kategorie" : kategorie.Trim().ToLowerInvariant();

                // 2️⃣ Construire le chemin WebDAV distant
                string remotePath = $"dokumente/{firma}/{abteilung}/{kategorie}/{file.FileName}";
                string fullUrl = $"{_baseUrl.TrimEnd('/')}/{remotePath}"
                    .Replace("//", "/")
                    .Replace(":/", "://");

                // 3️⃣ Upload du fichier
                using (var stream = file.OpenReadStream())
                {
                    await UploadStreamAsync(stream, remotePath, file.ContentType);
                }

                Console.WriteLine($"✅ [WebDAV] Datei hochgeladen: {fullUrl}");
                return fullUrl; // Retourne l’URL complète (à enregistrer dans Dateipfad)
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in UploadForUserAsync: {ex.Message}");
                throw;
            }
        }



        public async Task<Stream?> DownloadStreamAsync(string remotePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath))
                    throw new ArgumentException("remotePath darf nicht leer sein.", nameof(remotePath));

                remotePath = remotePath.Trim().Replace("\\", "/");

                string fullUrl = remotePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? remotePath
                    : $"{_baseUrl.TrimEnd('/')}/{remotePath.TrimStart('/')}";

                fullUrl = Uri.EscapeUriString(fullUrl);

                Console.WriteLine($"🌐 [WebDAV] GET {fullUrl}");

                // 🔐 WebDAV Basic Auth (funktioniert mit deinem Server)
                var handler = new HttpClientHandler
                {
                    PreAuthenticate = true,
                    Credentials = _credentials
                };

                using var client = new HttpClient(handler);

                // Setze explizit Basic Auth Header
                var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

                // Optional: User-Agent, weil manche WebDAV-Server sonst blockieren
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DMS-Project-WebDavClient/1.0");

                var response = await client.GetAsync(fullUrl);

                if (!response.IsSuccessStatusCode)
                {
                    var msg = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ WebDAV GET fehlgeschlagen ({response.StatusCode}) : {msg}");
                    return null;
                }

                var memory = new MemoryStream();
                await response.Content.CopyToAsync(memory);
                memory.Position = 0;

                Console.WriteLine($"📥 [WebDAV] Download abgeschlossen: {remotePath} ({memory.Length} Bytes)");
                return memory;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in DownloadStreamAsync: {ex.Message}");
                return null;
            }
        }
        public async Task<Stream?> DownloadStreamStableAsync(string remotePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath))
                    throw new ArgumentException("remotePath darf nicht leer sein.", nameof(remotePath));

                remotePath = remotePath.Trim().Replace("\\", "/");

                string fullUrl = remotePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? remotePath
                    : $"{_baseUrl.TrimEnd('/')}/{remotePath.TrimStart('/')}";

                Console.WriteLine($"🌐 [WebDAV-Stable] GET {fullUrl}");

                var response = await _client.GetAsync(fullUrl);

                if (!response.IsSuccessStatusCode)
                {
                    var msg = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ WebDAV-Stable GET fehlgeschlagen ({response.StatusCode}) : {msg}");
                    return null;
                }

                var memory = new MemoryStream();
                await response.Content.CopyToAsync(memory);
                memory.Position = 0;

                Console.WriteLine($"📥 [WebDAV-Stable] Download abgeschlossen: {remotePath} ({memory.Length} Bytes)");
                return memory;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in DownloadStreamStableAsync: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Prüft, ob eine Datei auf dem WebDAV-Server existiert.
        /// </summary>
        public async Task<bool> FileExistsAsync(string remotePath)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
                return false;

            try
            {
                // ✅ Normalisation du chemin
                remotePath = remotePath.Replace("\\", "/").Trim();

                string baseUrl = _baseUrl.TrimEnd('/');
                string fullUrl;

                // ✅ Si le chemin est déjà complet, on le garde
                if (remotePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    remotePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    fullUrl = remotePath;
                }
                else
                {
                    // Supprimer un éventuel "/DmsDaten/" redondant
                    int idx = remotePath.IndexOf("/DmsDaten/", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                        remotePath = remotePath.Substring(idx + "/DmsDaten/".Length);

                    fullUrl = $"{baseUrl}/{remotePath}".Replace("//", "/").Replace(":/", "://");
                }

                // ✅ Encodage complet (espaces, accents, etc.)
                fullUrl = Uri.EscapeUriString(fullUrl);

                Console.WriteLine($"🔍 [WebDav.FileExistsAsync] Checking: {fullUrl}");

                // ✅ HEAD request (rapide)
                using var request = new HttpRequestMessage(HttpMethod.Head, fullUrl);
                using var response = await _client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"✅ [WebDav.FileExistsAsync] File exists: {remotePath}");
                    return true;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    Console.WriteLine($"ℹ️ [WebDav.FileExistsAsync] File not found: {remotePath}");
                    return false;
                }

                Console.WriteLine($"⚠️ [WebDav.FileExistsAsync] Status={response.StatusCode} ({response.ReasonPhrase}) for {remotePath}");
                return false;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"❌ [WebDav.FileExistsAsync] HTTP error for {remotePath}: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [WebDav.FileExistsAsync] General error for {remotePath}: {ex.Message}");
                return false;
            }
        }

        public string NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("⚠️ [WebDav.NormalizePath] Path is null or empty");
                return string.Empty;
            }

            path = path.Replace("\\", "/").Trim();
            string baseUrl = BaseUrl.TrimEnd('/');

            // ✅ 1. Nettoyer les pollutions connues
            // Supprime les répétitions de "DmsDaten/" ou "https://..."
            while (path.Contains("/DmsDaten/DmsDaten/", StringComparison.OrdinalIgnoreCase))
                path = path.Replace("/DmsDaten/DmsDaten/", "/DmsDaten/", StringComparison.OrdinalIgnoreCase);

            // Supprimer tout avant "/DmsDaten/"
            int idx = path.IndexOf("/DmsDaten/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                path = path.Substring(idx + "/DmsDaten/".Length);

            // ✅ 2. Si le chemin commence déjà par la base URL, on le garde tel quel
            if (path.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"✅ [WebDav.NormalizePath] Already normalized: {path}");
                return path;
            }

            // ✅ 3. Si c’est une URL complète (https://...), ne rien reconstruire
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"✅ [WebDav.NormalizePath] Full URL detected, returning as-is: {path}");
                return path;
            }

            // ✅ 4. Nettoyer les barres et reconstruire correctement
            path = path.TrimStart('/');
            string fullPath = $"{baseUrl}/{path}";

            // ✅ 5. Nettoyer les doublons de "https://" ou de baseUrl
            while (fullPath.Contains($"{baseUrl}/{baseUrl}", StringComparison.OrdinalIgnoreCase))
                fullPath = fullPath.Replace($"{baseUrl}/{baseUrl}", baseUrl, StringComparison.OrdinalIgnoreCase);

            fullPath = fullPath.Replace("//", "/").Replace(":/", "://");

            Console.WriteLine($"✅ [WebDav.NormalizePath] Normalized Path = {fullPath}");
            return fullPath;
        }

        /// <summary>
        /// Vérifie si un dossier WebDAV est vide (aucun fichier ni sous-dossier).
        /// Retourne true si le dossier est vide, false sinon.
        /// </summary>
        public async Task<bool> IsFolderEmptyAsync(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    Console.WriteLine("⚠️ [IsFolderEmptyAsync] Pfad ist leer oder ungültig.");
                    return true; // Considérer vide si le chemin est invalide
                }

                folderPath = folderPath.Trim('/');

                var requestUrl = $"{_baseUrl.TrimEnd('/')}/{folderPath}";
                Console.WriteLine($"🔍 [WebDAV] Prüfe, ob der Ordner leer ist: {requestUrl}");

                // 🔹 PROPFIND pour lister les fichiers et dossiers à 1 niveau
                var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), requestUrl);
                request.Headers.Add("Depth", "1");

                string propfindXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
        <propfind xmlns=""DAV:"">
            <prop>
                <displayname />
                <resourcetype />
            </prop>
        </propfind>";

                request.Content = new StringContent(propfindXml, Encoding.UTF8, "text/xml");

                // 🔐 Authentification
                var byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                _client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

                var response = await _client.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"⚠️ [IsFolderEmptyAsync] PROPFIND fehlgeschlagen: {response.StatusCode}");
                    return true; // Si erreur → considérer vide pour éviter blocage
                }

                var xml = await response.Content.ReadAsStringAsync();
                var xdoc = XDocument.Parse(xml);
                XNamespace ns = "DAV:";

                // 🧩 Compter les entrées (ignore le dossier lui-même)
                var responses = xdoc.Descendants(ns + "response").ToList();

                // 1ère réponse = le dossier lui-même → ignorer
                if (responses.Count <= 1)
                {
                    Console.WriteLine("✅ [IsFolderEmptyAsync] Ordner ist leer.");
                    return true;
                }

                // Vérifier s'il y a des fichiers ou sous-dossiers (autres que le parent)
                foreach (var responseNode in responses.Skip(1))
                {
                    var name = responseNode.Descendants(ns + "displayname").FirstOrDefault()?.Value ?? "";
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        Console.WriteLine($"📄 [IsFolderEmptyAsync] Gefunden: {name}");
                        return false; // Dossier non vide
                    }
                }

                Console.WriteLine("✅ [IsFolderEmptyAsync] Ordner ist leer (keine Dateien oder Unterordner).");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ [IsFolderEmptyAsync] Fehler: {ex.Message}");
                return true; // En cas d'erreur, éviter de bloquer → considérer vide
            }
        }





        // ============================================================
        // ✅ STREAMING DOWNLOAD WITH RANGE SUPPORT
        // ============================================================
        // ============================================================
        // ✅ STREAMING DOWNLOAD WITH RANGE SUPPORT
        // ============================================================
        public async Task<WebDavStreamResult?> DownloadStreamWithRangeAsync(string remotePath, string? rangeHeaderString = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(remotePath))
                    throw new ArgumentException("remotePath darf nicht leer sein.", nameof(remotePath));

                remotePath = remotePath.Trim().Replace("\\", "/");

                string fullUrl;
                if (remotePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    fullUrl = remotePath;
                }
                else
                {
                    fullUrl = $"{_baseUrl.TrimEnd('/')}/{remotePath.TrimStart('/')}";
                }

                // URL Encoding fix: Only encode spaces if not already encoded
                // This is a simple fix. For robust handling, we should parse and re-assemble.
                // But given the context, ensuring spaces are %20 is the most critical part.
                if (!fullUrl.Contains("%20") && fullUrl.Contains(" "))
                {
                    fullUrl = fullUrl.Replace(" ", "%20");
                }
                // Note: Uri.EscapeUriString is obsolete. We rely on manual space replacement or pre-encoded paths.

                Console.WriteLine($"🌐 [WebDAV-Stream] GET {fullUrl} (Range: {rangeHeaderString})");

                var handler = new HttpClientHandler
                {
                    PreAuthenticate = true,
                    Credentials = _credentials
                };

                var client = new HttpClient(handler);
                client.Timeout = Timeout.InfiniteTimeSpan; // ⏳ Disable timeout for large file streaming
                
                var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DMS-Project-WebDavClient/1.0");

                var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
                if (!string.IsNullOrEmpty(rangeHeaderString))
                {
                    request.Headers.TryAddWithoutValidation("Range", rangeHeaderString);
                }

                // Use ResponseHeadersRead to get the stream immediately
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                Console.WriteLine($"✅ [WebDAV-Stream] Response received: {response.StatusCode} | Length: {response.Content.Headers.ContentLength}");

                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
                {
                    var msg = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"❌ WebDAV-Stream GET fehlgeschlagen ({response.StatusCode}) : {msg}");
                    response.Dispose();
                    client.Dispose();
                    return null;
                }

                var stream = await response.Content.ReadAsStreamAsync();
                Console.WriteLine("✅ [WebDAV-Stream] Stream opened. Wrapping in AutoDisposeStream...");
                
                var wrappedStream = new AutoDisposeStream(stream, response, client);

                return new WebDavStreamResult
                {
                    Stream = wrappedStream,
                    StatusCode = response.StatusCode,
                    ContentLength = response.Content.Headers.ContentLength,
                    ContentRange = response.Content.Headers.ContentRange,
                    ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler in DownloadStreamWithRangeAsync: {ex.Message}");
                return null;
            }
        }

        private class AutoDisposeStream : Stream
        {
            private readonly Stream _inner;
            private readonly IDisposable[] _disposables;

            public AutoDisposeStream(Stream inner, params IDisposable[] disposables)
            {
                _inner = inner;
                _disposables = disposables;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    foreach (var d in _disposables) d?.Dispose();
                }
                base.Dispose(disposing);
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
            public override void SetLength(long value) => _inner.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.WriteAsync(buffer, offset, count, cancellationToken);
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner.WriteAsync(buffer, cancellationToken);
        }
    }

    public class WebDavStreamResult
    {
        public Stream Stream { get; set; }
        public System.Net.HttpStatusCode StatusCode { get; set; }
        public System.Net.Http.Headers.ContentRangeHeaderValue? ContentRange { get; set; }
        public long? ContentLength { get; set; }
        public string ContentType { get; set; }
    }
}
