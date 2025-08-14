using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using MSProject = Microsoft.Build.Evaluation.Project;
using Project = Models.Project;

namespace Commands;
public sealed class SolutionParserCommand : Command<SolutionParserCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
    [Description("The solution file (.sln or .slnx) path, or folder containing project files.")]
        [CommandArgument(0, "<SOLUTION>")]
        public required string Solution { get; init; }
    }

    record ProjectRecord(string Name, string Path);

    public override int Execute([NotNull] CommandContext context, [NotNull] Settings settings)
    {
        var solutionFilePath = Path.GetFullPath(settings.Solution);
        string? solutionFolderPath;

        bool isSln = solutionFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase);
        bool isSlnx = solutionFilePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

        if (!isSln && !isSlnx && Directory.Exists(solutionFilePath))
        {
            solutionFolderPath = solutionFilePath;
            solutionFilePath = null;
        }
        else if (File.Exists(solutionFilePath))
        {
            solutionFolderPath = Path.GetDirectoryName(solutionFilePath);
        }
        else
        {
            Console.Error.WriteLine($"Invalid solution path \"{solutionFilePath}\"");
            return 1;
        }

        // Lookup a MSBuild instance from the installed dotnet SDKs.
        // The results should NOT be ordered: the first one matches the global.json if present.
        var msbuildInstance = MSBuildLocator
            .QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
            {
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = solutionFolderPath
            })
            .FirstOrDefault();

        if (msbuildInstance is null)
        {
            Console.Error.WriteLine($"Could not find a matching .NET SDK for {solutionFolderPath}");
            return 2;
        }

        MSBuildLocator.RegisterInstance(msbuildInstance);

        ExecuteCore(solutionFilePath, solutionFolderPath);
        return 0;
    }

    private void ExecuteCore(string? solutionFilePath, string? solutionFolderPath)
    {
        IEnumerable<ProjectRecord>? projFiles;

        if (solutionFilePath is not null)
        {
            if (solutionFilePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                var sln = SolutionFile.Parse(solutionFilePath);
                projFiles = sln.ProjectsInOrder
                    .Where(prj => prj.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat)
                    .Select(prj => new ProjectRecord(prj.ProjectName, prj.AbsolutePath));
            }
            else if (solutionFilePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var doc = XDocument.Load(solutionFilePath);
                    XNamespace ns = doc.Root?.Name.Namespace ?? "";
                    var projectNodes = doc.Descendants(ns + "Project");
                    projFiles = projectNodes
                        .Select(x => new ProjectRecord(
                            (string?)x.Attribute("Name") ?? Path.GetFileNameWithoutExtension((string?)x.Attribute("Include") ?? string.Empty),
                            Path.GetFullPath((string?)x.Attribute("Include") ?? string.Empty, Path.GetDirectoryName(solutionFilePath)!)))
                        .Where(r => !string.IsNullOrEmpty(r.Path) && File.Exists(r.Path))
                        .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error parsing .slnx file: {ex.Message}");
                    projFiles = Enumerable.Empty<ProjectRecord>();
                }
            }
            else
            {
                projFiles = Enumerable.Empty<ProjectRecord>();
            }
        }
        else if (solutionFolderPath is not null)
        {
            string[] projFileGlobs = ["*.csproj", "*.fsproj"];
            projFiles = projFileGlobs
                .SelectMany(glob => Directory.GetFiles(solutionFolderPath, glob))
                .Select(p => new ProjectRecord(Path.GetFileNameWithoutExtension(p), p));
        }
        else
        {
            throw new InvalidOperationException("Invalid solution path");
        }

        var projects = new ConcurrentBag<Project>();
        Parallel.ForEach(projFiles, proj =>
        {
            var projectDetails = GetProjectDetails(proj.Name, proj.Path);
            if (projectDetails != null)
                projects.Add(projectDetails);
        });

        var allProjects = projects.ToList();

        List<ProjectFile> designerFiles = new();

        foreach (var proj in allProjects)
        {
            proj.CoreProject?.GetItems("AvaloniaXaml").ToList().ForEach(item =>
            {
                var filePath = Path.GetFullPath(item.EvaluatedInclude, proj.DirectoryPath ?? "");
                var designerFile = new ProjectFile
                {
                    Path = filePath,
                    TargetPath = proj.TargetPath,
                    ProjectPath = proj.Path
                };
                designerFiles.Add(designerFile);
            });
        }

        var solution = solutionFilePath ?? solutionFolderPath;
        var json = new { Solution = solution, Projects = allProjects, Files = designerFiles };

        var jsonStr = JsonSerializer.Serialize(json, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var jsonFilePath = Path.Combine(Path.GetTempPath(), $"{Path.GetFileName(solution)}.json");
        File.WriteAllText(jsonFilePath, jsonStr);

        Console.WriteLine(jsonStr);
    }

    Project? GetProjectDetails(string name, string projPath)
    {
        try
        {
            var proj = MSProject.FromFile(projPath, new ProjectOptions
            {
                LoadSettings = ProjectLoadSettings.IgnoreMissingImports | ProjectLoadSettings.FailOnUnresolvedSdk
            });

            var assembly = proj.GetPropertyValue("TargetPath");
            var outputType = proj.GetPropertyValue("outputType");
            var normalizedOutputType = NormalizeOutputType(outputType);
            var desingerHostPath = proj.GetPropertyValue("AvaloniaPreviewerNetCoreToolPath");

            var targetfx = proj.GetPropertyValue("TargetFramework");
            var projectDepsFilePath = proj.GetPropertyValue("ProjectDepsFilePath");
            var projectRuntimeConfigFilePath = proj.GetPropertyValue("ProjectRuntimeConfigFilePath");

            var references = proj.GetItems("ProjectReference");
            var referencesPath = references.Select(p => Path.GetFullPath(p.EvaluatedInclude, projPath)).ToArray();
            desingerHostPath = string.IsNullOrEmpty(desingerHostPath) ? "" : Path.GetFullPath(desingerHostPath);

            var intermediateOutputPath = GetIntermediateOutputPath(proj);

            return new Project
            {
                Name = name,
                Path = projPath,
                TargetPath = assembly,
                OutputType = outputType,
                NormalizedOutputType = normalizedOutputType,
                DesignerHostPath = desingerHostPath,

                TargetFramework = targetfx,
                DepsFilePath = projectDepsFilePath,
                RuntimeConfigFilePath = projectRuntimeConfigFilePath,

                CoreProject = proj,
                ProjectReferences = referencesPath,
                IntermediateOutputPath = intermediateOutputPath

            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error parsing project {name}: {ex.Message}");
            return null;
        }
    }

    static string GetIntermediateOutputPath(MSProject proj)
    {
        var intermediateOutputPath = proj.GetPropertyValue("IntermediateOutputPath");
        var iop = Path.Combine(intermediateOutputPath, "Avalonia", "references");

        if (!Path.IsPathRooted(intermediateOutputPath))
        {
            iop = Path.Combine(proj.DirectoryPath ?? "", iop);
            if (Path.DirectorySeparatorChar == '/')
                iop = iop.Replace("\\", "/");
        }

        return iop;
    }

    static string NormalizeOutputType(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // MSBuild sometimes returns numeric values (0=Library, 1=Exe, 2=WinExe, 3=Module per legacy) or textual.
        return raw.Trim() switch
        {
            "0" => "Library",
            "1" => "Exe",
            "2" => "WinExe",
            "3" => "Module",
            var s when s.Equals("library", StringComparison.OrdinalIgnoreCase) => "Library",
            var s when s.Equals("exe", StringComparison.OrdinalIgnoreCase) => "Exe",
            var s when s.Equals("winexe", StringComparison.OrdinalIgnoreCase) => "WinExe",
            _ => raw
        };
    }
}
