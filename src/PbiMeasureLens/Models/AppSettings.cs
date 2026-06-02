using System.Collections.Generic;

namespace PbiMeasureLens.Models;

public sealed class AppSettings
{
    public string? LastPbixPath { get; set; }

    /// <summary>Root folders scanned (recursively) for *.SemanticModel definitions.</summary>
    public List<string> SemanticModelRoots { get; set; } = new();
}
