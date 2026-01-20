using DmsProjeckt.Data;
using DmsProjeckt.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DmsProjeckt.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = "Identity.Application, Bearer")]
    public class DocumentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly WebDavStorageService _webDavStorage;

        public DocumentsController(ApplicationDbContext context, WebDavStorageService webDavStorage)
        {
            _context = context;
            _webDavStorage = webDavStorage;
        }

        [HttpGet("view/{id}")]
        public async Task<IActionResult> ViewDocument(Guid id)
        {
            Console.WriteLine($"[DocumentsController] ViewDocument called with ID: {id}");
            
            var document = await _context.Dokumente
                .FirstOrDefaultAsync(d => d.Id == id);

            if (document == null)
            {
                Console.WriteLine($"[DocumentsController] Document not found: {id}");
                return NotFound("Document not found in database");
            }

            Console.WriteLine($"[DocumentsController] Document found: {document.Dateiname}");
            Console.WriteLine($"[DocumentsController] ObjectPath: {document.ObjectPath}");

            try
            {
                // Download from WebDAV
                var fileStream = await _webDavStorage.DownloadStreamAsync(document.ObjectPath);
                if (fileStream == null || fileStream.Length == 0)
                {
                    Console.WriteLine($"[DocumentsController] File not found on WebDAV");
                    return NotFound("File not found on storage");
                }

                Console.WriteLine($"[DocumentsController] File downloaded from WebDAV, size: {fileStream.Length} bytes");
                var contentType = "application/pdf";

                return File(fileStream, contentType, document.Dateiname);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DocumentsController] Error downloading file: {ex.Message}");
                return StatusCode(500, "Error retrieving file from storage");
            }
        }

        // 📱 MOBILE-SPECIFIC ENDPOINT - Allows anonymous access for browser viewing
        [AllowAnonymous]
        [HttpGet("mobile/view/{id}")]
        public async Task<IActionResult> MobileViewDocument(Guid id)
        {
            Console.WriteLine($"[DocumentsController] MobileViewDocument called with ID: {id}");
            
            var document = await _context.Dokumente
                .FirstOrDefaultAsync(d => d.Id == id);

            if (document == null)
            {
                Console.WriteLine($"[DocumentsController] Document not found: {id}");
                return NotFound("Document not found in database");
            }

            Console.WriteLine($"[DocumentsController] Document found: {document.Dateiname}");
            Console.WriteLine($"[DocumentsController] ObjectPath: {document.ObjectPath}");

            try
            {
                // Download from WebDAV
                var fileStream = await _webDavStorage.DownloadStreamAsync(document.ObjectPath);
                if (fileStream == null || fileStream.Length == 0)
                {
                    Console.WriteLine($"[DocumentsController] File not found on WebDAV");
                    return NotFound("File not found on storage");
                }

                Console.WriteLine($"[DocumentsController] File downloaded from WebDAV for mobile, size: {fileStream.Length} bytes");
                var contentType = "application/pdf";
                
                // Set Content-Disposition to inline for browser viewing
                Response.Headers["Content-Disposition"] = $"inline; filename=\"{document.Dateiname}\"";
                
                return File(fileStream, contentType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DocumentsController] Error downloading file: {ex.Message}");
                return StatusCode(500, "Error retrieving file from storage");
            }
        }

        [HttpGet("download/{id}")]
        public async Task<IActionResult> DownloadDocument(Guid id)
        {
            Console.WriteLine($"[DocumentsController] DownloadDocument called with ID: {id}");
            
            var document = await _context.Dokumente
                .FirstOrDefaultAsync(d => d.Id == id);

            if (document == null)
            {
                Console.WriteLine($"[DocumentsController] Document not found: {id}");
                return NotFound("Document not found");
            }

            Console.WriteLine($"[DocumentsController] Downloading: {document.Dateiname}");
            Console.WriteLine($"[DocumentsController] ObjectPath: {document.ObjectPath}");

            try
            {
                // Download from WebDAV
                var fileStream = await _webDavStorage.DownloadStreamAsync(document.ObjectPath);
                if (fileStream == null || fileStream.Length == 0)
                {
                    Console.WriteLine($"[DocumentsController] File not found on WebDAV");
                    return NotFound("File not found on storage");
                }

                Console.WriteLine($"[DocumentsController] File ready for download, size: {fileStream.Length} bytes");
                var contentType = "application/pdf";

                // Force download with attachment header
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{document.Dateiname}\"";
                return File(fileStream, contentType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DocumentsController] Error downloading file: {ex.Message}");
                return StatusCode(500, "Error retrieving file from storage");
            }
        }
    }
}
