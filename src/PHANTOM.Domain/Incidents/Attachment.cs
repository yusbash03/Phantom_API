namespace PHANTOM.Domain.Incidents;

public enum AttachmentKind
{
    Image,
    Audio,
}

/// <summary>A file attached to an incident report. Immutable after creation.</summary>
public sealed class Attachment
{
    public const int MaxOriginalFileNameLength = 255;
    public const int MaxContentTypeLength = 100;
    public const int MaxStorageKeyLength = 50;
    public const int MaxProviderLength = 100;
    public const int MaxTranscriptLength = 10_000;

    private Attachment()
    {
        // EF Core
        StorageKey = string.Empty;
        ContentType = string.Empty;
        OriginalFileName = string.Empty;
        Sha256 = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid ReportId { get; private set; }
    public AttachmentKind Kind { get; private set; }

    /// <summary>Server-generated UUID used as the storage key — never the client file name.</summary>
    public string StorageKey { get; private set; }

    /// <summary>Sniffed content type (magic bytes), not the client-supplied value.</summary>
    public string ContentType { get; private set; }

    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; }

    /// <summary>Sanitised original file name, stored as metadata only.</summary>
    public string OriginalFileName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    // Audio-only: derived transcript, never touches the original file.
    public string? TranscriptText { get; private set; }
    public string? TranscriptProvider { get; private set; }
    public DateTimeOffset? TranscribedAt { get; private set; }

    public static Attachment Create(
        Guid id,
        Guid reportId,
        AttachmentKind kind,
        string storageKey,
        string contentType,
        long sizeBytes,
        string sha256,
        string originalFileName,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFileName);

        return new Attachment
        {
            Id = id,
            ReportId = reportId,
            Kind = kind,
            StorageKey = storageKey,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            OriginalFileName = originalFileName,
            CreatedAt = createdAt,
        };
    }
}
