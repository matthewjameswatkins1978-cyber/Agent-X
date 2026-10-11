namespace AgentX.Core;

public sealed record TranscriptImportResult(
    TranscriptDocument Document,
    int LineCount,
    int Utf8ByteCount,
    string SourceName);