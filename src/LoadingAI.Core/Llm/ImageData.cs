using LoadingAI.Tools;

namespace LoadingAI.Llm;

/// <summary>Turns a tool image file into a data URL attachment. Null when missing or over the attachment cap.</summary>
public static class ImageData
{
    public const int MaxBytes = 4 * 1024 * 1024;

    public static AttachmentDto? ToAttachment(ToolImage img)
    {
        try
        {
            var info = new FileInfo(img.Path);
            if (!info.Exists || info.Length > MaxBytes) return null;
            var b64 = Convert.ToBase64String(File.ReadAllBytes(img.Path));
            return new AttachmentDto(Guid.NewGuid().ToString("N").Substring(0, 8), info.Name, img.Mime, $"data:{img.Mime};base64,{b64}");
        }
        catch { return null; }
    }
}
