using System.Globalization;
using System.Text.RegularExpressions;

namespace DmsProjeckt.Service
{
    public class OcrMetadataExtractorService
    {
        public class OcrMetadataResult
        {
            public string Rechnungsnummer { get; set; } = "";
            public string Rechnungsbetrag { get; set; } = "";
            public string Rechnungsdatum { get; set; } = "";
            public string Lieferdatum { get; set; } = "";
            public string Kundennummer { get; set; } = "";
            public string Kategorie { get; set; } = "";
            public string Titel { get; set; } = "";
            public string Autor { get; set; } = "";
            public string Betreff { get; set; } = "";
            public string Stichworte { get; set; } = "";
            public string Zahlungsbedingungen { get; set; } = "";
            public string Lieferart { get; set; } = "";
            public string Steuerbetrag { get; set; } = "";
            public string ArtikelAnzahl { get; set; } = "";
            public string Email { get; set; } = "";
            public string Telefon { get; set; } = "";
            public string Telefax { get; set; } = "";
            public string IBAN { get; set; } = "";
            public string BIC { get; set; } = "";
            public string Bankverbindung { get; set; } = "";
            public string Zeitraum { get; set; } = "";
            public string SteuerNr { get; set; } = "";
            public string Gesamtpreis { get; set; } = "";
            public string AnsprechPartner { get; set; } = "";
            public string Adresse { get; set; } = "";
            public string Website { get; set; } = "";
            public string Nettobetrag { get; set; } = "";
            public string? PdfAutor { get; set; } = "";
            public string? PdfBetreff { get; set; } = "";
            public string? Schluesselwoerter { get; set; } = "";
            public string? Faelligkeitsdatum { get; set; } = "";
            public string? UIDNummer { get; set; } = "";
        }
        public static OcrMetadataResult Extract(string ocrText)
        {
            var result = new OcrMetadataResult();
            var cleanedText = Regex.Replace(ocrText ?? string.Empty, "\\s{2,}", " ").Trim();
            var lowerText = cleanedText.ToLowerInvariant();

            // === 📧 Kontakt ===
            result.Email = MatchValue(cleanedText, @"[\w\.-]+@[\w\.-]+\.\w+", 0);
            result.Telefon = MatchValue(cleanedText, @"(?i)(Telefon|Tel\.?|Phone)\s*[:\-]?\s*([\+0-9\s\/\-]{6,})", 2);
            result.Telefax = MatchValue(cleanedText, @"(?i)(Fax|Telefax)\s*[:\-]?\s*([\+0-9\s\/\-]{6,})", 2);
            result.Website = MatchValue(cleanedText, @"(?i)\bwww\.[\w\-\.]+\b", 0);

            // === 💳 Zahlungen & Finanzen ===
            result.IBAN = MatchValue(cleanedText, @"\bDE\d{2}[\s\d]{10,26}\b", 0);
            result.BIC = MatchValue(cleanedText, @"(?i)\bBIC[:\s]*([A-Z0-9]{6,11})\b", 1);
            if (!string.IsNullOrWhiteSpace(result.BIC))
                result.BIC = result.BIC.Replace("DXX", "XXX").Trim();

            result.SteuerNr = MatchValue(cleanedText, @"(?i)Steuer[-\s]?Nr[\s:]*([0-9/]+)", 1);
            result.UIDNummer = MatchValue(cleanedText, @"(?i)(UID[-\s]?Nummer|USt[-\s]?IdNr?|VAT[-\s]?No\.?)\s*[:\-]?\s*(DE[0-9A-Z]+)", 2);
            result.Bankverbindung = MatchValue(cleanedText, @"(?i)(Bankverbindung|Kreditinstitut)\s*[:\-]?\s*([^\n\r]+)", 2);

            // === 📅 Datumsangaben ===
            result.Rechnungsdatum = MatchValue(cleanedText, @"(?i)(Rechnungsdatum|Datum)\s*[:\-]?\s*([0-9]{2}\.[0-9]{2}\.[0-9]{4})", 2);
            result.Faelligkeitsdatum = MatchValue(cleanedText, @"(?i)(Fälligkeitsdatum|Zahlbar bis|Fälligkeit)\s*[:\-]?\s*([0-9]{2}\.[0-9]{2}\.[0-9]{4})", 2);
            result.Lieferdatum = MatchValue(cleanedText, @"(?i)(Lieferdatum|Leistungsdatum)\s*[:\-]?\s*(\d{2}\.\d{2}\.\d{4})", 2)
                ?? MatchValue(cleanedText, @"(?i)(Lieferdatum|Leistungsdatum)\s*[:\-]?\s*([A-Za-zÄÖÜäöüß]+\s*[0-9]{4})", 2);

            // 🧩 Korrektur: unrealistische Datumsrelationen fixen
            if (DateTime.TryParse(result.Lieferdatum, out var liefer) &&
                DateTime.TryParse(result.Rechnungsdatum, out var rechnung) &&
                liefer > rechnung)
            {
                result.Lieferdatum = result.Rechnungsdatum;
            }

            // === 📆 Leistungszeitraum ===
            result.Zeitraum =
                MatchValue(cleanedText,
                    @"(?i)(Leistungszeitraum|Liefer-?zeitraum|Zeitraum)\s*[:\-]?\s*([0-9]{2}[./][0-9]{2}[./][0-9]{4})\s*(bis|-|–)\s*([0-9]{2}[./][0-9]{2}[./][0-9]{4})", 2)
                + " bis " +
                MatchValue(cleanedText,
                    @"(?i)(Leistungszeitraum|Liefer-?zeitraum|Zeitraum)\s*[:\-]?[0-9]{2}[./][0-9]{2}[./][0-9]{4}\s*(bis|-|–)\s*([0-9]{2}[./][0-9]{2}[./][0-9]{4})", 4);

            if (string.IsNullOrWhiteSpace(result.Zeitraum))
                result.Zeitraum = MatchValue(cleanedText, @"(?i)(Zeitraum|Leistungszeitraum)\s*[:\-]?\s*([A-Za-zÄÖÜäöüß]+\s*[0-9]{4})", 2);

            // === 👥 Identifikation ===
            result.AnsprechPartner = MatchValue(cleanedText, @"(?i)(Ansprechpartner|Herr|Frau)\s*[:\-]?\s*([A-ZÄÖÜ][a-zäöüß]+\s+[A-ZÄÖÜ][a-zäöüß]+)", 2);
            if (!string.IsNullOrWhiteSpace(result.AnsprechPartner))
                result.AnsprechPartner = Regex.Replace(result.AnsprechPartner, @"\b(Datum|Rechnungsdatum|Kundennummer|PLZ|Ort)\b.*", "").Trim();

            result.Kundennummer = MatchValue(cleanedText, @"(?i)(Kundennummer|Kunden[-\s]?Nr\.?)\s*[:\-]?\s*([A-Za-z0-9\-\/]+)", 2);
            result.Rechnungsnummer = MatchValue(cleanedText, @"(?i)(Rechnungsnummer|Rechnung\s*Nr\.?)\s*[:\-]?\s*([A-Za-z0-9\-\/]+)", 2);

            // === 💰 Beträge ===
            result.Nettobetrag = NormalizeDecimal(MatchValue(cleanedText, @"(?i)(Nettobetrag|Summe\s*Netto|Netto|Nettosumme)\s*[:\-]?\s*([\d\.,]+)", 2));
            result.Steuerbetrag = NormalizeDecimal(MatchValue(cleanedText, @"(?i)(MwSt|USt|Steuerbetrag|Umsatzsteuer)\s*[:\-]?\s*([\d\.,]+)", 2));
            result.Gesamtpreis = NormalizeDecimal(MatchValue(cleanedText, @"(?i)(Gesamtbetrag|Bruttobetrag|Bruttosumme|Rechnungssumme|Summe\s*(brutto)?)\s*[:\-]?\s*([\d\.,]+)", 3));

            // === 📦 Artikel ===
            result.ArtikelAnzahl = Regex.Matches(cleanedText, @"(?i)\b(Nr\.|Bezeichnung|Pos\.|Menge|Artikel)\b", RegexOptions.IgnoreCase).Count.ToString();
            var countedLines = Regex.Matches(cleanedText, @"(?m)^\s*\d+\.\s+.*\d+[,.]\d{2}", RegexOptions.Multiline).Count;
            if (countedLines > 0)
                result.ArtikelAnzahl = countedLines.ToString();

            result.Lieferart = MatchValue(cleanedText, @"(?i)(Lieferart|Versandart)\s*[:\-]?\s*([^\n]+)", 2);

            // === 🏠 Adressen ===
            result.Adresse = MatchValue(cleanedText,
                    @"(?i)([A-ZÄÖÜa-zäöüß\-\.]+\s*(Straße|Str\.|Gasse|Platz|Weg|Allee)\s*\d+[,\s]*\d{5}\s*[A-Za-zÄÖÜäöüß]+)", 1)
                ?? MatchValue(cleanedText,
                    @"(?i)(Am|An\sder|Zum)\s+[A-ZÄÖÜa-zäöüß]+\s*\d*[,\s]*\d{5}\s*[A-ZÄÖÜa-zäöüß]+", 0);

            if (!string.IsNullOrWhiteSpace(result.Adresse))
            {
                // Fix "Straße 1/12345" → "Straße 1 12345"
                result.Adresse = Regex.Replace(result.Adresse, @"(\d{1,5})/(\d{5})", "$1 $2");
                result.Adresse = Regex.Replace(result.Adresse,
                    @"([A-Za-zÄÖÜäöüß]+\s*(Straße|Str\.|Gasse|Platz|Weg|Allee)\s*\d{1,5})\s*(\d{5})(?=\s*[A-Za-zÄÖÜäöüß])",
                    "$1 $3");
                // Fix fehlendes Leerzeichen zwischen Hausnummer und PLZ
                result.Adresse = Regex.Replace(result.Adresse, @"(\d{1,5})(PLZ\s*\d{4,5})", "$1 $2");
                // Entferne "PLZ " falls OCR das so erkannt hat
                result.Adresse = result.Adresse.Replace("PLZ ", "");
            }
            if (!string.IsNullOrWhiteSpace(result.Zahlungsbedingungen))
                Console.WriteLine($"💬 Zahlungsbedingungen erkannt: {result.Zahlungsbedingungen}");

            // === 💬 Zahlungsbedingungen ===
            result.Zahlungsbedingungen = MatchValue(cleanedText,
                @"(?i)(Zahlungsbedingungen|Zahlbar bis|innerhalb von\s+\d+\s*Tagen[^\n]*)", 0);


            // === 🧹 Aufräumen ===
            foreach (var prop in typeof(OcrMetadataResult).GetProperties())
            {
                if (prop.PropertyType == typeof(string))
                {
                    var val = (string)prop.GetValue(result);
                    if (!string.IsNullOrWhiteSpace(val))
                        prop.SetValue(result, Regex.Replace(val.Replace("?", "").Trim(), @"\s{2,}", " "));
                }
            }

            // === 🚫 Filterlogik ===
            if (result.Zeitraum?.Trim().ToLower() == "bis") result.Zeitraum = string.Empty;
            if (result.Adresse?.Length < 8) result.Adresse = string.Empty;

            return result;
        }



        // === 🧮 Normalisation numérique ===
        private static string NormalizeDecimal(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            return input.Replace(".", "").Replace(",", ".").Trim();
        }

        // === 🔍 Extraction simple ===
        private static string MatchValue(string text, string pattern, int group)
        {
            var match = Regex.Match(text, pattern, RegexOptions.Multiline);
            return match.Success ? match.Groups[group].Value.Trim() : null;
        }

        // === 🧠 Détection de mots-clés ===
        private static List<string> DetectKeywords(string text)
        {
            var keywords = new[]
            {
                "Gebührenrechnung", "Postfach", "Telefon", "Telefax", "Email",
                "AnsprechPartner", "Adresse", "Website", "Partnerschaftsregister",
                "Gesetz", "Zeitraum", "Umsatzsteuer", "IBAN", "BIC", "Autor", "Betreff", "PdfSchluesselwoerter"
            };
            return keywords.Where(k => text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                           .Distinct()
                           .ToList();
        }
    }
}
