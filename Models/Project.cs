using System.Text.Json.Serialization;
using MSProject = Microsoft.Build.Evaluation.Project;

namespace Models;
internal class Project
{
    public required string Name { get; set; }
    public required string Path { get; set; }

    public required string TargetPath { get; set; }
    public required string OutputType { get; set; }
    // Normalized textual representation of OutputType (e.g. WinExe, Exe, Library)
    public string NormalizedOutputType { get; set; } = string.Empty;
    public required string DesignerHostPath { get; set; }
    public required string TargetFramework { get; set; }
    public required string DepsFilePath { get; set; }
    public required string RuntimeConfigFilePath { get; set; }
    public required string[] ProjectReferences { get; set; }

    [JsonIgnore]
    public MSProject? CoreProject { get; set; } = null;

    [JsonIgnore]
    public List<ProjectFile>? AvaloniaXamlFiles { get; set; }

    public string? DirectoryPath => System.IO.Path.GetDirectoryName(Path);

    public string? IntermediateOutputPath { get; internal set; }

    public override string ToString() => $"{Name} ({OutputType}) - {DesignerHostPath}";
}

internal class ProjectFile
{
    public required string Path { get; set; }

    public required string TargetPath { get; set; }
    public required string ProjectPath { get; set; }

    public override string ToString() => $"{Path} ({TargetPath}) - {ProjectPath}";
}