using DmsProjeckt.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;                    // ✅ nécessaire pour Encoding
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace DmsProjeckt.Service
{
    public class ChunkService
    {
        private readonly ApplicationDbContext _db;
        private readonly WebDavStorageService _webDav;
        private const int CHUNK_SIZE = 20 * 1024 * 1024; // 🔹 20 MB pro Block
        private static readonly SemaphoreSlim _reconstructLock = new(1, 1);
        private readonly ILogger<ChunkService> _logger;
        private readonly IMemoryCache _cache;
        public ChunkService(ApplicationDbContext db, WebDavStorageService webDav, ILogger<ChunkService> logger, IMemoryCache cache)
        {
            _db = db;
            _webDav = webDav;
            _logger = logger;
            _cache = cache;
        }

        // ============================================================
        // 1️⃣ Datei in Chunks aufteilen und auf WebDAV hochladen
        // ============================================================
        public async Task<List<DokumentChunk>> SaveFileAsChunksToWebDavAsync(
       Stream fileStream,
       Guid dokumentId,
       string firma,
       string abteilung,
       string kategorie)
        {
            var chunks = new List<DokumentChunk>();
            int index = 0;

            // 📁 Dossier cible
            var chunkFolder = $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{dokumentId}";

            // ✅ On utilise maintenant la méthode unifiée (remplace CreateFolderRecursiveAsync)
            await _webDav.EnsureFolderTreeExistsAsync(chunkFolder);

            var buffer = new byte[20 * 1024 * 1024]; // 20 MB par chunk
            int bytesRead;

            _logger.LogInformation($"🧩 [ChunkUpload] Start für {chunkFolder}");

            while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                var chunkFile = $"{chunkFolder}/chunk_{index}.bin";
                _logger.LogInformation($"➡️ Lade Chunk {index} ({bytesRead / 1024 / 1024.0:F2} MB) → {chunkFile}");

                using var chunkStream = new MemoryStream(buffer, 0, bytesRead);

                // ✅ Upload binaire réel vers WebDAV
                var success = await _webDav.UploadFileAsync(chunkFile, chunkStream);
                if (!success)
                {
                    _logger.LogInformation($"❌ Chunk {index} konnte nicht hochgeladen werden: {chunkFile}");
                    break;
                }

                // ✅ Sauvegarde DB
                var chunkEntity = new DokumentChunk
                {
                    Id = Guid.NewGuid(),
                    CreatedAt = DateTime.UtcNow,
                    DokumentId = dokumentId,
                    FirebasePath = chunkFile,
                    Hash = ComputeSha256(buffer.Take(bytesRead).ToArray()),
                    Index = index,
                    Size = bytesRead
                };

                _db.DokumentChunks.Add(chunkEntity);
                chunks.Add(chunkEntity);
                index++;
            }

            await _db.SaveChangesAsync();
            var manifest = new
            {
                CreatedAt = DateTime.UtcNow,
                Chunks = chunks.Select(c => new { c.Index, c.Hash, c.Size, c.FirebasePath }).ToList()
            };

            string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            string manifestPath = $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{dokumentId}_manifest.json";

            using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
            await _webDav.UploadStreamAsync(manifestStream, manifestPath, "application/json");
            _logger.LogInformation($"📄 Initiales Manifest gespeichert: {manifestPath}");

            _logger.LogInformation($"✅ [ChunkUpload] {chunks.Count} Chunks enregistrés pour {dokumentId}");
            return chunks;
        }
        public async Task<string?> ReconstructFileFromWebDavAsync(Guid dokumentId)
        {
            await _reconstructLock.WaitAsync();
            try
            {
                _logger.LogInformation("📄 [Reconstruct] Starte Rekonstruktion für Dokument-ID: {DokumentId}", dokumentId);

                // =====================================================
                // 🧠 1️⃣ Prüfen, ob Datei bereits im Cache vorhanden
                // =====================================================
                if (_cache.TryGetValue(dokumentId, out string cachedPath) && System.IO.File.Exists(cachedPath))
                {
                    _logger.LogInformation("⚡ [Reconstruct] Cache-Treffer → Verwende zwischengespeicherte Datei: {Pfad}", cachedPath);
                    return cachedPath;
                }

                // =====================================================
                // 🔍 2️⃣ Dokument oder Version suchen
                // =====================================================
                var dokument = await _db.Dokumente
                    .Include(d => d.Abteilung)
                    .Include(d => d.ApplicationUser)
                    .FirstOrDefaultAsync(d => d.Id == dokumentId);

                bool isVersion = false;
                Guid effectiveId = dokumentId;

                if (dokument == null)
                {
                    var version = await _db.DokumentVersionen
                        .Include(v => v.Abteilung)
                        .Include(v => v.ApplicationUser)
                        .FirstOrDefaultAsync(v => v.Id == dokumentId || v.OriginalId == dokumentId);

                    if (version == null)
                    {
                        _logger.LogWarning("❌ [Reconstruct] Weder Dokument noch Version gefunden: {DokumentId}", dokumentId);
                        return null;
                    }

                    isVersion = true;
                    effectiveId = version.OriginalId ?? version.Id;

                    _logger.LogInformation("🧬 [Reconstruct] Version erkannt: {VersionId} (OriginalId={OriginalId})", version.Id, version.OriginalId);

                    // Original laden, wenn vorhanden
                    var original = await _db.Dokumente
                        .Include(d => d.Abteilung)
                        .Include(d => d.ApplicationUser)
                        .FirstOrDefaultAsync(d => d.Id == effectiveId);

                    dokument = original ?? new Dokumente
                    {
                        Id = version.Id,
                        OriginalId = version.OriginalId,
                        Dateiname = version.Dateiname,
                        Kategorie = version.Kategorie ?? "misc",
                        ObjectPath = version.ObjectPath ?? version.Dateipfad,
                        Dateipfad = version.Dateipfad,
                        ApplicationUser = version.ApplicationUser,
                        ApplicationUserId = version.ApplicationUserId,
                        Abteilung = version.Abteilung,
                        AbteilungId = version.AbteilungId,
                        HochgeladenAm = version.HochgeladenAm,
                        IsChunked = version.IsChunked,
                        EstSigne = version.EstSigne,
                        IsVersion = true
                    };
                }

                string firma = dokument.ApplicationUser?.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";
                string abteilung = dokument.Abteilung?.Name?.Trim().ToLowerInvariant() ?? "allgemein";
                string kategorie = dokument.Kategorie?.Trim().ToLowerInvariant() ?? "misc";

                _logger.LogInformation("🏢 [Reconstruct] Firma={Firma}, Abteilung={Abteilung}, Kategorie={Kategorie}, Version={Version}, EffektiveID={EffektiveId}",
                    firma, abteilung, kategorie, isVersion, effectiveId);

                // =====================================================
                // 📦 3️⃣ Chunks laden (aus DB oder Manifest)
                // =====================================================
                List<DokumentChunk> chunks = new();

                if (isVersion)
                {
                    chunks = await _db.DokumentVersionChunks
                        .Where(v => v.VersionId == dokument.Id)
                        .Include(v => v.Chunk)
                        .Select(v => v.Chunk)
                        .OrderBy(c => c.Index)
                        .ToListAsync();

                    if (chunks.Any())
                        _logger.LogInformation("✅ [Reconstruct] {Count} Chunks aus DokumentVersionChunks geladen.", chunks.Count);
                }

                if (!chunks.Any())
                {
                    chunks = await _db.DokumentChunks
                        .Where(c => c.DokumentId == effectiveId)
                        .OrderBy(c => c.Index)
                        .ToListAsync();

                    if (chunks.Any())
                        _logger.LogInformation("✅ [Reconstruct] {Count} Chunks aus DokumentChunks geladen.", chunks.Count);
                }

                if (!chunks.Any())
                {
                    _logger.LogWarning("⚠️ [Reconstruct] Keine Chunks in DB gefunden – versuche Manifest von WebDAV...");

                    string manifestPath = isVersion
                        ? $"dokumente/{firma}/{abteilung}/versionen/chunks/{effectiveId}_manifest.json"
                        : $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{effectiveId}_manifest.json";

                    string manifestUrl = $"{_webDav.BaseUrl.TrimEnd('/')}/{manifestPath}";
                    _logger.LogInformation("🔍 [Reconstruct] Lade Manifest: {ManifestUrl}", manifestUrl);

                    var manifestStream = await _webDav.DownloadStreamStableAsync(manifestUrl);
                    if (manifestStream == null)
                    {
                        _logger.LogError("❌ [Reconstruct] Manifest nicht gefunden unter {Url}", manifestUrl);
                        return null;
                    }

                    using var reader = new StreamReader(manifestStream);
                    var json = await reader.ReadToEndAsync();
                    var manifest = JsonSerializer.Deserialize<ChunkManifest>(json);

                    if (manifest?.Chunks == null || manifest.Chunks.Count == 0)
                    {
                        _logger.LogError("❌ [Reconstruct] Manifest ungültig oder leer: {Url}", manifestUrl);
                        return null;
                    }

                    chunks = manifest.Chunks.Select(c => new DokumentChunk
                    {
                        DokumentId = effectiveId,
                        Index = c.Index,
                        FirebasePath = isVersion
                            ? $"dokumente/{firma}/{abteilung}/versionen/chunks/{c.File}"
                            : $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{c.File}"
                    }).ToList();

                    _logger.LogInformation("✅ [Reconstruct] {Count} Chunks aus Manifest geladen.", chunks.Count);
                }

                if (!chunks.Any())
                {
                    _logger.LogError("❌ [Reconstruct] Keine Chunks gefunden – Abbruch!");
                    return null;
                }

                // =====================================================
                // 🧩 4️⃣ Temporäre Datei erstellen
                // =====================================================
                var tempDir = Path.Combine(Path.GetTempPath(), "DMS_Reconstructed");
                Directory.CreateDirectory(tempDir);
                var tempFile = Path.Combine(tempDir, dokument.Dateiname ?? $"{effectiveId}.pdf");

                _logger.LogInformation("📁 [Reconstruct] Temporäre Datei: {Pfad}", tempFile);

                using (var output = new FileStream(tempFile, FileMode.Create, FileAccess.Write))
                {
                    foreach (var chunk in chunks.OrderBy(c => c.Index))
                    {
                        string fullUrl = chunk.FirebasePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            ? chunk.FirebasePath
                            : $"{_webDav.BaseUrl.TrimEnd('/')}/{chunk.FirebasePath.TrimStart('/')}";

                        _logger.LogInformation("⬇️ [Reconstruct] Lade Chunk {Index}: {Url}", chunk.Index, fullUrl);

                        using var chunkStream = await _webDav.DownloadStreamStableAsync(fullUrl);
                        if (chunkStream == null)
                        {
                            _logger.LogWarning("⚠️ [Reconstruct] Chunk {Index} konnte nicht geladen werden!", chunk.Index);
                            continue;
                        }

                        await chunkStream.CopyToAsync(output);
                        await output.FlushAsync();
                    }
                }

                // =====================================================
                // ✅ 5️⃣ Datei prüfen und hochladen
                // =====================================================
                if (!System.IO.File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                {
                    _logger.LogError("❌ [Reconstruct] Rekonstruierte Datei ist leer!");
                    return null;
                }

                _logger.LogInformation("✅ [Reconstruct] Datei erfolgreich rekonstruiert ({Pfad})", tempFile);

                string targetFolder = isVersion
                    ? $"dokumente/{firma}/{abteilung}/versionen/reconstructed"
                    : $"dokumente/{firma}/{abteilung}/{kategorie}/reconstructed";

                await _webDav.EnsureFolderTreeExistsAsync(targetFolder);
                string targetPath = $"{targetFolder}/{dokument.Dateiname}";

                _logger.LogInformation("📤 [Reconstruct] Upload zu WebDAV → {Pfad}", targetPath);

                await using (var uploadStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read))
                {
                    bool uploaded = await _webDav.UploadStreamAsync(uploadStream, targetPath, "application/pdf");
                    if (!uploaded)
                    {
                        _logger.LogError("❌ [Reconstruct] Fehler beim Upload zu WebDAV: {Pfad}", targetPath);
                        return tempFile;
                    }
                }

                _logger.LogInformation("✅ [Reconstruct] Upload erfolgreich: {Pfad}", targetPath);

                // =====================================================
                // 🗃️ 6️⃣ Datenbank aktualisieren
                // =====================================================
                if (isVersion)
                {
                    var versionEntity = await _db.DokumentVersionen.FirstOrDefaultAsync(v => v.Id == dokument.Id);
                    if (versionEntity != null)
                    {
                        versionEntity.ObjectPath = targetPath;
                        versionEntity.Dateipfad = $"{_webDav.BaseUrl.TrimEnd('/')}/{targetPath}";
                        versionEntity.IsChunked = false;
                        await _db.SaveChangesAsync();
                    }
                }
                else
                {
                    dokument.ObjectPath = targetPath;
                    dokument.Dateipfad = $"{_webDav.BaseUrl.TrimEnd('/')}/{targetPath}";
                    dokument.IsChunked = false;
                    dokument.dtStatus = DokumentStatus.Fertig;
                    await _db.SaveChangesAsync();
                }

                // =====================================================
                // 💾 7️⃣ In Cache speichern
                // =====================================================
                _cache.Set(dokumentId, dokument.Dateipfad, TimeSpan.FromMinutes(10));
                _logger.LogInformation("💾 [Reconstruct] Datei im Cache gespeichert für 10 Minuten → {Pfad}", dokument.Dateipfad);

                return dokument.Dateipfad;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ [Reconstruct] Fehler bei der Rekonstruktion von Dokument {DokumentId}", dokumentId);
                return null;
            }
            finally
            {
                _reconstructLock.Release();
            }
        }

        public async Task<Stream?> GetStreamForChunkedFileAsync(Guid dokumentId)
        {
            _logger.LogInformation("🌊 [ChunkStream] Starte Stream-Initialisierung für Dokument-ID: {DokumentId}", dokumentId);

            // =====================================================
            // 🔍 1️⃣ Dokument oder Version suchen (Kopie der Logik aus Reconstruct)
            // =====================================================
            var dokument = await _db.Dokumente
                .Include(d => d.Abteilung)
                .Include(d => d.ApplicationUser)
                .FirstOrDefaultAsync(d => d.Id == dokumentId);

            bool isVersion = false;
            Guid effectiveId = dokumentId;

            if (dokument == null)
            {
                var version = await _db.DokumentVersionen
                    .Include(v => v.Abteilung)
                    .Include(v => v.ApplicationUser)
                    .FirstOrDefaultAsync(v => v.Id == dokumentId || v.OriginalId == dokumentId);

                if (version == null)
                {
                    _logger.LogWarning("❌ [ChunkStream] Weder Dokument noch Version gefunden: {DokumentId}", dokumentId);
                    return null;
                }

                isVersion = true;
                effectiveId = version.OriginalId ?? version.Id;

                // Original laden für Metadaten
                var original = await _db.Dokumente
                    .Include(d => d.Abteilung)
                    .Include(d => d.ApplicationUser)
                    .FirstOrDefaultAsync(d => d.Id == effectiveId);

                dokument = original ?? new Dokumente
                {
                    Id = version.Id,
                    OriginalId = version.OriginalId,
                    Dateiname = version.Dateiname,
                    Kategorie = version.Kategorie ?? "misc",
                    ApplicationUser = version.ApplicationUser,
                    Abteilung = version.Abteilung
                };
            }

            string firma = dokument.ApplicationUser?.FirmenName?.Trim().ToLowerInvariant() ?? "unbekannt";
            string abteilung = dokument.Abteilung?.Name?.Trim().ToLowerInvariant() ?? "allgemein";
            string kategorie = dokument.Kategorie?.Trim().ToLowerInvariant() ?? "misc";

            // =====================================================
            // 📦 2️⃣ Chunks laden (aus DB oder Manifest)
            // =====================================================
            List<DokumentChunk> chunks = new();

            if (isVersion)
            {
                chunks = await _db.DokumentVersionChunks
                    .Where(v => v.VersionId == dokument.Id)
                    .Include(v => v.Chunk)
                    .Select(v => v.Chunk)
                    .OrderBy(c => c.Index)
                    .ToListAsync();
            }

            if (!chunks.Any())
            {
                chunks = await _db.DokumentChunks
                    .Where(c => c.DokumentId == effectiveId)
                    .OrderBy(c => c.Index)
                    .ToListAsync();
            }

            if (!chunks.Any())
            {
                _logger.LogWarning("⚠️ [ChunkStream] Keine Chunks in DB gefunden – versuche Manifest von WebDAV...");

                string manifestPath = isVersion
                    ? $"dokumente/{firma}/{abteilung}/versionen/chunks/{effectiveId}_manifest.json"
                    : $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{effectiveId}_manifest.json";

                string manifestUrl = $"{_webDav.BaseUrl.TrimEnd('/')}/{manifestPath}";
                var manifestStream = await _webDav.DownloadStreamStableAsync(manifestUrl);

                if (manifestStream != null)
                {
                    using var reader = new StreamReader(manifestStream);
                    var json = await reader.ReadToEndAsync();
                    var manifest = JsonSerializer.Deserialize<ChunkManifest>(json);

                    if (manifest?.Chunks != null)
                    {
                        chunks = manifest.Chunks.Select(c => new DokumentChunk
                        {
                            DokumentId = effectiveId,
                            Index = c.Index,
                            Size = 20 * 1024 * 1024, // Fallback size if not in manifest, ideally manifest has it
                            FirebasePath = isVersion
                                ? $"dokumente/{firma}/{abteilung}/versionen/chunks/{c.File}"
                                : $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{c.File}"
                        }).ToList();
                    }
                }
            }

            if (!chunks.Any())
            {
                _logger.LogError("❌ [ChunkStream] Keine Chunks gefunden – Abbruch!");
                return null;
            }

            _logger.LogInformation("✅ [ChunkStream] {Count} Chunks geladen. Erstelle ChunkedWebDavStream...", chunks.Count);
            
            // Return the custom stream
            return new ChunkedWebDavStream(_webDav, chunks);
        }
        public async Task<List<DokumentChunk>> CompareAndUploadNewVersionChunksAsync(
           Guid oldDokumentId,
           Stream newFileStream,
           Guid newDokumentId,
           string firma,
           string abteilung,
           string kategorie)
        {
            const int CHUNK_SIZE = 5 * 1024 * 1024; // 5 MB
            const double TOLERANCE = 0.05; // 5% tolerance for deep compare

            var chunks = new List<DokumentChunk>();

            _logger.LogInformation("");
            _logger.LogInformation("═══════════════════════════════════════════════════════════════");
            _logger.LogInformation("⚙️  [ChunkVergleich] Starte differenziellen Chunk-Vergleich");
            _logger.LogInformation($"🏢 Firma: {firma} | Abteilung: {abteilung} | Kategorie: {kategorie}");
            _logger.LogInformation($"📦 Chunkgröße: {CHUNK_SIZE / 1024 / 1024} MB");
            _logger.LogInformation("═══════════════════════════════════════════════════════════════");
            _logger.LogInformation("");

            // ============================================================
            // 🧩 1️⃣ Altes Manifest intelligent suchen
            // ============================================================
            var oldManifestDict = new Dictionary<int, string>();
            bool hasOldManifest = false;
            string? oldManifestPath = null;

            string[] possibleManifests =
            {
        $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/{oldDokumentId}_manifest.json",
        $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{oldDokumentId}_manifest.json",
        $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{oldDokumentId}/{oldDokumentId}_manifest.json"
    };

            Stream? oldStream = null;

            foreach (var path in possibleManifests)
            {
                try
                {
                    _logger.LogInformation($"🔍 [ManifestCheck] Versuche: {path}");
                    oldStream = await _webDav.DownloadStreamAsync(path);
                    if (oldStream != null)
                    {
                        oldManifestPath = path;
                        _logger.LogInformation($"✅ Altes Manifest gefunden: {path}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("404") || ex.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
                        _logger.LogDebug($"❌ Manifest nicht gefunden unter: {path}");
                    else
                        _logger.LogWarning($"⚠️ [ChunkVergleich] Fehler beim Laden des Manifests ({path}): {ex.Message}");
                }
            }

            if (oldStream != null)
            {
                using var reader = new StreamReader(oldStream);
                var json = await reader.ReadToEndAsync();
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var oldManifest = doc.RootElement;

                    foreach (var chunk in oldManifest.GetProperty("Chunks").EnumerateArray())
                    {
                        int chunkIndex = chunk.GetProperty("Index").GetInt32();
                        string hash = chunk.GetProperty("Hash").GetString();
                        oldManifestDict[chunkIndex] = hash;
                    }

                    hasOldManifest = oldManifestDict.Count > 0;
                    _logger.LogInformation($"📜 [ChunkVergleich] Altes Manifest geladen → {oldManifestDict.Count} Chunks erkannt.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"⚠️ [ChunkVergleich] Fehler beim Lesen des alten Manifests: {ex.Message}");
                }
            }
            else
            {
                _logger.LogInformation("⚠️ Kein altes Manifest gefunden – vollständiger Upload erforderlich.");
            }

            // ============================================================
            // ⚡ 2️⃣ Global Hash Vergleich
            // ============================================================
            try
            {
                newFileStream.Position = 0;
                string newFileHash = ComputeSha256FullStream(newFileStream);
                _logger.LogInformation($"🔢 [HashCheck] Neuer Datei-Hash: {newFileHash}");

                if (hasOldManifest)
                {
                    string oldCombinedHash = ComputeSha256(
                        Encoding.UTF8.GetBytes(string.Join("", oldManifestDict.OrderBy(x => x.Key).Select(x => x.Value)))
                    );

                    _logger.LogInformation($"🔢 [HashCheck] Alter kombinierter Hash: {oldCombinedHash}");

                    if (newFileHash == oldCombinedHash)
                    {
                        _logger.LogInformation("🟢 [SkipUpload] Keine binären Änderungen erkannt → alle Chunks werden wiederverwendet.");

                        var manifestCopy = new
                        {
                            CreatedAt = DateTime.UtcNow,
                            Chunks = oldManifestDict.Select(kv => new
                            {
                                Index = kv.Key,
                                Hash = kv.Value,
                                Size = 0,
                                FirebasePath = $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{oldDokumentId}/chunk_{kv.Key:D4}.bin"
                            }).ToList()
                        };

                        string manifestJson = JsonSerializer.Serialize(manifestCopy, new JsonSerializerOptions { WriteIndented = true });
                        string newManifestPath = $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{newDokumentId}_manifest.json";

                        using var manifestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));
                        await _webDav.UploadStreamAsync(manifestStream, newManifestPath, "application/json");

                        _logger.LogInformation($"📄 Neues Manifest gespeichert (identisch, keine Änderungen): {newManifestPath}");
                        return manifestCopy.Chunks
                            .Select(c => new DokumentChunk { Index = c.Index, Hash = c.Hash, FirebasePath = c.FirebasePath, IsChanged = false })
                            .ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"⚠️ [HashCheck] Fehler bei globalem Hash-Vergleich: {ex.Message}");
            }

            newFileStream.Position = 0;

            // ============================================================
            // 🧠 Helper für Byte-Vergleich
            // ============================================================
            bool IsChunkTrulyModified(byte[] oldChunk, byte[] newChunk, double tolerance = TOLERANCE)
            {
                if (oldChunk.Length != newChunk.Length)
                    return true;

                int diffCount = 0;
                for (int i = 0; i < oldChunk.Length; i++)
                {
                    if (oldChunk[i] != newChunk[i])
                        diffCount++;
                }

                double diffRatio = (double)diffCount / oldChunk.Length;
                return diffRatio > tolerance;
            }

            // ============================================================
            // 3️⃣ Vergleich & Upload
            // ============================================================
            string baseChunkFolder = $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{oldDokumentId}";
            string newChunkFolder = $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{newDokumentId}";
            string newManifestPathFinal = $"{newChunkFolder}_manifest.json";

            int index = 0;
            byte[] buffer = new byte[CHUNK_SIZE];
            int bytesRead;
            int changedCount = 0, reusedCount = 0;
            long uploadedBytes = 0, totalBytes = 0;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation("");
            _logger.LogInformation("📤 [Upload] Beginne Chunk-Vergleich und Upload...");
            _logger.LogInformation("───────────────────────────────────────────────────────────────");

            while ((bytesRead = await newFileStream.ReadAsync(buffer, 0, CHUNK_SIZE)) > 0)
            {
                var chunkData = buffer.Take(bytesRead).ToArray();
                string hash = ComputeSha256(chunkData);
                totalBytes += bytesRead;

                bool isChanged = !hasOldManifest || !oldManifestDict.TryGetValue(index, out var oldHash) || oldHash != hash;

                // 🔍 Si chunk existant → vérifie son contenu
                if (isChanged && hasOldManifest && oldManifestDict.TryGetValue(index, out var oldHashValue))
                {
                    Stream? oldChunkStream = null;
                    bool chunkFound = false;
                    string oldChunkPath = string.Empty;

                    // Recherche intelligente multi-niveaux
                    string[] possibleChunkPaths =
                    {
                $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{oldDokumentId}/chunk_{index:D4}.bin",
                $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{oldDokumentId}/chunk_{index:D4}.bin",
                $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/{oldDokumentId}/chunk_{index:D4}.bin",
                $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{newDokumentId}/chunk_{index:D4}.bin"
            };

                    foreach (var candidatePath in possibleChunkPaths)
                    {
                        if (await _webDav.FileExistsAsync(candidatePath))
                        {
                            oldChunkStream = await _webDav.DownloadStreamStableAsync(candidatePath);
                            oldChunkPath = candidatePath;
                            chunkFound = true;
                            _logger.LogInformation($"✅ [ChunkFinder] Chunk {index:D3} gefunden → {candidatePath}");
                            break;
                        }
                    }

                    if (chunkFound && oldChunkStream != null)
                    {
                        using var msOld = new MemoryStream();
                        await oldChunkStream.CopyToAsync(msOld);
                        var oldChunkBytes = msOld.ToArray();

                        bool trulyModified = IsChunkTrulyModified(oldChunkBytes, chunkData);
                        if (!trulyModified)
                        {
                            _logger.LogInformation($"🟢 [DeepCompare] Chunk {index:D3} quasi identique → réutilisé ({oldChunkPath}).");

                            reusedCount++;
                            chunks.Add(new DokumentChunk
                            {
                                Index = index,
                                Hash = oldHashValue,
                                Size = bytesRead,
                                FirebasePath = oldChunkPath,
                                IsChanged = false
                            });

                            index++;
                            continue;
                        }
                        else
                        {
                            _logger.LogInformation($"🔴 [DeepCompare] Chunk {index:D3} modifié → upload requis.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"❌ [ChunkFinder] Chunk {index:D3} introuvable → upload forcé.");
                    }
                }

                // Upload du chunk modifié
                string chunkPath = $"{newChunkFolder}/chunk_{index:D4}.bin";
                using var ms = new MemoryStream(chunkData);
                await _webDav.UploadStreamAsync(ms, chunkPath, "application/octet-stream");
                _logger.LogInformation($"✅ [Chunk {index:D3}] Upload abgeschlossen ({bytesRead / 1024} KB)");

                changedCount++;
                uploadedBytes += bytesRead;

                chunks.Add(new DokumentChunk
                {
                    Index = index,
                    Hash = hash,
                    Size = bytesRead,
                    FirebasePath = chunkPath,
                    IsChanged = isChanged
                });

                index++;
            }

            stopwatch.Stop();

            // ============================================================
            // 📜 Manifest speichern
            // ============================================================
            var manifest = new
            {
                CreatedAt = DateTime.UtcNow,
                Chunks = chunks.Select(c => new { c.Index, c.Hash, c.Size, c.FirebasePath }).ToList()
            };

            string manifestJsonFinal = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

            using var manifestStreamFinal = new MemoryStream(Encoding.UTF8.GetBytes(manifestJsonFinal));
            await _webDav.UploadStreamAsync(manifestStreamFinal, newManifestPathFinal, "application/json");

            _logger.LogInformation($"📄 Neues Manifest gespeichert: {newManifestPathFinal}");
            _logger.LogInformation($"✅ [ChunkVergleich] Vorgang erfolgreich abgeschlossen ({changedCount} geändert, {reusedCount} wiederverwendet)");
            _logger.LogInformation("═══════════════════════════════════════════════════════");

            return chunks;
        }

        private string ComputeSha256FullStream(Stream stream)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            stream.Position = 0;
            byte[] hash = sha.ComputeHash(stream);
            stream.Position = 0;
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }


        public async Task<(bool Success, string Message)> UploadChangedChunksAsync(
       Guid oldDokumentId,
       Stream newFileStream,
       Guid newDokumentId,
       string firma,
       string abteilung,
       string kategorie)
        {
            try
            {
                // 🧩 Normalisation
                firma = (firma ?? "unbekannt").Trim().ToLowerInvariant();
                abteilung = (abteilung ?? "allgemein").Trim().ToLowerInvariant();
                kategorie = (kategorie ?? "").Trim().ToLowerInvariant();

                _logger.LogInformation("");
                _logger.LogInformation("═══════════════════════════════════════════════════════════════");
                _logger.LogInformation("🚀 [SmartChunkSave] Starte Upload-Prozess für neue Version");
                _logger.LogInformation($"🏢 Firma: {firma} | Abteilung: {abteilung} | Kategorie: {(string.IsNullOrEmpty(kategorie) ? "(leer)" : kategorie)}");
                _logger.LogInformation($"📄 Ursprüngliches Dokument: {oldDokumentId}");
                _logger.LogInformation($"🆕 Neue Version: {newDokumentId}");
                _logger.LogInformation("═══════════════════════════════════════════════════════════════");

                // ============================================================
                // 1️⃣ Chunk-Vergleich starten (CompareAndUploadNewVersionChunksAsync)
                // ============================================================
                _logger.LogInformation("⚙️  [Phase 1] Chunk-Vergleich und Upload der Änderungen...");
                _logger.LogInformation("───────────────────────────────────────────────────────────────");

                var chunks = await CompareAndUploadNewVersionChunksAsync(
                    oldDokumentId,
                    newFileStream,
                    newDokumentId,
                    firma,
                    abteilung,
                    kategorie
                );

                if (chunks == null || chunks.Count == 0)
                {
                    _logger.LogInformation("⚠️ [SmartChunkSave] Keine neuen Chunks erkannt – keine Änderungen gespeichert.");
                    return (false, "Keine neuen Chunks gefunden oder gespeichert.");
                }

                _logger.LogInformation("───────────────────────────────────────────────────────────────");
                _logger.LogInformation($"✅ [SmartChunkSave] Chunk-Vergleich abgeschlossen → {chunks.Count} Chunks insgesamt");
                _logger.LogInformation($"📊 Geändert: {chunks.Count(c => c.IsChanged)}, Wiederverwendet: {chunks.Count(c => !c.IsChanged)}");
                _logger.LogInformation("");

                // ============================================================
                // 2️⃣ Manifest erzeugen (stabiler Chunk-Pfad)
                // ============================================================
                _logger.LogInformation("🧾 [Phase 2] Erstelle Manifest-Datei...");

                // 🧠 Détermine le dossier stable partagé entre toutes les versions
                string baseChunkFolder = $"dokumente/{firma}/{abteilung}/{kategorie}/chunks/{oldDokumentId}";
                string versionChunkFolder = $"dokumente/{firma}/{abteilung}/{kategorie}/versionen/chunks/{newDokumentId}";
                string manifestPath = $"{baseChunkFolder}_manifest.json";

                // 🧩 Vérifie si le dossier stable existe, sinon fallback vers versionné
                if (!await _webDav.FileExistsAsync($"{baseChunkFolder}/chunk_0000.bin"))
                {
                    _logger.LogInformation("🟡 [Fallback] Kein gemeinsamer Chunk-Ordner gefunden → Nutzung des versionierten Pfads.");
                    baseChunkFolder = versionChunkFolder;
                    manifestPath = $"{versionChunkFolder}_manifest.json";
                }

                // ✅ Crée le manifest JSON
                var manifest = new ChunkManifest
                {
                    DokumentId = newDokumentId,
                    OriginalId = oldDokumentId,
                    Chunks = chunks.Select(c => new ChunkInfo
                    {
                        Index = c.Index,
                        File = Path.GetFileName(c.FirebasePath),
                        Hash = c.Hash
                    }).ToList()
                };

                string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

                await _webDav.EnsureFolderTreeExistsAsync(Path.GetDirectoryName(manifestPath));
                await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));

                _logger.LogInformation($"📡 [SmartChunkSave] Upload Manifest → {manifestPath}");
                await _webDav.UploadStreamAsync(ms, manifestPath, "application/json");
                _logger.LogInformation("✅ [SmartChunkSave] Manifest-Datei erfolgreich hochgeladen.");
                _logger.LogInformation("");

                // ============================================================
                // 3️⃣ Verknüpfungen Version <-> Chunks
                // ============================================================
                try
                {
                    _logger.LogInformation("🔗 [VersionChunks] Aufbau der Chunk-Verknüpfungen...");

                    var oldLinks = _db.DokumentVersionChunks.Where(vc => vc.VersionId == newDokumentId);
                    _db.DokumentVersionChunks.RemoveRange(oldLinks);
                    await _db.SaveChangesAsync();

                    foreach (var chunk in chunks)
                    {
                        var chunkEntity = await _db.DokumentChunks
                            .FirstOrDefaultAsync(c =>
                                c.FirebasePath.Contains(Path.GetFileName(chunk.FirebasePath)) ||
                                c.Hash == chunk.Hash);

                        if (chunkEntity != null)
                        {
                            // ✅ Vérifie la présence sur WebDAV
                            if (!await _webDav.FileExistsAsync(chunkEntity.FirebasePath))
                            {
                                _logger.LogWarning($"⚠️ Chunk fehlt auf WebDAV ({chunkEntity.FirebasePath}) → übersprungen");
                                continue;
                            }

                            _db.DokumentVersionChunks.Add(new DokumentVersionChunk
                            {
                                VersionId = newDokumentId,
                                ChunkId = chunkEntity.Id
                            });
                        }
                        else
                        {
                            _logger.LogWarning($"⚠️ Kein Chunk-Entity gefunden für {chunk.FirebasePath}");
                        }
                    }

                    await _db.SaveChangesAsync();
                    _logger.LogInformation($"✅ [VersionChunks] {chunks.Count} Chunk-Verknüpfungen erstellt für Version {newDokumentId}.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Fehler beim Aufbau der DokumentVersionChunks.");
                }

                _logger.LogInformation("═══════════════════════════════════════════════════════════════");
                _logger.LogInformation("🎉 [SmartChunkSave] Neuer Chunk-Upload abgeschlossen!");
                _logger.LogInformation($"📊 Gesamt: {chunks.Count} Chunks | Geändert: {chunks.Count(c => c.IsChanged)} | Wiederverwendet: {chunks.Count(c => !c.IsChanged)}");
                _logger.LogInformation($"📂 Manifest gespeichert unter: {manifestPath}");
                _logger.LogInformation("═══════════════════════════════════════════════════════════════");

                return (true, $"Manifest hochgeladen und {chunks.Count} Chunk-Verknüpfungen gespeichert.");
            }
            catch (Exception ex)
            {
                _logger.LogInformation("═══════════════════════════════════════════════════════════════");
                _logger.LogInformation($"❌ [SmartChunkSave] Fehler beim Upload der neuen Version: {ex.Message}");
                _logger.LogInformation("═══════════════════════════════════════════════════════════════");
                return (false, ex.Message);
            }
        }


        // ============================================================
        // 🔒 SHA256 Hash-Helfer
        // ============================================================
        private string ComputeSha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }
    }
}
