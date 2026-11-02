namespace Haskap.LargeFileUpload;

public class LargeFileUploadSettings
{
    public const string SectionName = "LargeFileUploadSettings";

    public int ChunkSizeInBytes { get; init; }
}
