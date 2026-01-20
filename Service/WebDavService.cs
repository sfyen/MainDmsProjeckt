using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using WebDav;

namespace DmsProjeckt.Service
{
    public class WebDavService
    {
        private readonly IWebDavClient _client;
        private readonly string _basePath;

        public WebDavService(string serverUrl, string username, string password, string basePath = "/")
        {
            // ⚠️ Autorise temporairement les certificats auto-signés (NAS Synology)
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            var clientParams = new WebDavClientParams
            {
                BaseAddress = new Uri(serverUrl),
                Credentials = new NetworkCredential(username, password)
            };

            _client = new WebDavClient(clientParams);
            _basePath = basePath.TrimEnd('/');
        }

        /// <summary>
        /// Envoie un fichier sur le serveur WebDAV
        /// </summary>
        public async Task<bool> UploadFileAsync(string relativePath, Stream fileStream)
        {
            try
            {
                var remotePath = $"{_basePath}/{relativePath}".Replace("//", "/");
                var response = await _client.PutFile(remotePath, fileStream);

                if (!response.IsSuccessful)
                {
                    Console.WriteLine($"[WebDAV ERROR] Upload failed: {response.StatusCode} - {response.Description}");
                }
                else
                {
                    Console.WriteLine($"[WebDAV SUCCESS] File uploaded to {remotePath}");
                }

                return response.IsSuccessful;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebDAV EXCEPTION] UploadFileAsync: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Télécharge un fichier depuis le serveur WebDAV
        /// </summary>
        public async Task<Stream?> DownloadFileAsync(string relativePath)
        {
            try
            {
                var remotePath = $"{_basePath}/{relativePath}".Replace("//", "/");
                var response = await _client.GetRawFile(remotePath);

                if (!response.IsSuccessful)
                {
                    Console.WriteLine($"[WebDAV ERROR] Download failed: {response.StatusCode} - {response.Description}");
                    return null;
                }

                Console.WriteLine($"[WebDAV SUCCESS] File downloaded: {remotePath}");
                return response.Stream;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebDAV EXCEPTION] DownloadFileAsync: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Supprime un fichier du serveur WebDAV
        /// </summary>
        public async Task<bool> DeleteFileAsync(string relativePath)
        {
            try
            {
                var remotePath = $"{_basePath}/{relativePath}".Replace("//", "/");
                var response = await _client.Delete(remotePath);

                if (!response.IsSuccessful)
                {
                    Console.WriteLine($"[WebDAV ERROR] Delete failed: {response.StatusCode} - {response.Description}");
                }
                else
                {
                    Console.WriteLine($"[WebDAV SUCCESS] File deleted: {remotePath}");
                }

                return response.IsSuccessful;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebDAV EXCEPTION] DeleteFileAsync: {ex.Message}");
                return false;
            }
        }
    }
}
