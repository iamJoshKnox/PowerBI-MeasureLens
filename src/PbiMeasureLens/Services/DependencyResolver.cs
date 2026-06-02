using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using PbiMeasureLens.Models;

namespace PbiMeasureLens.Services;

/// <summary>
/// Builds a recursive measure dependency tree by parsing DAX expression text.
/// A reference like 'Table'[X] or Table[X] is treated as a column; a bare [X] is
/// classified as a measure (if known), a column (if known), or unresolved/external.
/// </summary>
public static class DependencyResolver
{
    // Optional table qualifier ('Quoted' or Unquoted), then [Name].
    private static readonly Regex RefRegex = new(
        @"(?:'(?<t1>[^']+)'|(?<t2>[A-Za-z_][A-Za-z0-9_]*))?\s*\[(?<name>[^\]]+)\]",
        RegexOptions.Compiled);

    public static MeasureNode Build(string measureName, TmdlModel model)
    {
        var root = model.FindMeasure(measureName);
        if (root == null)
            return new MeasureNode { Name = measureName, Kind = DependencyKind.Unresolved };

        var node = new MeasureNode
        {
            Name = root.Name,
            Table = root.Table,
            Expression = root.Expression,
            SourceModel = root.ModelName,
            Kind = DependencyKind.Measure
        };
        BuildChildren(node, root, model, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.Name });
        return node;
    }

    private static void BuildChildren(MeasureNode node, MeasureDef def, TmdlModel model, HashSet<string> path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ExtractRefs(def.Expression))
        {
            if (!seen.Add(r.Key)) continue;

            if (r.HasTable)
            {
                node.Children.Add(new MeasureNode { Name = r.Name, Table = r.Table, Kind = DependencyKind.Column });
                continue;
            }

            var m = model.FindMeasure(r.Name);
            if (m != null)
            {
                if (path.Contains(r.Name))
                {
                    node.Children.Add(new MeasureNode { Name = r.Name, Table = m.Table, Kind = DependencyKind.Cycle });
                }
                else
                {
                    var child = new MeasureNode
                    {
                        Name = m.Name,
                        Table = m.Table,
                        Expression = m.Expression,
                        SourceModel = m.ModelName,
                        Kind = DependencyKind.Measure
                    };
                    BuildChildren(child, m, model, new HashSet<string>(path, StringComparer.OrdinalIgnoreCase) { m.Name });
                    node.Children.Add(child);
                }
            }
            else if (model.ColumnNames.Contains(r.Name))
            {
                node.Children.Add(new MeasureNode { Name = r.Name, Kind = DependencyKind.Column });
            }
            else
            {
                node.Children.Add(new MeasureNode { Name = r.Name, Kind = DependencyKind.Unresolved });
            }
        }
    }

    /// <summary>Measures whose DAX references <paramref name="measureName"/> (reverse dependency / where-used in the model).</summary>
    public static IReadOnlyList<MeasureDef> FindReferencingMeasures(string measureName, TmdlModel model)
    {
        var result = new List<MeasureDef>();
        foreach (var list in model.MeasuresByName.Values)
        {
            foreach (var def in list)
            {
                if (string.Equals(def.Name, measureName, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var r in ExtractRefs(def.Expression))
                {
                    if (!r.HasTable && string.Equals(r.Name, measureName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(def);
                        break;
                    }
                }
            }
        }
        return result;
    }

    private readonly record struct Ref(string Table, string Name, bool HasTable)
    {
        public string Key => HasTable ? $"{Table}[{Name}]" : $"[{Name}]";
    }

    private static IEnumerable<Ref> ExtractRefs(string expression)
    {
        string clean = StripCommentsAndStrings(expression);
        foreach (Match m in RefRegex.Matches(clean))
        {
            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0) continue;

            string table = m.Groups["t1"].Success ? m.Groups["t1"].Value
                         : m.Groups["t2"].Success ? m.Groups["t2"].Value
                         : "";
            yield return new Ref(table, name, table.Length > 0);
        }
    }

    /// <summary>Blanks out DAX comments and string literals so they don't yield false references.</summary>
    private static string StripCommentsAndStrings(string expr)
    {
        var sb = new StringBuilder(expr.Length);
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];

            if ((c == '-' && i + 1 < expr.Length && expr[i + 1] == '-') ||
                (c == '/' && i + 1 < expr.Length && expr[i + 1] == '/'))
            {
                while (i < expr.Length && expr[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < expr.Length && expr[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < expr.Length && !(expr[i] == '*' && expr[i + 1] == '/')) i++;
                i++; // land on '/'
                sb.Append(' ');
                continue;
            }
            if (c == '"')
            {
                i++;
                while (i < expr.Length && expr[i] != '"') i++;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
