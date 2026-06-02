using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using PbiMeasureLens.Models;

namespace PbiMeasureLens.Services;

public sealed class PbixReadException : Exception
{
    public PbixReadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Reads the classic .pbix report layout (Report/Layout — UTF-16 JSON with nested,
/// escaped-JSON config strings) and extracts each visual's field rename mappings.
/// </summary>
public static class PbixLayoutReader
{
    public static List<VisualInfo> ReadVisuals(string pbixPath)
    {
        if (!File.Exists(pbixPath))
            throw new PbixReadException($"File not found:\n{pbixPath}");

        string layoutJson;
        try
        {
            using var zip = ZipFile.OpenRead(pbixPath);
            var entry = zip.GetEntry("Report/Layout")
                ?? throw new PbixReadException(
                    "This .pbix has no Report/Layout entry.\n\n" +
                    "It may be a thin/Service-only file, or a .pbip report (folder format), " +
                    "which isn't supported yet.");
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            layoutJson = DecodeLayout(ms.ToArray());
        }
        catch (InvalidDataException ex)
        {
            throw new PbixReadException("This file is not a valid .pbix (zip) archive.", ex);
        }

        var visuals = new List<VisualInfo>();
        using var doc = JsonDocument.Parse(layoutJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            return visuals;

        foreach (var section in sections.EnumerateArray())
        {
            string page = GetString(section, "displayName") ?? GetString(section, "name") ?? "(unnamed page)";
            int ordinal = section.TryGetProperty("ordinal", out var ord) && ord.TryGetInt32(out var o) ? o : 0;

            if (!section.TryGetProperty("visualContainers", out var containers) ||
                containers.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var vc in containers.EnumerateArray())
            {
                var configStr = GetString(vc, "config");
                if (string.IsNullOrEmpty(configStr)) continue;

                try
                {
                    var vi = ParseVisual(configStr, page, ordinal);
                    if (vi != null) visuals.Add(vi);
                }
                catch (JsonException)
                {
                    // skip a malformed visual rather than failing the whole report
                }
            }
        }

        return visuals.OrderBy(v => v.PageOrdinal).ToList();
    }

    private static VisualInfo? ParseVisual(string configJson, string page, int ordinal)
    {
        using var doc = JsonDocument.Parse(configJson);
        var config = doc.RootElement;
        string visualId = GetString(config, "name") ?? "";

        if (!config.TryGetProperty("singleVisual", out var sv) || sv.ValueKind != JsonValueKind.Object)
            return null; // group container / non-visual

        var vi = new VisualInfo
        {
            Page = page,
            PageOrdinal = ordinal,
            VisualId = visualId,
            VisualType = GetString(sv, "visualType") ?? "(unknown)",
            Title = TryGetVisualTitle(sv) ?? ""
        };

        if (!sv.TryGetProperty("prototypeQuery", out var pq) || pq.ValueKind != JsonValueKind.Object)
            return vi; // visual with no data (textbox, shape, image)

        // alias -> table entity
        var aliasToEntity = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pq.TryGetProperty("From", out var from) && from.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in from.EnumerateArray())
            {
                var alias = GetString(f, "Name");
                var entity = GetString(f, "Entity");
                if (alias != null && entity != null) aliasToEntity[alias] = entity;
            }
        }

        JsonElement? columnProps =
            sv.TryGetProperty("columnProperties", out var cp) && cp.ValueKind == JsonValueKind.Object ? cp : null;

        if (pq.TryGetProperty("Select", out var select) && select.ValueKind == JsonValueKind.Array)
        {
            foreach (var sel in select.EnumerateArray())
            {
                var fm = ParseSelect(sel, aliasToEntity, columnProps);
                if (fm != null) vi.Fields.Add(fm);
            }
        }

        return vi;
    }

    private static FieldMapping? ParseSelect(JsonElement sel, Dictionary<string, string> aliasToEntity, JsonElement? columnProps)
    {
        FieldKind kind;
        JsonElement def;
        if (sel.TryGetProperty("Measure", out def)) kind = FieldKind.Measure;
        else if (sel.TryGetProperty("Column", out def)) kind = FieldKind.Column;
        else return null; // aggregation / hierarchy level / etc. — out of scope for v1

        string original = GetString(def, "Property") ?? "(unknown)";
        string table = ResolveTable(def, aliasToEntity);
        string queryRef = GetString(sel, "Name") ?? $"{table}.{original}";
        string native = GetString(sel, "NativeReferenceName") ?? original;

        string display = native;
        bool renamedByProps = false;
        if (columnProps is JsonElement props && props.TryGetProperty(queryRef, out var entry))
        {
            var dn = GetString(entry, "displayName");
            if (!string.IsNullOrEmpty(dn))
            {
                display = dn;
                renamedByProps = true;
            }
        }

        bool isRenamed = renamedByProps || !string.Equals(display, original, StringComparison.Ordinal);

        return new FieldMapping
        {
            Kind = kind,
            Table = table,
            OriginalName = original,
            DisplayName = display,
            IsRenamed = isRenamed
        };
    }

    private static string ResolveTable(JsonElement def, Dictionary<string, string> aliasToEntity)
    {
        if (def.TryGetProperty("Expression", out var expr) && expr.TryGetProperty("SourceRef", out var srcRef))
        {
            var src = GetString(srcRef, "Source");
            if (src != null && aliasToEntity.TryGetValue(src, out var ent)) return ent;
            var entity = GetString(srcRef, "Entity");
            if (entity != null) return entity;
        }
        return "";
    }

    private static string? TryGetVisualTitle(JsonElement sv)
    {
        // sv.objects.title[0].properties.text.expr.Literal.Value  (literal includes surrounding quotes)
        if (sv.TryGetProperty("objects", out var objs) &&
            objs.TryGetProperty("title", out var titleArr) && titleArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in titleArr.EnumerateArray())
            {
                if (t.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("text", out var text) &&
                    text.TryGetProperty("expr", out var ex) &&
                    ex.TryGetProperty("Literal", out var lit))
                {
                    var v = GetString(lit, "Value");
                    if (!string.IsNullOrEmpty(v)) return v.Trim('\'');
                }
            }
        }
        return null;
    }

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    /// <summary>Report/Layout is typically UTF-16 LE (sometimes with a BOM). Detect and decode.</summary>
    private static string DecodeLayout(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        // No BOM: a JSON document starting with '{' in UTF-16 LE has a 0x00 at byte[1].
        if (bytes.Length >= 2 && bytes[0] != 0 && bytes[1] == 0)
            return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }
}
