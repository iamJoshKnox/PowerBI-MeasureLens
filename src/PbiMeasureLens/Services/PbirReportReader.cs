using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using PbiMeasureLens.Models;

namespace PbiMeasureLens.Services;

/// <summary>
/// Reads the modern PBIR ("enhanced report format") report definition — a folder of JSON files
/// (one visual.json per visual) — and extracts each visual's field rename mappings.
/// Works from a folder on disk or from the Report/definition entries inside a .pbix zip.
/// </summary>
public static class PbirReportReader
{
    /// <summary>Read a PBIR <c>definition</c> folder (the one that directly contains <c>pages/</c>).</summary>
    public static PbixReadResult ReadFromDefinitionFolder(string definitionFolder)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(definitionFolder, "*.json", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(definitionFolder, f).Replace('\\', '/');
            try { files[rel] = File.ReadAllText(f); }
            catch { /* skip unreadable file */ }
        }
        return Build(files);
    }

    /// <summary>Read PBIR JSON entries from an open .pbix zip, relative to the report's definition/ prefix.</summary>
    public static PbixReadResult ReadFromZip(ZipArchive zip, string definitionPrefix)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries)
        {
            string full = e.FullName.Replace('\\', '/');
            if (!full.StartsWith(definitionPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!full.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            using var s = e.Open();
            using var sr = new StreamReader(s);
            files[full.Substring(definitionPrefix.Length)] = sr.ReadToEnd();
        }
        return Build(files);
    }

    // ---- Shared logic over an in-memory map of "<path under definition/>" -> file text ----

    private static PbixReadResult Build(Dictionary<string, string> files)
    {
        var visuals = new List<VisualInfo>();
        int dataVisuals = 0, skipped = 0;

        foreach (var (pageFolder, ordinal) in OrderedPages(files))
        {
            string displayName = pageFolder;
            if (files.TryGetValue($"pages/{pageFolder}/page.json", out var pageJson))
            {
                try
                {
                    using var pd = JsonDocument.Parse(pageJson);
                    displayName = GetString(pd.RootElement, "displayName")
                                  ?? GetString(pd.RootElement, "name") ?? pageFolder;
                }
                catch (JsonException) { /* keep folder name */ }
            }

            string visualPrefix = $"pages/{pageFolder}/visuals/";
            foreach (var kv in files)
            {
                if (!kv.Key.StartsWith(visualPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!kv.Key.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var vi = ParseVisual(kv.Value, displayName, ordinal);
                    if (vi == null) continue;
                    visuals.Add(vi);
                    if (vi.Fields.Count > 0) dataVisuals++;
                }
                catch (JsonException)
                {
                    skipped++; // skip a malformed visual rather than failing the whole report
                }
            }
        }

        return new PbixReadResult
        {
            Visuals = visuals.OrderBy(v => v.PageOrdinal).ToList(),
            DataVisuals = dataVisuals,
            SkippedMalformed = skipped,
            UnknownSchema = 0,
            Warning = skipped > 0
                ? $"{skipped} visual(s) had unreadable JSON and were skipped; the field list may be incomplete."
                : null
        };
    }

    /// <summary>Page folders in report order: pages.json's pageOrder first, then any stragglers.</summary>
    private static IEnumerable<(string Page, int Ordinal)> OrderedPages(Dictionary<string, string> files)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in files.Keys)
        {
            const string prefix = "pages/";
            const string suffix = "/page.json";
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string mid = key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length);
                if (!mid.Contains('/')) present.Add(mid);
            }
        }

        var order = new List<string>();
        if (files.TryGetValue("pages/pages.json", out var pagesJson))
        {
            try
            {
                using var d = JsonDocument.Parse(pagesJson);
                if (d.RootElement.TryGetProperty("pageOrder", out var po) && po.ValueKind == JsonValueKind.Array)
                    foreach (var p in po.EnumerateArray())
                        if (p.GetString() is string s && present.Contains(s) && !order.Contains(s))
                            order.Add(s);
            }
            catch (JsonException) { /* fall back to discovery order */ }
        }
        foreach (var p in present)
            if (!order.Contains(p))
                order.Add(p);

        for (int i = 0; i < order.Count; i++)
            yield return (order[i], i);
    }

    private static VisualInfo? ParseVisual(string visualJson, string page, int ordinal)
    {
        using var doc = JsonDocument.Parse(visualJson);
        var root = doc.RootElement;
        string visualId = GetString(root, "name") ?? "";

        // Group containers carry "visualGroup"; only data containers have "visual".
        if (!root.TryGetProperty("visual", out var visual) || visual.ValueKind != JsonValueKind.Object)
            return null;

        var vi = new VisualInfo
        {
            Page = page,
            PageOrdinal = ordinal,
            VisualId = visualId,
            VisualType = GetString(visual, "visualType") ?? "(unknown)",
            Title = TryGetVisualTitle(visual) ?? ""
        };

        if (!visual.TryGetProperty("query", out var query) || query.ValueKind != JsonValueKind.Object)
            return vi; // visual with no data (textbox, shape, image)

        // Optional alias -> table entity map (PBIR usually inlines Entity, but handle SourceRef.Source too).
        var aliasToEntity = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.TryGetProperty("From", out var from) && from.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in from.EnumerateArray())
            {
                var alias = GetString(f, "Name");
                var entity = GetString(f, "Entity");
                if (alias != null && entity != null) aliasToEntity[alias] = entity;
            }
        }

        if (query.TryGetProperty("queryState", out var queryState) && queryState.ValueKind == JsonValueKind.Object)
        {
            foreach (var role in queryState.EnumerateObject())
            {
                if (role.Value.ValueKind != JsonValueKind.Object) continue;
                if (!role.Value.TryGetProperty("projections", out var projections) ||
                    projections.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var proj in projections.EnumerateArray())
                {
                    var fm = ParseProjection(proj, aliasToEntity);
                    if (fm == null) continue;
                    fm.Page = page;
                    fm.VisualType = vi.VisualType;
                    fm.VisualId = vi.VisualId;
                    fm.VisualTitle = vi.Title;
                    vi.Fields.Add(fm);
                }
            }
        }

        return vi;
    }

    private static FieldMapping? ParseProjection(JsonElement proj, Dictionary<string, string> aliasToEntity)
    {
        if (!proj.TryGetProperty("field", out var field) || field.ValueKind != JsonValueKind.Object)
            return null;

        FieldKind kind;
        JsonElement def;
        if (field.TryGetProperty("Measure", out def)) kind = FieldKind.Measure;
        else if (field.TryGetProperty("Column", out def)) kind = FieldKind.Column;
        else return null; // aggregation / hierarchy level / etc. — out of scope

        string native = GetString(proj, "nativeQueryRef") ?? "";
        string original = GetString(def, "Property") ?? (native.Length > 0 ? native : "(unknown)");
        string table = ResolveTable(def, aliasToEntity);
        if (native.Length == 0) native = original;

        string? displayName = GetString(proj, "displayName");
        bool isRenamed = !string.IsNullOrEmpty(displayName) &&
                         !string.Equals(displayName, native, StringComparison.Ordinal);
        string display = string.IsNullOrEmpty(displayName) ? native : displayName;

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
            var entity = GetString(srcRef, "Entity");
            if (entity != null) return entity;
            var src = GetString(srcRef, "Source");
            if (src != null && aliasToEntity.TryGetValue(src, out var ent)) return ent;
        }
        return "";
    }

    private static string? TryGetVisualTitle(JsonElement visual)
    {
        // PBIR title: visual.visualContainerObjects.title[0].properties.text.expr.Literal.Value
        foreach (var bag in new[] { "visualContainerObjects", "objects" })
        {
            if (visual.TryGetProperty(bag, out var objs) &&
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
        }
        return null;
    }

    private static string? GetString(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
