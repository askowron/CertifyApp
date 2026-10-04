namespace Certify.Core.Utils;

public static class FileLogger
{
    public static void Append(string dataDirectory, string message)
    {
        try
        {
            var dir = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"log-{DateTime.Now:yyyy-MM-dd}.txt");
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
