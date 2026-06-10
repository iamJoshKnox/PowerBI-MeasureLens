namespace PbiMeasureLens.Models;

public enum FieldKind
{
    Measure,
    Column
}

/// <summary>
/// One field projected into a visual: its original model name and the (possibly renamed)
/// display name shown to report consumers.
/// </summary>
public sealed class FieldMapping
{
    public FieldKind Kind { get; init; }
    public string Table { get; init; } = "";
    public string OriginalName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsRenamed { get; init; }

    // Report context (set when the visual is parsed) — enables whole-report audit and footprint views.
    public string Page { get; set; } = "";
    public string VisualType { get; set; } = "";
    public string VisualId { get; set; } = "";
    public string VisualTitle { get; set; } = "";

    public string KindText => Kind == FieldKind.Measure ? "Measure" : "Column";
    // DAX-idiomatic reference: measures are bare [Name]; columns are 'Table'[Column].
    public string Qualified => Kind == FieldKind.Measure || string.IsNullOrEmpty(Table)
        ? $"[{OriginalName}]"
        : $"'{Table}'[{OriginalName}]";
    public string VisualLabel => string.IsNullOrWhiteSpace(VisualTitle) ? VisualType : VisualTitle;
}
