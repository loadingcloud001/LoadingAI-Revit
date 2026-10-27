namespace LoadingAI.Tools;

/// <summary>An image a tool produced for the model to look at (P1 view snapshot). The file is on local disk.</summary>
public sealed record ToolImage(string Path, string Mime, int Width, int Height);
