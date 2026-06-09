using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace PbiMeasureLens.Services;

/// <summary>
/// Entry point for reading a report's visuals regardless of format. Auto-detects classic
/// (.pbix with Report/Layout) vs. PBIR ("enhanced" definition folder — on disk as a .pbip/.Report
/// folder, or embedded inside a newer .pbix) and dispatches to the matching reader.
/// </summary>
public static class ReportReader
{
    public static PbixReadResult ReadVisuals(string path)
    {
        if (Directory.Exists(path))
            return ReadFolder(path);

        if (!File.Exists(path))
            throw new PbixReadException($"File not found:\n{path}");

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pbix" => ReadPbix(path),
            ".pbip" => ReadPbip(path),
            ".pbir" => ReadFolder(Path.GetDirectoryName(path) ?? path),
            _ => ReadPbix(path) // best effort: assume zip
        };
    }

    private static PbixReadResult ReadPbix(string path)
    {
        bool hasLayout;
        string? defPrefix = null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            hasLayout = zip.GetEntry("Report/Layout") != null;
            if (!hasLayout)
                defPrefix = FindDefinitionPrefix(zip);
        }
        catch (InvalidDataException ex)
        {
            throw new PbixReadException("This file is not a valid .pbix (zip) archive.", ex);
        }

        if (hasLayout)
            return PbixLayoutReader.ReadVisuals(path); // classic singleVisual layout

        if (defPrefix != null)
        {
            using var zip = ZipFile.OpenRead(path);
            return PbirReportReader.ReadFromZip(zip, defPrefix);
        }

        throw new PbixReadException(
            "This .pbix has neither a classic Report/Layout nor a PBIR definition folder.\n\n" +
            "It may be a thin/Service-only file.");
    }

    /// <summary>Locate the report's "…/definition/" prefix in a PBIR-embedded .pbix (ignores the model's).</summary>
    private static string? FindDefinitionPrefix(ZipArchive zip)
    {
        const string marker = "definition/pages/";
        foreach (var e in zip.Entries)
        {
            string full = e.FullName.Replace('\\', '/');
            int i = full.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
                return full.Substring(0, i + "definition/".Length);
        }
        return null;
    }

    private static PbixReadResult ReadPbip(string pbipPath)
    {
        string dir = Path.GetDirectoryName(pbipPath) ?? ".";
        string? reportRel = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pbipPath));
            if (doc.RootElement.TryGetProperty("artifacts", out var artifacts) &&
                artifacts.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in artifacts.EnumerateArray())
                {
                    if (a.TryGetProperty("report", out var report) &&
                        report.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                    {
                        reportRel = p.GetString();
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new PbixReadException("Could not read the .pbip project file.", ex);
        }

        if (string.IsNullOrEmpty(reportRel))
            throw new PbixReadException("This .pbip does not reference a report artifact.");

        string reportFolder = Path.GetFullPath(Path.Combine(dir, reportRel));
        return ReadFolder(reportFolder);
    }

    /// <summary>Accepts a *.Report folder (contains definition/) or a definition/ folder directly.</summary>
    private static PbixReadResult ReadFolder(string folder)
    {
        string? definition =
            Directory.Exists(Path.Combine(folder, "definition", "pages")) ? Path.Combine(folder, "definition") :
            Directory.Exists(Path.Combine(folder, "pages")) ? folder :
            null;

        if (definition == null)
            throw new PbixReadException(
                "No PBIR report definition was found here.\n\n" +
                "Point at a .pbip file, a .Report folder, or a folder containing definition\\pages.");

        return PbirReportReader.ReadFromDefinitionFolder(definition);
    }
}
