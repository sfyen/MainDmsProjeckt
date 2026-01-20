using System.Security.Cryptography;
using DmsProjeckt.Data;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Service
{
    public class DocumentHashService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly WebDavStorageService _WebDav;

        public DocumentHashService(ApplicationDbContext dbContext, WebDavStorageService WebDiv)
        {
            _dbContext = dbContext;
            _WebDav = WebDiv;
        }

        /// <summary>
        /// Berechnet den SHA256-Hash eines Datei-Streams.
        /// </summary>
        public string ComputeHash(Stream fileStream)
        {
            using var sha = SHA256.Create();
            fileStream.Position = 0;
            var hashBytes = sha.ComputeHash(fileStream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Prüft, ob bereits eine Datei mit demselben Hash existiert.
        /// </summary>
        public async Task<Dokumente?> FindExistingAsync(string fileHash)
        {
            return await _dbContext.Dokumente.FirstOrDefaultAsync(d => d.FileHash == fileHash);
        }

        /// <summary>
        /// Speichert eine Datei neu oder nutzt eine bestehende Datei wieder, falls der Hash übereinstimmt.
        /// </summary>
        public async Task<(bool reused, string firebasePath, string hash)> SaveOrReuseAsync(
            Guid dokumentId, byte[] fileBytes)
        {
            // 🔹 1️⃣ Hash berechnen
            var hash = ComputeHash(new MemoryStream(fileBytes));

            // 🔹 2️⃣ Prüfen, ob dieser Hash bereits vorhanden ist
            var existing = await FindExistingAsync(hash);

            if (existing != null)
            {
                Console.WriteLine($"♻️ Datei bereits vorhanden: {existing.ObjectPath}");
                return (true, existing.ObjectPath, hash);
            }

            // 🔹 3️⃣ Andernfalls Datei nach Firebase hochladen
            string path = $"dokumente/{dokumentId}_v{DateTime.UtcNow:yyyyMMddHHmmss}.pdf";
            using var uploadStream = new MemoryStream(fileBytes);

            try
            {
                await _WebDav.UploadStreamAsync(uploadStream, path, "application/pdf");
                Console.WriteLine($"✅ Neue Datei erfolgreich hochgeladen: {path}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fehler beim Hochladen nach Firebase: {ex.Message}");
                throw;
            }

            return (false, path, hash);
        }
    }
}
