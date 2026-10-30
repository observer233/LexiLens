using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace LexiLens.App.Platform;

// Windows.Media.Ocr is documented only for desktop apps with package identity.
// A successful call from an unpackaged EXE does not establish portable support.
internal static class WindowsOcrSupport
{
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);

    internal static bool HasPackageIdentity { get; } = CheckPackageIdentity();

    private static bool CheckPackageIdentity()
    {
        try
        {
            uint length = 0;
            return GetCurrentPackageFullName(ref length, 0) == ErrorInsufficientBuffer;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static OcrEngine? TryCreate(string language, out string reason, bool allowUnsupported = false)
    {
        if (!HasPackageIdentity && !allowUnsupported)
        {
            reason = "package_identity_required";
            return null;
        }
        try
        {
            var engine = OcrEngine.TryCreateFromLanguage(new Language(language));
            reason = engine == null ? "language_unavailable" : !HasPackageIdentity ? "unsupported_package_identity_opt_in" : "";
            return engine;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "activation_failed";
            return null;
        }
    }

    internal static IReadOnlyList<Language> Languages(bool allowUnsupported = false)
    {
        if (!HasPackageIdentity && !allowUnsupported) return [];
        try { return OcrEngine.AvailableRecognizerLanguages.ToArray(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
    }
}
