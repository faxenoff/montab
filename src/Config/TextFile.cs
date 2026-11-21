using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Montab.Config;

/// <summary>
/// Небольшой текстовый файл UTF-8 целиком, напрямую через Win32: FileStream,
/// StreamReader и таблицы кодировок из BCL ради одного файла настроек не нужны.
/// </summary>
internal static unsafe class TextFile
{
    // Файл настроек — сотни байт; всё, что больше, заведомо не наш файл
    const long MaxBytes = 1 << 20;

    /// <summary>Текст файла; null — файла нет или он не читается.</summary>
    public static char[]? Read(string path)
    {
        HANDLE file;
        fixed (char* p = path)
        {
            file = PInvoke.CreateFile(
                p, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_READ, FILE_SHARE_MODE.FILE_SHARE_READ, null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, default);
        }
        if (file == HANDLE.INVALID_HANDLE_VALUE)
            return null;

        byte[]? bytes = null;
        long size;
        if (PInvoke.GetFileSizeEx(file, &size) && size <= MaxBytes)
        {
            bytes = new byte[size];
            uint read = 0;
            fixed (byte* b = bytes)
            {
                if (!PInvoke.ReadFile(file, b, (uint)size, &read, null) || read != size)
                    bytes = null;
            }
        }
        PInvoke.CloseHandle(file);
        if (bytes is null)
            return null;

        // Метка UTF-8 (BOM) в текст не входит
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        int byteCount = bytes.Length - skip;
        if (byteCount == 0)
            return [];

        fixed (byte* b = &bytes[skip])
        {
            int charCount = PInvoke.MultiByteToWideChar(PInvoke.CP_UTF8, 0, (PCSTR)b, byteCount, null, 0);
            var chars = new char[charCount];
            fixed (char* c = chars)
                PInvoke.MultiByteToWideChar(PInvoke.CP_UTF8, 0, (PCSTR)b, byteCount, c, charCount);
            return chars;
        }
    }

    /// <summary>Перезаписывает файл текстом (UTF-8 без BOM); false — записать не удалось.</summary>
    public static bool Write(string path, string text)
    {
        byte[] bytes;
        fixed (char* c = text)
        {
            int byteCount = PInvoke.WideCharToMultiByte(PInvoke.CP_UTF8, 0, (PCWSTR)c, text.Length, default, 0, default, null);
            bytes = new byte[byteCount];
            fixed (byte* b = bytes)
                PInvoke.WideCharToMultiByte(PInvoke.CP_UTF8, 0, (PCWSTR)c, text.Length, (PSTR)b, byteCount, default, null);
        }

        HANDLE file;
        fixed (char* p = path)
        {
            file = PInvoke.CreateFile(
                p, (uint)GENERIC_ACCESS_RIGHTS.GENERIC_WRITE, FILE_SHARE_MODE.FILE_SHARE_NONE, null,
                FILE_CREATION_DISPOSITION.CREATE_ALWAYS, FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, default);
        }
        if (file == HANDLE.INVALID_HANDLE_VALUE)
            return false;

        uint written = 0;
        bool ok;
        fixed (byte* b = bytes)
            ok = PInvoke.WriteFile(file, b, (uint)bytes.Length, &written, null) && written == bytes.Length;
        PInvoke.CloseHandle(file);
        return ok;
    }
}
