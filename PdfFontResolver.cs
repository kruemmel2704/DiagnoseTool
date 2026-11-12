using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PdfSharp.Fonts;

namespace DiagnoseTool;

/// <summary>
/// Liefert PdfSharp die eingebettete Schrift "Carlito" (SIL OFL 1.1, metrisch kompatibel zu Calibri).
/// PdfSharp 6 bringt selbst keine Schriften mit; ohne Resolver schlägt jede PDF-Erzeugung mit
/// "No appropriate font found for family name 'Segoe UI'" fehl (Docker/Linux/macOS).
/// Alle angefragten Schriftfamilien (z.B. "Segoe UI") werden auf Carlito abgebildet.
/// </summary>
public sealed class PdfFontResolver : IFontResolver
{
    private const string Family = "Carlito";
    private static readonly object InitLock = new();
    private static bool _registered;

    private static readonly Dictionary<string, string> FaceToResource = new(StringComparer.Ordinal)
    {
        ["Carlito#R"] = "Carlito-Regular.ttf",
        ["Carlito#B"] = "Carlito-Bold.ttf",
        ["Carlito#I"] = "Carlito-Italic.ttf",
        ["Carlito#BI"] = "Carlito-BoldItalic.ttf"
    };

    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>Einmalig vor der ersten PDF-Erzeugung aufrufen (mehrfacher Aufruf ist unkritisch).</summary>
    public static void EnsureRegistered()
    {
        lock (InitLock)
        {
            if (_registered) return;
            if (GlobalFontSettings.FontResolver == null)
            {
                GlobalFontSettings.FontResolver = new PdfFontResolver();
            }
            _registered = true;
        }
    }

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        string face = isBold && isItalic ? "BI" : isBold ? "B" : isItalic ? "I" : "R";
        return new FontResolverInfo($"{Family}#{face}");
    }

    public byte[] GetFont(string faceName)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(faceName, out var cached)) return cached;
            if (!FaceToResource.TryGetValue(faceName, out var file)) file = FaceToResource["Carlito#R"];

            var asm = typeof(PdfFontResolver).Assembly;
            string resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
            if (resource == null) return null;

            using var stream = asm.GetManifestResourceStream(resource)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            Cache[faceName] = bytes;
            return bytes;
        }
    }
}
