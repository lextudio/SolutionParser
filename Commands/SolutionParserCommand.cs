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
                    Console.Error.WriteLine($"[slnx] Found {projectNodes.Count()} <Project> nodes");
                    var slnDir = Path.GetDirectoryName(solutionFilePath)!;
                    List<ProjectRecord> collected = new();
                    foreach (var node in projectNodes)
                    {
                        var nameAttr = (string?)node.Attribute("Name");
                        var includeRaw = (string?)node.Attribute("Include")
                            ?? (string?)node.Attribute("Path")
                            ?? (string?)node.Attribute("File")
                            ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(includeRaw))
                        {
                            Console.Error.WriteLine("[slnx] Skipping <Project> with no Include/Path/File attribute");
                            continue;
                        }
                        var resolved = ResolveProjectReference(slnDir, includeRaw);
                        foreach (var r in resolved)
                        {
                            if (File.Exists(r))
                            {
                                var prName = nameAttr ?? Path.GetFileNameWithoutExtension(r);
                                collected.Add(new ProjectRecord(prName, r));
                                Console.Error.WriteLine($"[slnx] Accepted '{includeRaw}' -> '{r}'");
                            }
                            else
                            {
                                Console.Error.WriteLine($"[slnx] Not found '{includeRaw}' -> '{r}'");
                            }
                        }
                    }
                    projFiles = collected
                        .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();
                    Console.Error.WriteLine($"[slnx] Resolved {projFiles.Count()} project file(s) after normalization");
                    if (projFiles is null || projFiles.Count() == 0)
                    {
                        // Fallback: scan directory for project files if explicit project nodes didn't resolve.
                        var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", "node_modules" };
                        var discovered = Directory.EnumerateFiles(slnDir, "*.*", SearchOption.AllDirectories)
                            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
                            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => excludedDirs.Contains(part)))
                            .Take(100)
                            .Select(p => new ProjectRecord(Path.GetFileNameWithoutExtension(p), p))
                            .ToList();
                        Console.Error.WriteLine($"[slnx] Fallback scan found {discovered.Count} project file(s) (after exclusions)");
                        projFiles = discovered;
                    }
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

    /// <summary>
    /// Resolve a .slnx project reference which may be:
    ///  - A relative path to a .csproj/.fsproj
    ///  - A directory containing exactly one .csproj/.fsproj
    ///  - A stem (project name without extension) in the solution directory or subdirectory
    /// Returns one or more candidate full paths (usually 0 or 1; >1 only if ambiguous directory contains multiple project files).
    /// </summary>
    static IEnumerable<string> ResolveProjectReference(string solutionDir, string includeRaw)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(includeRaw)) return results;

        string norm = includeRaw.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).Trim();
        string full = Path.GetFullPath(norm, solutionDir);

        // If direct file with extension
        if (File.Exists(full)) { results.Add(full); return results; }

        // If path without extension but directory exists -> scan inside for project files (top-level only)
        if (Directory.Exists(full))
        {
            var projFiles = Directory.GetFiles(full, "*.csproj").Concat(Directory.GetFiles(full, "*.fsproj"));
            results.AddRange(projFiles);
            return results;
        }

        // If no extension supplied, try adding .csproj / .fsproj
        if (!Path.HasExtension(full))
        {
            var cs = full + ".csproj";
            var fs = full + ".fsproj";
            if (File.Exists(cs)) results.Add(cs);
            if (File.Exists(fs)) results.Add(fs);
            if (results.Count > 0) return results;
        }

        // As last resort, search recursively (limited depth) for matching file name
        var stem = Path.GetFileName(full);
        if (!string.IsNullOrEmpty(stem))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(solutionDir, "*.*", SearchOption.AllDirectories))
                {
                    if (!(f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))) continue;
                    if (Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(f);
                        if (results.Count > 10) break; // safety cap
                    }
                }
            }
            catch { /* ignore */ }
        }
        return results;
    }
}
