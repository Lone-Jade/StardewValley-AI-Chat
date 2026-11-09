using System.Text.Json;

namespace StardewAI.Dialogue;

internal static class AtomicJsonFile
{
    public static void Write<T>(string path, T value, int? maximumBytes = null)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                if (maximumBytes is int limit && file.Length > limit)
                    throw new InvalidDataException("JSON file exceeds its size limit.");
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static bool IsStorageError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException;
}
