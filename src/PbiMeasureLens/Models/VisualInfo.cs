using System.Collections.Generic;
using System.Linq;

namespace PbiMeasureLens.Models;

/// <summary>A single visual on a report page and the fields it projects.</summary>
public sealed class VisualInfo
{
    public string Page { get; init; } = "";
    public int PageOrdinal { get; init; }
    public string VisualId { get; init; } = "";
    public string VisualType { get; init; } = "";
    public string Title { get; set; } = "";
    public List<FieldMapping> Fields { get; } = new();

    public bool HasRenames => Fields.Any(f => f.IsRenamed);

    /// <summary>Label shown in the visual picker list.</summary>
    public string Label
    {
        get
        {
            var preview = string.Join(", ", Fields.Select(f => f.DisplayName));
            if (preview.Length > 70) preview = preview[..67] + "...";
            var head = string.IsNullOrWhiteSpace(Title) ? VisualType : $"{VisualType} — \"{Title}\"";
            var renameFlag = HasRenames ? "  ✎" : "";
            return preview.Length == 0 ? $"{head}{renameFlag}" : $"{head}{renameFlag}   [{preview}]";
        }
    }
}
