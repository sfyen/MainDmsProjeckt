using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DmsProjeckt.Data;

namespace DmsProjeckt.Service
{
    public class ChunkedWebDavStream : Stream
    {
        private readonly WebDavStorageService _webDav;
        private readonly List<DokumentChunk> _chunks;
        private readonly long _totalLength;
        private long _position;

        public ChunkedWebDavStream(WebDavStorageService webDav, List<DokumentChunk> chunks)
        {
            _webDav = webDav;
            _chunks = chunks.OrderBy(c => c.Index).ToList();
            _totalLength = _chunks.Sum(c => (long)c.Size);
            _position = 0;
            _buffer = new byte[BUFFER_SIZE];
        }

        private const int BUFFER_SIZE = 2 * 1024 * 1024; // 2MB Read-Ahead Buffer
        private byte[] _buffer;
        private long _bufferStart = -1;
        private int _bufferLength = 0;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _totalLength;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _totalLength)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_position >= _totalLength)
                return 0;

            if (_position + count > _totalLength)
                count = (int)(_totalLength - _position);

            int totalBytesRead = 0;

            while (count > 0)
            {
                // 1. Check if data is in buffer
                if (_bufferStart != -1 && _position >= _bufferStart && _position < _bufferStart + _bufferLength)
                {
                    long offsetInBuffer = _position - _bufferStart;
                    int availableInBuffer = _bufferLength - (int)offsetInBuffer;
                    int toCopy = Math.Min(count, availableInBuffer);

                    Array.Copy(_buffer, offsetInBuffer, buffer, offset, toCopy);

                    _position += toCopy;
                    offset += toCopy;
                    count -= toCopy;
                    totalBytesRead += toCopy;
                    continue; // Continue loop to see if we need more data
                }

                // 2. Data not in buffer - Fill Buffer from WebDAV
                // Calculate which chunk(s) we need to fetch from
                // We will fetch BUFFER_SIZE bytes (or less if EOF/Chunk boundary) starting from _position
                    
                long fetchStart = _position;
                long fetchEnd = Math.Min(_totalLength, fetchStart + BUFFER_SIZE);
                int bytesToFetch = (int)(fetchEnd - fetchStart);

                // We need to fetch 'bytesToFetch' from the underlying chunks
                // NOTE: For simplicity, we fetch from the CURRENT chunk only to avoid complex multi-chunk buffer filling in one go.
                // If the read spans chunks, the loop will handle the next chunk in the next iteration.
                
                var chunk = GetChunkForPosition(fetchStart);
                if (chunk == null) break; // Should not happen

                // Adjust fetch size to not cross chunk boundary for this specific fetch op
                long chunkStartGlobal = GetChunkStart(chunk);
                long chunkEndGlobal = chunkStartGlobal + chunk.Size;
                
                if (fetchEnd > chunkEndGlobal)
                {
                    fetchEnd = chunkEndGlobal;
                    bytesToFetch = (int)(fetchEnd - fetchStart);
                }

                // Execute Fetch
                int fetched = await FetchFromWebDavAsync(chunk, fetchStart - chunkStartGlobal, bytesToFetch, cancellationToken);
                if (fetched == 0) break; // EOF or Error

                _bufferStart = fetchStart;
                _bufferLength = fetched;
            }

            return totalBytesRead;
        }

        private DokumentChunk? GetChunkForPosition(long position)
        {
            long currentPos = 0;
            foreach (var chunk in _chunks)
            {
                if (position >= currentPos && position < currentPos + chunk.Size)
                    return chunk;
                currentPos += chunk.Size;
            }
            return null;
        }
        
        private long GetChunkStart(DokumentChunk chunk)
        {
            // Optimize: Could cache this map in constructor
             long currentPos = 0;
            foreach (var c in _chunks)
            {
                if (c == chunk) return currentPos;
                currentPos += c.Size;
            }
            return 0;
        }

        private async Task<int> FetchFromWebDavAsync(DokumentChunk chunk, long startInChunk, int count, CancellationToken cancellationToken)
        {
            string chunkUrl = chunk.FirebasePath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? chunk.FirebasePath
                : $"{_webDav.BaseUrl.TrimEnd('/')}/{chunk.FirebasePath.TrimStart('/')}";

            string rangeHeader = $"bytes={startInChunk}-{startInChunk + count - 1}";
            
            // Retry Logic
            int maxRetries = 5;
            int retryDelay = 1000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var streamResult = await _webDav.DownloadStreamWithRangeAsync(chunkUrl, rangeHeader);

                    if (streamResult != null && streamResult.Stream != null)
                    {
                        using (streamResult.Stream)
                        {
                            // Read fully into _buffer
                            int totalRead = 0;
                            while (totalRead < count)
                            {
                                int read = await streamResult.Stream.ReadAsync(_buffer, totalRead, count - totalRead, cancellationToken);
                                if (read == 0) break;
                                totalRead += read;
                            }
                            return totalRead;
                        }
                    }
                    else
                    {
                         if (attempt == maxRetries) throw new IOException($"Null response from WebDAV for {chunkUrl}");
                         await Task.Delay(retryDelay, cancellationToken); 
                         retryDelay *= 2;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ [ChunkBuffered] Retry {attempt} for {chunkUrl}: {ex.Message}");
                     if (attempt == maxRetries) throw;
                     await Task.Delay(retryDelay, cancellationToken);
                     retryDelay *= 2;
                }
            }
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    Position = offset;
                    break;
                case SeekOrigin.Current:
                    Position += offset;
                    break;
                case SeekOrigin.End:
                    Position = _totalLength + offset;
                    break;
            }
            return Position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
