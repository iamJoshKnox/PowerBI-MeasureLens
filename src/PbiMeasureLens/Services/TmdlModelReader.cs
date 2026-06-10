using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PbiMeasureLens.Services;

public sealed class MeasureDef
{
    public string Name { get; init; } = "";
    public string Table { get; init; } = "";
    public string Expression { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string ModelName { get; init; } = "";

    /// <summary>TMDL lineageTag GUID — a stable object identity preserved when a model is surfaced.</summary>
    public string LineageTag { get; set; } = "";

    /// <summary>On a surfaced (DirectQuery) measure, the source object's lineageTag — links back to the real definition.</summary>
    public string SourceLineageTag { get; set; } = "";

    /// <summary>When this measure's table is DirectQuery-sourced from another model, that model's name.</summary>
    public string RemoteSourceModel { get; set; } = "";

    /// <summary>A surfaced proxy (e.g. EXTERNALMEASURE(...)) whose real DAX lives in another model.</summary>
    public bool IsExternal => Expression.TrimStart().StartsWith("EXTERNALMEASURE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A combined view of one or more local .pbip semantic models, parsed from TMDL text.
/// Measures are indexed by name so references can be resolved across chained models.
/// </summary>
public sealed class TmdlModel
{
    public Dictionary<string, List<MeasureDef>> MeasuresByName { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> ColumnNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ScannedModelFolders { get; } = new();

    public int MeasureCount => MeasuresByName.Values.Sum(l => l.Count);

    /// <summary>
    /// Resolve a measure by name. When the name is defined in more than one model and
    /// <paramref name="preferModel"/> is given, prefer the definition in that model — so a measure's
    /// child references bind to its own model first (correct for composite/chained models).
    /// Otherwise prefer the true source (a local definition) over a surfaced/remote copy.
    /// </summary>
    public MeasureDef? FindMeasure(string name, string? preferModel = null)
    {
        if (!MeasuresByName.TryGetValue(name, out var list) || list.Count == 0) return null;

        if (list.Count > 1 && !string.IsNullOrEmpty(preferModel))
        {
            var pref = list.FirstOrDefault(m => string.Equals(m.ModelName, preferModel, StringComparison.OrdinalIgnoreCase) && !m.IsExternal)
                    ?? list.FirstOrDefault(m => string.Equals(m.ModelName, preferModel, StringComparison.OrdinalIgnoreCase));
            if (pref != null) return pref;
        }
        // The real definition lives where the measure has its actual DAX — not a surfaced EXTERNALMEASURE proxy.
        return list.FirstOrDefault(m => !m.IsExternal && string.IsNullOrEmpty(m.RemoteSourceModel))
            ?? list.FirstOrDefault(m => !m.IsExternal)
            ?? list[0];
    }

    /// <summary>True only when the name maps to more than one *distinct logical* measure (a real conflict).</summary>
    public bool HasDuplicate(string name) => DefinitionCount(name) > 1;

    /// <summary>
    /// Count of distinct logical measures sharing this name. Definitions that are the same measure
    /// surfaced across models (shared lineageTag, a composite's DirectQuery source, or identical DAX)
    /// collapse to one — so composite models don't read as duplicates.
    /// </summary>
    public int DefinitionCount(string name)
        => MeasuresByName.TryGetValue(name, out var list) ? GroupLogical(list).Count : 0;

    /// <summary>Greedy single-link grouping of definitions that represent the same logical measure.</summary>
    private static List<List<MeasureDef>> GroupLogical(List<MeasureDef> list)
    {
        var groups = new List<List<MeasureDef>>();
        foreach (var def in list)
        {
            var g = groups.FirstOrDefault(grp => grp.Any(o => SameLogicalMeasure(o, def)));
            if (g != null) g.Add(def);
            else groups.Add(new List<MeasureDef> { def });
        }
        return groups;
    }

    private static bool SameLogicalMeasure(MeasureDef a, MeasureDef b)
    {
        // 0. DirectQuery source lineage: a surfaced measure records the source object's lineageTag.
        if (!string.IsNullOrEmpty(a.SourceLineageTag) &&
            string.Equals(a.SourceLineageTag, b.LineageTag, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(b.SourceLineageTag) &&
            string.Equals(b.SourceLineageTag, a.LineageTag, StringComparison.OrdinalIgnoreCase))
            return true;

        // 1. Same TMDL object identity (lineageTag preserved through surfacing).
        if (!string.IsNullOrEmpty(a.LineageTag) &&
            string.Equals(a.LineageTag, b.LineageTag, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. One is a composite surfacing the other (its table DirectQuerys that model).
        if ((!string.IsNullOrEmpty(a.RemoteSourceModel) &&
             string.Equals(a.RemoteSourceModel, b.ModelName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(b.RemoteSourceModel) &&
             string.Equals(b.RemoteSourceModel, a.ModelName, StringComparison.OrdinalIgnoreCase)))
            return true;

        // 3. Identical DAX (whitespace-insensitive) — a mirrored copy.
        var ea = NormalizeDax(a.Expression);
        return ea.Length > 0 && ea == NormalizeDax(b.Expression);
    }

    private static string NormalizeDax(string? expr)
        => Regex.Replace(expr ?? "", @"\s+", " ").Trim().ToLowerInvariant();
}

/// <summary>Scans folders for *.SemanticModel/definition/tables/*.tmdl and parses measures + columns.</summary>
public static class TmdlModelReader
{
    public static TmdlModel Load(IEnumerable<string> rootFolders)
    {
        var model = new TmdlModel();
        foreach (var root in rootFolders.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var md in FindSemanticModelDirs(root))
            {
                var tablesDir = Path.Combine(md, "definition", "tables");
                if (!Directory.Exists(tablesDir)) continue;

                model.ScannedModelFolders.Add(md);
                string modelName = ModelNameFromDir(md);
                var exprToModel = LoadExpressionSourceModels(md);
                foreach (var file in Directory.EnumerateFiles(tablesDir, "*.tmdl"))
                    ParseFile(file, model, modelName, exprToModel);
            }
        }
        return model;
    }

    private static IEnumerable<string> FindSemanticModelDirs(string root)
    {
        var found = new List<string>();

        // The root itself may already be a .SemanticModel folder.
        if (Directory.Exists(Path.Combine(root, "definition", "tables")))
            found.Add(root);

        try
        {
            found.AddRange(Directory.EnumerateDirectories(root, "*.SemanticModel", SearchOption.AllDirectories));
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        return found.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Friendly model name from a *.SemanticModel folder (drops the .SemanticModel suffix).</summary>
    private static string ModelNameFromDir(string modelDir)
    {
        var name = Path.GetFileName(modelDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        const string suffix = ".SemanticModel";
        return name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - suffix.Length)
            : name;
    }

    private static void ParseFile(string path, TmdlModel model, string modelName, Dictionary<string, string> exprToModel)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }

        string currentTable = "";
        var fileMeasures = new List<MeasureDef>();
        MeasureDef? pendingMeasure = null; // the measure whose property block we're currently inside

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimStart();
            if (line.Length == 0) continue;

            if (line.StartsWith("table ", StringComparison.Ordinal))
            {
                currentTable = Unquote(line.Substring("table ".Length).Trim());
                pendingMeasure = null;
            }
            else if (line.StartsWith("column ", StringComparison.Ordinal))
            {
                string name = ReadName(line.Substring("column ".Length), out _);
                if (!string.IsNullOrEmpty(name)) model.ColumnNames.Add(name);
                pendingMeasure = null;
            }
            else if (line.StartsWith("partition ", StringComparison.Ordinal) ||
                     line.StartsWith("hierarchy ", StringComparison.Ordinal))
            {
                pendingMeasure = null; // partitions are handled by the remote-source pass below
            }
            else if (pendingMeasure != null && line.StartsWith("sourceLineageTag", StringComparison.Ordinal))
            {
                var tag = ValueAfterColon(line);
                if (!string.IsNullOrEmpty(tag)) pendingMeasure.SourceLineageTag = tag;
            }
            else if (pendingMeasure != null && line.StartsWith("lineageTag", StringComparison.Ordinal))
            {
                var tag = ValueAfterColon(line);
                if (!string.IsNullOrEmpty(tag)) pendingMeasure.LineageTag = tag;
            }
            else if (line.StartsWith("measure ", StringComparison.Ordinal))
            {
                string rest = line.Substring("measure ".Length);
                string name = ReadName(rest, out int consumed);
                string afterName = rest.Substring(consumed).TrimStart();

                string expr = "";
                int eq = afterName.IndexOf('=');
                if (eq >= 0)
                {
                    string exprStart = afterName.Substring(eq + 1).TrimStart();
                    if (exprStart.StartsWith("```", StringComparison.Ordinal))
                    {
                        // Multi-line DAX fenced with triple backticks. Collect until the closing fence.
                        var sb = new StringBuilder();
                        string afterTicks = exprStart.Substring(3);
                        if (afterTicks.Trim().Length > 0)
                            sb.AppendLine(afterTicks);

                        for (i++; i < lines.Length; i++)
                        {
                            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                            sb.AppendLine(lines[i]);
                        }
                        expr = Dedent(sb.ToString()).Trim();
                    }
                    else
                    {
                        // Unfenced expression. Power BI inlines short ones, but multi-line measures
                        // appear as deeper-indented lines after the '=' — often led by a blank line —
                        // ending at a dedent or a known sub-property keyword.
                        var sb = new StringBuilder();
                        bool started = exprStart.Length > 0;
                        if (started) sb.AppendLine(exprStart);

                        int measureIndent = IndentWidth(lines[i]);
                        int j = i + 1;
                        for (; j < lines.Length; j++)
                        {
                            string raw = lines[j];
                            string trimmed = raw.TrimStart();
                            if (trimmed.Length == 0)
                            {
                                if (started) sb.AppendLine(); // keep internal blanks; skip leading ones
                                continue;
                            }
                            if (IndentWidth(raw) <= measureIndent) break;   // dedent to a sibling/parent
                            if (IsSubPropertyOrDecl(trimmed)) break;        // measure property or new object
                            sb.AppendLine(raw);
                            started = true;
                        }
                        i = j - 1; // resume after the lines we consumed

                        expr = Dedent(sb.ToString()).Trim();
                    }
                }

                if (!string.IsNullOrEmpty(name))
                {
                    var def = new MeasureDef
                    {
                        Name = name,
                        Table = currentTable,
                        Expression = expr,
                        SourceFile = path,
                        ModelName = modelName
                    };
                    // A surfaced EXTERNALMEASURE names its data-source expression; map it to the source model.
                    if (def.IsExternal && ExtractExternalDataSource(expr) is string ds &&
                        exprToModel.TryGetValue(ds, out var srcModel))
                        def.RemoteSourceModel = srcModel;

                    fileMeasures.Add(def);
                    pendingMeasure = def; // its lineageTag (if any) follows on indented lines
                }
            }
        }

        // Second pass: map DirectQuery tables to their remote source model, then commit measures.
        var remoteByTable = ExtractTableRemoteSources(lines, exprToModel);
        foreach (var def in fileMeasures)
        {
            if (string.IsNullOrEmpty(def.RemoteSourceModel) && remoteByTable.TryGetValue(def.Table, out var remote))
                def.RemoteSourceModel = remote;

            if (!model.MeasuresByName.TryGetValue(def.Name, out var list))
                model.MeasuresByName[def.Name] = list = new List<MeasureDef>();
            list.Add(def);
        }
    }

    /// <summary>
    /// Maps each table to the remote semantic model its DirectQuery partition sources from, if any.
    /// Tolerant of TMDL source shapes; primarily recognises an AnalysisServices.Database(..., "Model")
    /// connection or a Catalog=… connection string.
    /// </summary>
    private static Dictionary<string, string> ExtractTableRemoteSources(string[] lines, Dictionary<string, string> exprToModel)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string currentTable = "";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimStart();
            if (line.Length == 0) continue;

            if (line.StartsWith("table ", StringComparison.Ordinal))
            {
                currentTable = Unquote(line.Substring("table ".Length).Trim());
            }
            else if (line.StartsWith("partition ", StringComparison.Ordinal))
            {
                int partitionIndent = IndentWidth(lines[i]);
                bool isDirectQuery = false;
                string? remote = null;

                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    string raw = lines[j];
                    string t = raw.TrimStart();
                    if (t.Length == 0) continue;
                    if (IndentWidth(raw) <= partitionIndent) break; // left the partition block

                    if (t.StartsWith("mode:", StringComparison.Ordinal) &&
                        t.IndexOf("directQuery", StringComparison.OrdinalIgnoreCase) >= 0)
                        isDirectQuery = true;

                    // entity-style: expressionSource: 'Name' -> look up the connection's catalog.
                    if (t.StartsWith("expressionSource:", StringComparison.Ordinal))
                    {
                        var exprName = Unquote(ValueAfterColon(t)?.Trim() ?? "");
                        if (exprName.Length > 0 && exprToModel.TryGetValue(exprName, out var mdl)) remote = mdl;
                    }
                    remote ??= TryExtractRemoteModel(t); // inline AnalysisServices.Database / Catalog=
                }
                i = j - 1;

                if (isDirectQuery && remote != null && currentTable.Length > 0)
                    map[currentTable] = remote;
            }
        }
        return map;
    }

    /// <summary>Parse definition/expressions.tmdl: expression name -> remote model (catalog) it connects to.</summary>
    private static Dictionary<string, string> LoadExpressionSourceModels(string modelDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(modelDir, "definition", "expressions.tmdl");
        if (!File.Exists(file)) return map;

        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch { return map; }

        string? current = null;
        foreach (var raw in lines)
        {
            var t = raw.TrimStart();
            if (t.StartsWith("expression ", StringComparison.Ordinal))
            {
                current = ReadName(t.Substring("expression ".Length), out _);
            }
            else if (current != null)
            {
                var m = AnalysisServicesDb.Match(t);
                if (m.Success) { map[current] = m.Groups[1].Value.Trim(); current = null; }
            }
        }
        return map;
    }

    private static readonly Regex AnalysisServicesDb =
        new("AnalysisServices\\.Database\\s*\\(\\s*\"[^\"]*\"\\s*,\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex CatalogName =
        new("(?:Initial\\s+Catalog|Catalog)\\s*=\\s*\"?([^\";\\]]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuotedString = new("\"([^\"]*)\"", RegexOptions.Compiled);

    private static string? TryExtractRemoteModel(string line)
    {
        var m = AnalysisServicesDb.Match(line);
        if (m.Success) return m.Groups[1].Value.Trim();
        var c = CatalogName.Match(line);
        if (c.Success) return c.Groups[1].Value.Trim();
        return null;
    }

    /// <summary>The last quoted string in an EXTERNALMEASURE(...) call — its data-source expression name.</summary>
    private static string? ExtractExternalDataSource(string expr)
    {
        var ms = QuotedString.Matches(expr);
        return ms.Count > 0 ? ms[ms.Count - 1].Groups[1].Value : null;
    }

    private static string? ValueAfterColon(string line)
    {
        int c = line.IndexOf(':');
        return c >= 0 ? line.Substring(c + 1).Trim() : null;
    }

    /// <summary>Reads a possibly single-quoted TMDL identifier from the start of <paramref name="s"/>.</summary>
    private static string ReadName(string s, out int consumed)
    {
        s ??= "";
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;

        if (i < s.Length && s[i] == '\'')
        {
            var sb = new StringBuilder();
            int j = i + 1;
            while (j < s.Length)
            {
                if (s[j] == '\'')
                {
                    if (j + 1 < s.Length && s[j + 1] == '\'') { sb.Append('\''); j += 2; continue; } // escaped ''
                    break;
                }
                sb.Append(s[j]);
                j++;
            }
            consumed = Math.Min(j + 1, s.Length); // past the closing quote
            return sb.ToString();
        }
        else
        {
            int j = i;
            while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != '=') j++;
            consumed = j;
            return s.Substring(i, j - i);
        }
    }

    private static string Unquote(string s)
        => s.Length >= 2 && s[0] == '\'' && s[^1] == '\'' ? s.Substring(1, s.Length - 2).Replace("''", "'") : s;

    /// <summary>Leading whitespace width (tabs and spaces counted equally) used for indent comparison.</summary>
    private static int IndentWidth(string line)
    {
        int c = 0;
        while (c < line.Length && (line[c] == ' ' || line[c] == '\t')) c++;
        return c;
    }

    // TMDL measure sub-properties and object declarations that mark the end of an unfenced expression.
    private static readonly string[] StopTokens =
    {
        "formatString", "formatStringDefinition", "lineageTag", "sourceLineageTag", "displayFolder",
        "description", "isHidden", "dataType", "annotation", "changedProperty", "extendedProperty",
        "detailRowsDefinition", "kpi", "dataCategory", "relatedColumnDetails", "calculationItem",
        "measure ", "column ", "table ", "partition ", "hierarchy ", "relationship", "variation",
    };

    private static bool IsSubPropertyOrDecl(string trimmedLine)
    {
        foreach (var token in StopTokens)
            if (trimmedLine.StartsWith(token, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Removes the common leading-whitespace prefix from a multi-line block.</summary>
    private static string Dedent(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int min = int.MaxValue;
        foreach (var l in lines)
        {
            if (l.Trim().Length == 0) continue;
            int c = 0;
            while (c < l.Length && (l[c] == ' ' || l[c] == '\t')) c++;
            min = Math.Min(min, c);
        }
        if (min is int.MaxValue or 0) return text;

        var sb = new StringBuilder();
        foreach (var l in lines)
            sb.AppendLine(l.Length >= min ? l.Substring(min) : l);
        return sb.ToString();
    }
}
