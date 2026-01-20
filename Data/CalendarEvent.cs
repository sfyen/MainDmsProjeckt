using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace DmsProjeckt.Data
{
    public class CalendarEvent
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }

        // 🔹 Neu: Zeitraum-Unterstützung
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }

        // 🔹 Uhrzeiten (jetzt als Strings für einfaches JSON-Parsing)
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }

        public string CreatedById { get; set; }
        public ApplicationUser CreatedBy { get; set; }

        // 🔹 Neu: Typ für automatische Farbzuordnung
        public string EventType { get; set; } = "personal"; // personal, meeting, task

        public string Color { get; set; } = "#b8a5ff";
        public bool AllDay { get; set; } = true;

        public string UserId { get; set; }
        public int? RelatedAufgabeId { get; set; }
        public ApplicationUser? User { get; set; }

        public ICollection<CalendarEventParticipant> Participants { get; set; }
        = new List<CalendarEventParticipant>();
    }

}
