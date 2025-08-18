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
            if (proj.AvaloniaXamlFiles != null)
            {
                proj.AvaloniaXamlFiles.ForEach(designerFiles.Add);
            }
            else
            {
                proj.CoreProject?.GetItems("AvaloniaXaml")?.ToList().ForEach(item =>
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
            var targetfx = proj.GetPropertyValue("TargetFramework");
            if (string.IsNullOrEmpty(targetfx))
            {
                var targetfxs = proj.GetPropertyValue("TargetFrameworks");
                if (!string.IsNullOrEmpty(targetfxs))
                {
                    var frameworks = targetfxs.Split(';');
                    foreach (var fw in frameworks)
                    {
                        var trimmed = fw.Trim();
                        if (trimmed.StartsWith("net") && trimmed.Length > 3 && char.IsDigit(trimmed[3]))
                        {
                            try
                            {
                                Project? value = LoadPropertiesAndItemsFromShell(name, projPath, proj, trimmed);
                                return value;
                            }
                            catch (Exception msbuildEx)
                            {
                                Console.Error.WriteLine($"[msbuild] Failed to run dotnet msbuild for {projPath} ({trimmed}): {msbuildEx.Message}");
                            }
                        }
                    }
                    if (string.IsNullOrEmpty(targetfx))
                    {
                        Console.Error.WriteLine($"[warning] Project '{name}' TargetFrameworks does not contain a compatible .NET TPM (netX.Y). Asset discovery may fail.");
                        return null;
                    }
                }
            }

            var assembly = proj.GetPropertyValue("TargetPath");
            var outputType = proj.GetPropertyValue("outputType");
            var normalizedOutputType = NormalizeOutputType(outputType);
            var designerHostPath = proj.GetPropertyValue("AvaloniaPreviewerNetCoreToolPath");

            var projectDepsFilePath = proj.GetPropertyValue("ProjectDepsFilePath");
            var projectRuntimeConfigFilePath = proj.GetPropertyValue("ProjectRuntimeConfigFilePath");

            var references = proj.GetItems("ProjectReference");
            var referencesPath = references.Select(p => Path.GetFullPath(p.EvaluatedInclude, projPath)).ToArray();
            designerHostPath = string.IsNullOrEmpty(designerHostPath) ? "" : Path.GetFullPath(designerHostPath);

            var intermediateOutputPath = GetIntermediateOutputPath(proj);

            Console.Error.WriteLine($"[assets] Project: {name}");
            Console.Error.WriteLine($"[assets]   Path: {projPath}");
            Console.Error.WriteLine($"[assets]   TargetPath: {assembly}");
            Console.Error.WriteLine($"[assets]   OutputType: {outputType}");
            Console.Error.WriteLine($"[assets]   NormalizedOutputType: {normalizedOutputType}");
            Console.Error.WriteLine($"[assets]   DesignerHostPath: {designerHostPath}");
            Console.Error.WriteLine($"[assets]   TargetFramework: {targetfx}");
            Console.Error.WriteLine($"[assets]   DepsFilePath: {projectDepsFilePath}");
            Console.Error.WriteLine($"[assets]   RuntimeConfigFilePath: {projectRuntimeConfigFilePath}");
            Console.Error.WriteLine($"[assets]   IntermediateOutputPath: {intermediateOutputPath}");

            return new Project
            {
                Name = name,
                Path = projPath,
                TargetPath = assembly,
                OutputType = outputType,
                NormalizedOutputType = normalizedOutputType,
                DesignerHostPath = designerHostPath,

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

    private static Project? LoadPropertiesAndItemsFromShell(string name, string projPath, MSProject proj, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"msbuild \"{projPath}\" /p:TargetFramework={target} /getProperty:outputType /getProperty:AvaloniaPreviewerNetCoreToolPath /getProperty:TargetPath /getProperty:ProjectDepsFilePath /getProperty:ProjectRuntimeConfigFilePath /getItem:AvaloniaXaml",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(projPath)
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        proc?.WaitForExit(10000);
        var output = proc?.StandardOutput.ReadToEnd();
        var error = proc?.StandardError.ReadToEnd();
        Console.Error.WriteLine($"[msbuild] Output for {projPath} ({target}):\n{output}");
        if (!string.IsNullOrWhiteSpace(error))
            Console.Error.WriteLine($"[msbuild] Error for {projPath} ({target}):\n{error}");

        if (!string.IsNullOrWhiteSpace(output))
        {
            try
            {
                return GetProjectFromJson(name, projPath, proj, target, output);
            }
            catch (Exception jsonEx)
            {
                Console.Error.WriteLine($"Failed to parse msbuild JSON output: {jsonEx.Message}");
            }
        }

        return null;
    }

    private static Project GetProjectFromJson(string name, string projPath, MSProject proj, string target, string output)
    {
        using var doc = JsonDocument.Parse(output);
        var props = doc.RootElement.GetProperty("Properties");
        string targetPath = props.TryGetProperty("TargetPath", out var targetPathElem) ? targetPathElem.GetString() ?? "" : "";
        string outputType = props.TryGetProperty("outputType", out var outputTypeElem) ? outputTypeElem.GetString() ?? "" : "";
        string designerHostPath = props.TryGetProperty("AvaloniaPreviewerNetCoreToolPath", out var designerHostElem) ? designerHostElem.GetString() ?? "" : "";
        string depsFilePath = props.TryGetProperty("ProjectDepsFilePath", out var depsElem) ? depsElem.GetString() ?? "" : "";
        string runtimeConfigFilePath = props.TryGetProperty("ProjectRuntimeConfigFilePath", out var runtimeElem) ? runtimeElem.GetString() ?? "" : "";
        string intermediateOutputPath = props.TryGetProperty("IntermediateOutputPath", out var iopElem) ? iopElem.GetString() ?? "" : "";

        // Parse AvaloniaXaml items from Items section
        List<ProjectFile> avaloniaXamlFiles = new();
        if (doc.RootElement.TryGetProperty("Items", out var itemsObj))
        {
            if (itemsObj.TryGetProperty("AvaloniaXaml", out var axamlArray) && axamlArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in axamlArray.EnumerateArray())
                {
                    var fullPath = item.TryGetProperty("FullPath", out var fp) ? fp.GetString() : null;
                    if (!string.IsNullOrEmpty(fullPath))
                    {
                        avaloniaXamlFiles.Add(new ProjectFile
                        {
                            Path = fullPath!,
                            TargetPath = targetPath,
                            ProjectPath = projPath
                        });
                    }
                }
            }
        }

        return new Project
        {
            Name = name,
            Path = projPath,
            TargetPath = targetPath,
            OutputType = outputType,
            NormalizedOutputType = NormalizeOutputType(outputType),
            DesignerHostPath = designerHostPath,
            TargetFramework = target,
            DepsFilePath = depsFilePath,
            RuntimeConfigFilePath = runtimeConfigFilePath,
            CoreProject = proj,
            AvaloniaXamlFiles = avaloniaXamlFiles,
            ProjectReferences = Array.Empty<string>(),
            IntermediateOutputPath = GetIntermediateOutputPath(proj, intermediateOutputPath)
        };
    }

    static string GetIntermediateOutputPath(MSProject proj, string? overridingPath = null)
    {
        var intermediateOutputPath = overridingPath ?? proj.GetPropertyValue("IntermediateOutputPath");
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
