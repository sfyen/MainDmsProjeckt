using DmsProjeckt.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RecentHistoryController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public RecentHistoryController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpGet("{userId}")]
        public async Task<ActionResult<IEnumerable<dynamic>>> GetRecentHistory(string userId)
        {
            // Try to find user by Id
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId);
            
            if (user == null)
            {
                // Fallback: If username is not found, maybe it's just a string match on Owner?
                // For now, return empty or try to find documents where user matches
                return Ok(new List<object>()); 
            }

            // Example logic: Get last 5 documents for this user
            var recentDocs = await _context.Dokumente
                .Where(d => d.ApplicationUserId == user.Id)
                .OrderByDescending(d => d.HochgeladenAm)
                .Take(5)
                .Select(d => new 
                {
                   d.Id,
                   Titel = d.Dateiname, // Use 'Titel' or 'Dateiname' consistent with Frontend Model
                   d.HochgeladenAm,
                   Thumbnail = "" 
                })
                .ToListAsync();

            return Ok(recentDocs);
        }
    }
}
