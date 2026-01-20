namespace DmsProjeckt.Data
{
    public class ChunkManifest
    {
        public Guid DokumentId { get; set; }
        public Guid? OriginalId { get; set; }
        public List<ChunkInfo> Chunks { get; set; } = new();
    }

    public class ChunkInfo
    {
        public int Index { get; set; }
        public string File { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }
}
