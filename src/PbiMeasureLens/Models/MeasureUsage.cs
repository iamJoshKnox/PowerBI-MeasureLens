namespace PbiMeasureLens.Models;

/// <summary>One place in the report where a given measure is displayed.</summary>
public sealed class MeasureUsage
{
    public string Page { get; init; } = "";
    public string Visual { get; init; } = "";
    public string DisplayName { get; init; } = "";
}
