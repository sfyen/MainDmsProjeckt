using System.Text;
using Tesseract;
using System.IO;
using PdfPig = UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DmsProjeckt.Data
{
    public class PdfOcrUtil
    {
        public static async Task<string> ExtractTextAsync(Stream pdfStream)
        {
            pdfStream.Position = 0;
            var sb = new StringBuilder();

            // 🧾 1. Lecture de texte via PdfPig
            using (var doc = PdfPig.PdfDocument.Open(pdfStream))
            {
                foreach (var page in doc.GetPages())
                {
                    if (!string.IsNullOrWhiteSpace(page.Text))
                        sb.AppendLine(page.Text);
                }
            }

            if (sb.Length > 50)
                return sb.ToString();

            // 📂 2. OCR Fallback (Linux-compatible)
            // Note: For OCR on scanned PDFs, consider adding:
            // - PDFtoImage NuGet package (cross-platform PDF to image)
            // - Or use Ghostscript for rendering
            // Current implementation skips OCR to maintain Linux compatibility
            
            return sb.Length > 0 
                ? sb.ToString() 
                : "⚠️ Kein Text gefunden. OCR ist für Linux-Kompatibilität deaktiviert.";
        }
    }
}
