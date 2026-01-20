using DmsProjeckt.Data;
using DmsProjeckt.Helpers;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DmsProjeckt.Service
{
    public class VersionierungsService
    {
        private readonly ApplicationDbContext _db;
        private readonly WebDavStorageService _webDav;

        public VersionierungsService(ApplicationDbContext db, WebDavStorageService webDav)
        {
            _db = db;
            _webDav = webDav;
        }

        public async Task SpeichereVersionAsync(Guid dokumentId, string userId, string? customLabel = null, object? meta = null)
        {
            // 🔹 Original laden inkl. Abteilung & Chunks
            var original = await _db.Dokumente
                .Include(d => d.Abteilung)
                .Include(d => d.Chunks)
                .FirstOrDefaultAsync(d => d.Id == dokumentId);

            if (original == null)
                throw new InvalidOperationException($"❌ Dokument {dokumentId} nicht gefunden.");

            if (string.IsNullOrWhiteSpace(original.ObjectPath))
                throw new InvalidOperationException("❌ ObjectPath ist leer – Datei nicht gefunden.");

            // 🔹 Prüfen, ob Benutzer berechtigt ist
            bool isAdmin = await (
                from ur in _db.UserRoles
                join r in _db.Roles on ur.RoleId equals r.Id
                where ur.UserId == userId && (r.Name == "Admin" || r.Name == "SuperAdmin")
                select ur
            ).AnyAsync();

            if (!isAdmin)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null)
                    throw new UnauthorizedAccessException("❌ Benutzer nicht gefunden.");

                if (original.ApplicationUserId != userId && original.AbteilungId != user.AbteilungId)
                    throw new UnauthorizedAccessException("❌ Keine Berechtigung für diese Versionierung.");
            }

            // 🔹 Anzahl bestehender Versionen bestimmen
            var existingCount = await _db.DokumentVersionen
                .CountAsync(v => v.DokumentId == original.Id);

            var label = string.IsNullOrWhiteSpace(customLabel)
                ? $"v{existingCount + 1}"
                : customLabel.Trim();

            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");

            // 🔹 Zielpfad für neue Version
            var (destinationPath, abteilungId) = DocumentPathHelper.BuildFinalPath(
                firma: original.ObjectPath?.Split('/')[1] ?? "unbekannt",
                fileName: $"{timestamp}_{original.Dateiname}",
                kategorie: "versionen",
                abteilungId: original.AbteilungId,
                abteilungName: original.Abteilung?.Name
            );

            // 🔹 Metadaten serialisieren
            var metaSource = meta ?? (object?)original.MetadatenObjekt ?? original;

            string metadataJson = JsonSerializer.Serialize(metaSource, new JsonSerializerOptions
            {
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
                WriteIndented = false
            });


            // 🔹 Version-Objekt anlegen
            var version = new DokumentVersionen
            {
                Id = Guid.NewGuid(),
                DokumentId = original.Id,
                ApplicationUserId = userId,
                Dateiname = original.Dateiname,
                Dateipfad = $"{_webDav.BaseUrl.TrimEnd('/')}/{destinationPath.TrimStart('/')}",
                ObjectPath = destinationPath,
                HochgeladenAm = DateTime.UtcNow,
                VersionsLabel = label,
                MetadataJson = metadataJson,
                AbteilungId = abteilungId,
                IsVersion = true,
                EstSigne = false
            };

            _db.DokumentVersionen.Add(version);
            await _db.SaveChangesAsync();

            // =======================================================
            // 🧩 Falls das Dokument chunked ist → Chunks kopieren
            // =======================================================
            // =======================================================
            // 🧩 Falls das Dokument chunked ist → Chunks kopieren
            // =======================================================
            if (original.IsChunked && original.Chunks != null && original.Chunks.Any())
            {
                Console.WriteLine($"🧩 Dokument ist chunked – {original.Chunks.Count} Chunks werden versioniert...");

                foreach (var chunk in original.Chunks.OrderBy(c => c.Index))
                {
                    // 🔹 Neue Verknüpfung Version <-> Chunk erstellen
                    var versionChunk = new DokumentVersionChunk
                    {
                        VersionId = version.Id,
                        ChunkId = chunk.Id
                    };

                    _db.DokumentVersionChunks.Add(versionChunk);
                }

                await _db.SaveChangesAsync();
                Console.WriteLine("✅ Chunk-Verknüpfungen erfolgreich erstellt.");
            }
            else
            {
                // 🔹 Klassische Datei auf WebDAV kopieren
                var sourcePath = original.ObjectPath.Replace('\\', '/');
                Console.WriteLine($"📁 Kopiere Datei von {sourcePath} nach {destinationPath}");
                //await _webDav.CopyFilesAsync(sourcePath, destinationPath);
            }


            Console.WriteLine($"✅ Neue Version gespeichert: {label} für Dokument {original.Id}");
        }

        public async Task<List<DokumentVersionen>> HoleVersionenZumOriginalAsync(Dokumente dokument)
        {
            if (dokument == null)
                return new List<DokumentVersionen>();

            return await _db.DokumentVersionen
                .Where(v => v.DokumentId == dokument.Id)
                .OrderByDescending(v => v.HochgeladenAm)
                .ToListAsync();
        }
    }
}
