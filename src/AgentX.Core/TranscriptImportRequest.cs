namespace AgentX.Core;

public sealed record TranscriptImportRequest(string Text, string SourceName = "clipboard");