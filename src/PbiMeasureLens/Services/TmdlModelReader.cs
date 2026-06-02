using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PbiMeasureLens.Services;

public sealed class MeasureDef
{
    public string Name { get; init; } = "";
    public string Table { get; init; } = "";
    public string Expression { get; init; } = "";
    public string SourceFile { get; init; } = "";
    public string ModelName { get; init; } = "";
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

    public MeasureDef? FindMeasure(string name)
        => MeasuresByName.TryGetValue(name, out var list) && list.Count > 0 ? list[0] : null;

    public bool HasDuplicate(string name)
        => MeasuresByName.TryGetValue(name, out var list) && list.Count > 1;
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
                foreach (var file in Directory.EnumerateFiles(tablesDir, "*.tmdl"))
                    ParseFile(file, model, modelName);
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

    private static void ParseFile(string path, TmdlModel model, string modelName)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }

        string currentTable = "";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimStart();
            if (line.Length == 0) continue;

            if (line.StartsWith("table ", StringComparison.Ordinal))
            {
                currentTable = Unquote(line.Substring("table ".Length).Trim());
            }
            else if (line.StartsWith("column ", StringComparison.Ordinal))
            {
                string name = ReadName(line.Substring("column ".Length), out _);
                if (!string.IsNullOrEmpty(name)) model.ColumnNames.Add(name);
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
                        expr = exprStart.Trim();
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
                    if (!model.MeasuresByName.TryGetValue(name, out var list))
                        model.MeasuresByName[name] = list = new List<MeasureDef>();
                    list.Add(def);
                }
            }
        }
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
