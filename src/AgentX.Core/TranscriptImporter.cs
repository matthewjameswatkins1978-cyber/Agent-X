namespace AgentX.Core;

/// <summary>Canonical import operation used by the UI and future typed Agent X clients.</summary>
public static class TranscriptImporter
{
    public static TranscriptImportResult Import(TranscriptImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Text);

        var document = TranscriptDocument.FromText(request.Text);
        return new TranscriptImportResult(
            document,
            document.LineCount,
            document.Utf8ByteCount,
            request.SourceName);
    }
}