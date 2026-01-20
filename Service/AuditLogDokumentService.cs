using DmsProjeckt.Data;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Services
{
    public class AuditLogDokumentService
    {
        private readonly ApplicationDbContext _context;

        public AuditLogDokumentService(ApplicationDbContext context)
        {
            _context = context;
        }

        /// <summary>
        /// Enregistre une action d’audit liée à un document ou une version.
        /// </summary>
        public async Task EnregistrerAsync(string aktion, string benutzerId, Guid dokumentId)
        {
            // 🔍 1️⃣ Vérifier si le document existe dans la table principale
            var dokument = await _context.Dokumente.FirstOrDefaultAsync(d => d.Id == dokumentId);

            // 🔍 2️⃣ Si non trouvé → essayer dans les versions
            if (dokument == null)
            {
                var version = await _context.DokumentVersionen.FirstOrDefaultAsync(v => v.Id == dokumentId);

                if (version == null)
                {
                    // ⚠️ Statt Exception zu werfen, loggen wir nur und brechen ab (z.B. bei Löschvorgängen)
                    Console.WriteLine($"[AuditLog] WARNUNG: Dokument/Version {dokumentId} nicht gefunden. Audit-Eintrag '{aktion}' übersprungen.");
                    return;
                }

                // ✅ Récupérer le document original
                dokument = await _context.Dokumente.FirstOrDefaultAsync(d => d.Id == version.OriginalId);

                if (dokument == null)
                {
                    Console.WriteLine($"[AuditLog] WARNUNG: Originaldokument für Version {version.Id} nicht gefunden. Audit-Eintrag '{aktion}' übersprungen.");
                    return;
                }

                // Pour la traçabilité → le log se rattache à l’OriginalId
                dokumentId = version.OriginalId ?? dokumentId;
            }

            // 🧩 3️⃣ Créer l’entrée d’audit
            // 🛡️ Truncate Aktion to 50 chars to avoid SQL exceptions
            if (!string.IsNullOrEmpty(aktion) && aktion.Length > 50)
            {
                // Versuchen, vernünftig abzuschneiden (z.B. "..." am Ende)
                aktion = aktion.Substring(0, 47) + "...";
            }

            var log = new AuditLogDokument
            {
                Aktion = aktion,
                BenutzerId = benutzerId,
                DokumentId = dokumentId,
                Zeitstempel = DateTime.Now
            };

            // 💾 4️⃣ Enregistrer dans la base
            _context.AuditLogDokumente.Add(log);
            await _context.SaveChangesAsync();

            Console.WriteLine($"🧾 [AuditLog] Aktion='{aktion}' für Dokument={dokumentId} durch Benutzer={benutzerId}");
        }

        // 📜 Historique d’un utilisateur
        public async Task<List<AuditLogDokument>> ObtenirHistoriquePourBenutzerAsync(string benutzerId)
        {
            return await _context.AuditLogDokumente
                .Where(x => x.BenutzerId == benutzerId)
                .OrderByDescending(x => x.Zeitstempel)
                .ToListAsync();
        }

        // 📜 Historique d’un document
        public async Task<List<AuditLogDokument>> ObtenirHistoriqueParDokumentAsync(Guid dokumentId)
        {
            return await _context.AuditLogDokumente
                .Where(x => x.DokumentId == dokumentId)
                .OrderByDescending(x => x.Zeitstempel)
                .ToListAsync();
        }

        // 📜 Tous les logs avec navigation vers Dokument
        public async Task<List<AuditLogDokument>> ObtenirTousLesLogsAvecDokumentAsync()
        {
            return await _context.AuditLogDokumente
                .Include(l => l.Dokument)
                .OrderByDescending(l => l.Zeitstempel)
                .ToListAsync();
        }
    }
}
