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

    public string KindText => Kind == FieldKind.Measure ? "Measure" : "Column";
    public string RenamedText => IsRenamed ? "Yes" : "";
    public string Qualified => string.IsNullOrEmpty(Table) ? OriginalName : $"{Table}[{OriginalName}]";
}
