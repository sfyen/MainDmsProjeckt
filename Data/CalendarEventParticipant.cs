namespace DmsProjeckt.Data
{
    public class CalendarEventParticipant
    {
        public int Id { get; set; }

        public int CalendarEventId { get; set; }
        public CalendarEvent CalendarEvent { get; set; }

        public string UserId { get; set; }
        public ApplicationUser User { get; set; }

        public EventParticipationStatus Status { get; set; } = EventParticipationStatus.Pending;
        public DateTime? RespondedAt { get; set; }
    }
    public enum EventParticipationStatus
    {
        Pending = 0,
        Accepted = 1,
        Declined = 2
    }

}
