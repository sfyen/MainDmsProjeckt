using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DmsProjeckt.Data;

namespace DmsProjeckt.Service
{
    public class DueTaskNotificationService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private const int MaxWaitMinutes = 60; // Maximale Wartezeit bevor erneut geprüft wird
        private const int MinWaitSeconds = 10; // Minimale Wartezeit
        
        public DueTaskNotificationService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan nextWait = TimeSpan.FromMinutes(MaxWaitMinutes);

                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var now = DateTime.UtcNow;

                    // NotificationType Ids für fällige Aufgaben
                    var faelligTypeIds = await context.NotificationTypes
                        .Where(nt => nt.Name == "Due" || nt.Name == "DueWF" || nt.Name == "Due email" || nt.Name == "DueWFEmail")
                        .Select(nt => nt.Id)
                        .ToListAsync(stoppingToken);

                    // Alle aktivierten Settings laden
                    var settings = await context.UserNotificationSettings
                        .Where(s => faelligTypeIds.Contains(s.NotificationTypeId) && s.Enabled)
                        .ToListAsync(stoppingToken);

                    if (settings.Count == 0)
                    {
                        Console.WriteLine($"[DueTaskNotification] Keine aktivierten Settings gefunden. Warte {MaxWaitMinutes} Minuten.");
                        await Task.Delay(TimeSpan.FromMinutes(MaxWaitMinutes), stoppingToken);
                        continue;
                    }

                    // Maximalen Vorlauf bestimmen
                    int advanceMax = settings.Max(s => s.AdvanceMinutes ?? 60);
                    var windowEnd = now.AddMinutes(advanceMax + 5); // +5 Min Puffer

                    // Alle offenen Aufgaben im Zeitfenster laden
                    var offeneAufgaben = await context.Aufgaben
                        .Where(a => a.FaelligBis > now && a.FaelligBis <= windowEnd && !a.Erledigt && a.Aktiv)
                        .ToListAsync(stoppingToken);

                    Console.WriteLine($"[DueTaskNotification] {now:HH:mm:ss}: {offeneAufgaben.Count} Aufgaben im Fenster bis {windowEnd:HH:mm}");

                    DateTime? nextNotifyTime = null;

                    foreach (var aufgabe in offeneAufgaben)
                    {
                        foreach (var typeId in faelligTypeIds)
                        {
                            var userSetting = settings.FirstOrDefault(
                                s => s.UserId == aufgabe.FuerUser && s.NotificationTypeId == typeId);

                            if (userSetting == null) continue;

                            int advance = userSetting.AdvanceMinutes ?? 60;
                            DateTime notifyAt = aufgabe.FaelligBis.AddMinutes(-advance);

                            // Wenn Benachrichtigungszeit schon vorbei war als Aufgabe erstellt wurde,
                            // keine Benachrichtigung senden (z.B. Aufgabe fällig in 20 Min, aber 1h Vorlauf gewünscht)
                            if (notifyAt < aufgabe.ErstelltAm)
                            {
                                Console.WriteLine($"[DueTaskNotification] -> '{aufgabe.Titel}' übersprungen: Benachrichtigungszeit ({notifyAt:HH:mm}) war vor Erstellzeit ({aufgabe.ErstelltAm:HH:mm}).");
                                continue;
                            }

                            // Wenn Zeitpunkt noch nicht erreicht, merke für nächsten Wartezyklus
                            if (notifyAt > now)
                            {
                                if (nextNotifyTime == null || notifyAt < nextNotifyTime)
                                    nextNotifyTime = notifyAt;
                                continue;
                            }

                            // Prüfe ob bereits gesendet
                            bool alreadySent = await context.UserNotifications
                                .Include(un => un.Notification)
                                .AnyAsync(un =>
                                    un.UserId == aufgabe.FuerUser &&
                                    un.Notification.NotificationTypeId == typeId &&
                                    un.Notification.Content == $"Die Aufgabe \"{aufgabe.Titel}\" ist fällig am {aufgabe.FaelligBis:g}.",
                                    stoppingToken);

                            if (alreadySent)
                            {
                                Console.WriteLine($"[DueTaskNotification] -> '{aufgabe.Titel}' bereits benachrichtigt.");
                                continue;
                            }

                            // Zeitpunkt erreicht und noch nicht gesendet -> Sende Benachrichtigung
                            if (notifyAt <= now && aufgabe.FaelligBis > now)
                            {
                                var notificationTitle = (typeId == faelligTypeIds[0] || typeId == faelligTypeIds[2])
                                    ? "Aufgabe fällig"
                                    : "Workflowaufgabe fällig";

                                var notification = new Notification
                                {
                                    Title = notificationTitle,
                                    Content = $"Die Aufgabe \"{aufgabe.Titel}\" ist fällig am {aufgabe.FaelligBis:g}.",
                                    CreatedAt = DateTime.UtcNow,
                                    NotificationTypeId = typeId
                                };
                                context.Notifications.Add(notification);
                                await context.SaveChangesAsync(stoppingToken);

                                var userNotification = new UserNotification
                                {
                                    UserId = aufgabe.FuerUser,
                                    NotificationId = notification.Id,
                                    IsRead = false,
                                    ReceivedAt = DateTime.UtcNow,
                                    SendAt = DateTime.UtcNow
                                };
                                context.UserNotifications.Add(userNotification);
                                await context.SaveChangesAsync(stoppingToken);

                                Console.WriteLine($"[DueTaskNotification] ✅ Benachrichtigung für '{aufgabe.Titel}' gesendet.");
                            }
                        }
                    }

                    // Berechne optimale Wartezeit bis zur nächsten Benachrichtigung
                    if (nextNotifyTime.HasValue)
                    {
                        var waitTime = nextNotifyTime.Value - DateTime.UtcNow;
                        if (waitTime.TotalSeconds > MinWaitSeconds)
                        {
                            nextWait = waitTime;
                            Console.WriteLine($"[DueTaskNotification] Nächste Benachrichtigung in {waitTime.TotalMinutes:F1} Minuten.");
                        }
                        else
                        {
                            nextWait = TimeSpan.FromSeconds(MinWaitSeconds);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[DueTaskNotification] Keine anstehenden Benachrichtigungen. Warte {MaxWaitMinutes} Min.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DueTaskNotification] Fehler: {ex.Message}");
                    nextWait = TimeSpan.FromMinutes(1); // Bei Fehler nach 1 Min erneut versuchen
                }

                await Task.Delay(nextWait, stoppingToken);
            }
        }
    }
}
