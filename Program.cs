using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace coreclr_pgo_compare
{
    class Program
    {
        private const string DotnetPgoPath = @"D:\dev\dotnet\runtime\artifacts\bin\coreclr\windows.x64.Debug\dotnet-pgo\dotnet-pgo.dll";

        static async Task Main(string[] args)
        {
            string[] platforms = { "windows_nt-x64", "windows_nt-x86", "linux-x64" };

            string resultDir = @"D:\dev\dotnet\coreclr_pgo_compare\result";
            var data = new List<PlatformScenarioData>();
            foreach (string platform in platforms)
            {
                await ProcessPlatformAsync(platform, data, resultDir);
            }

            static string ToAnchor(string heading)
                => "#" + heading.ToLowerInvariant().Replace(' ', '-');

            Directory.CreateDirectory(resultDir);
            StringBuilder toc = new StringBuilder();
            toc.AppendLine("Table of contents");
            StringBuilder contents = new StringBuilder();
            foreach (var byPlatform in data.GroupBy(d => d.Platform))
            {
                toc.AppendLine($"* [{byPlatform.Key}]({ToAnchor(byPlatform.Key)})");
                contents.AppendLine($"# {byPlatform.Key}");
                foreach (var byScenario in byPlatform.GroupBy(d => d.Scenario))
                {
                    toc.AppendLine($"  * [{byScenario.Key}]({ToAnchor(byPlatform.Key + "-" + byScenario.Key)})");
                    contents.AppendLine($"# {byPlatform.Key} {byScenario.Key}");

                    foreach (PlatformScenarioData graphs in byScenario)
                    {
                        foreach (string graph in graphs.Graphs)
                        {
                            contents.AppendLine($"![]({graph.Replace('\\', '/')})").AppendLine();
                        }
                    }
                }
            }

            File.WriteAllText(Path.Combine(resultDir, "README.md"), toc.ToString() + Environment.NewLine + Environment.NewLine + contents.ToString());
            File.WriteAllText(Path.Combine(resultDir, "graphs.txt"), string.Join(Environment.NewLine, data.Select(d => d.MathematicaCodeToCreateGraphs)));
        }

        private static async Task ProcessPlatformAsync(string platform, List<PlatformScenarioData> data, string resultDir)
        {
            Console.WriteLine("Processing platform {0}", platform);
            string package = $"optimization.{platform}.MIBC.Runtime";

            string platformDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "output", platform);
            Directory.CreateDirectory(platformDir);

            string source = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet6-transport/nuget/v3/index.json";

            SourceCacheContext cache = new SourceCacheContext();
            SourceRepository repo = Repository.Factory.GetCoreV3(source);
            FindPackageByIdResource resource = await repo.GetResourceAsync<FindPackageByIdResource>();

            List<NuGetVersion> versions = (await resource.GetAllVersionsAsync(package, cache, NullLogger.Instance, default)).ToList();
            versions.RemoveAll(ver => ver.Version == new Version(99, 99, 99, 0));
            versions.Sort();

            string packageDir = Path.Combine(platformDir, "packages");
            Directory.CreateDirectory(packageDir);

            using var parallelismSem = new SemaphoreSlim(8);
            // Ensure that the .mibc files with the specified version are extracted and present in the output dir.
            async Task EnsurePackageAsync(NuGetVersion ver)
            {
                string verDir = Path.Combine(packageDir, ver.ToNormalizedString());
                string mergedPath = Path.Combine(verDir, "Merged.mibc");
                if (File.Exists(mergedPath))
                {
                    return;
                }

                await parallelismSem.WaitAsync();
                try
                {
                    Directory.CreateDirectory(verDir);
                    using MemoryStream ms = new();
                    await resource.CopyNupkgToStreamAsync(package, ver, ms, cache, NullLogger.Instance, default);
                    using PackageArchiveReader reader = new(ms);
                    var mibcFiles = reader.GetFiles().Where(f => f.EndsWith(".mibc", StringComparison.OrdinalIgnoreCase));
                    foreach (var file in mibcFiles)
                    {
                        reader.ExtractFile(file, Path.Combine(verDir, Path.GetFileName(file)), NullLogger.Instance);
                    }

                    string[] args = { DotnetPgoPath, "merge", "--output", mergedPath };
                    args = args.Concat(mibcFiles.SelectMany(name => new[] { "--input", Path.Combine(verDir, Path.GetFileName(name)) })).ToArray();

                    ExecuteAndReadStdOut("dotnet", args);
                }
                finally
                {
                    parallelismSem.Release();
                }

                Console.WriteLine("Downloaded and merged {0}", ver.ToNormalizedString());
            }

            await Task.WhenAll(versions.Select(EnsurePackageAsync));

            string[] scenarioNames = { "DotNet_TechEmpower", "DotNet_FirstTimeXP", "DotNet_HelloWorld", "DotNet_OrchardCore", "Merged" };
            foreach (string scenarioName in scenarioNames)
            {
                data.Add(CreatePlatformScenarioData(packageDir, platform, scenarioName, versions, resultDir));
            }
        }

        private static PlatformScenarioData CreatePlatformScenarioData(string packageDir, string platformName, string scenarioName, List<NuGetVersion> versions, string resultDir)
        {
            Console.WriteLine("  Processing scenario {0}", scenarioName);

            var overlaps = new double?[versions.Count - 1];
            var meanSquaredErrors = new double?[versions.Count - 1];
            var matchingFgs = new double?[versions.Count - 1];
            var mismatchingFgs = new double?[versions.Count - 1];
            var lessThan50s = new double?[versions.Count - 1];
            Parallel.For(
                0, versions.Count - 1,
                i =>
            {
                string ver1 = versions[i].ToNormalizedString();
                string ver2 = versions[i + 1].ToNormalizedString();
                string path1 = Path.Combine(packageDir, ver1, scenarioName + ".mibc");
                string path2 = Path.Combine(packageDir, ver2, scenarioName + ".mibc");
                string log1 = Path.Combine(ver1, scenarioName + ".mibc");
                string log2 = Path.Combine(ver2, scenarioName + ".mibc");
                if (!File.Exists(path1) && !File.Exists(path2))
                {
                    Console.WriteLine("Not comparing {0} with {1}: files not found", log1, log2);
                    return;
                }
                if (!File.Exists(path1))
                {
                    Console.WriteLine("Not comparing {0} with {1}: {0} not found", log1, log2);
                    return;
                }
                if (!File.Exists(path2))
                {
                    Console.WriteLine("Not comparing {0} with {1}: {1} not found", log1, log2);
                    return;
                }

                List<string> lines = ExecuteAndReadStdOut("dotnet", DotnetPgoPath, "compare-mibc", "--input", path1, "--input", path2);
                int? matching = null;
                int? mismatching = null;
                double? avgOverlap = null;
                double? meanSquaredError = null;
                int? lessThan50 = null;
                foreach (string line in lines)
                {
                    Match m = Regex.Match(line, "Of these, ([0-9]+) have matching flow-graphs and the remaining ([0-9]+) do not");
                    if (m.Success)
                    {
                        Trace.Assert(!matching.HasValue && !mismatching.HasValue);
                        matching = int.Parse(m.Groups[1].Value);
                        mismatching = int.Parse(m.Groups[2].Value);
                        continue;
                    }

                    m = Regex.Match(line, "The average overlap is ([0-9]+\\.[0-9]+)% for the ([0-9]+) methods");
                    if (m.Success)
                    {
                        Trace.Assert(!avgOverlap.HasValue);
                        avgOverlap = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        continue;
                    }

                    m = Regex.Match(line, "The mean squared error is ([0-9]+\\.[0-9]+)");
                    if (m.Success)
                    {
                        Trace.Assert(!meanSquaredError.HasValue);
                        meanSquaredError = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        continue;
                    }

                    m = Regex.Match(line, "There are ([0-9]+)/[0-9]+ methods with overlaps < 50%");
                    if (m.Success)
                    {
                        Trace.Assert(!lessThan50.HasValue);
                        lessThan50 = int.Parse(m.Groups[1].Value);
                    }
                }

                Trace.Assert(matching.HasValue && mismatching.HasValue && avgOverlap.HasValue && meanSquaredError.HasValue && lessThan50.HasValue);
                overlaps[i] = avgOverlap.Value;
                matchingFgs[i] = matching.Value;
                mismatchingFgs[i] = mismatching.Value;
                meanSquaredErrors[i] = meanSquaredError.Value;
                lessThan50s[i] = lessThan50.Value;
            });

            var graphs = new List<string>();
            var sb = new StringBuilder();
            void AppendGraph(double?[] vals, string xLabel, string graphFileName)
            {
                sb.AppendLine("plot = ListPlot[");
                // At y=0 we place the first label of version, y=1 we place the second, etc.
                // Then at y=0.5 we place the point with the value as the x coordinate, to make it sit between two labels.
                sb.Append("  {").AppendJoin(",", vals.Select((v, i) => (v, i)).Where(t => t.v.HasValue).Select(t => FormattableString.Invariant($"{{{t.v.Value:R},{0.5 + t.i:R}}}"))).AppendLine("},");
                sb.AppendLine("  PlotRange->Full,");
                sb.AppendLine("  Axes->False,");
                sb.AppendLine("  Frame->{{True,False},{True,False}},");
                sb.Append("  FrameLabel->{{\"\",None},{\"").Append(xLabel).AppendLine("\",None}},");
                sb.AppendLine("  FrameStyle->Directive[16,Black],");
                sb.AppendLine("  FrameTicksStyle->{{Directive[Black,12],Automatic},{Automatic,Automatic}},");
                sb.AppendLine("  PlotMarkers->{Automatic,Small},");
                sb.AppendLine("  ImageSize->1000,");
                string leftTicks = string.Join(",", versions.Select((v, i) => $"{{{i},\"{v.ToNormalizedString()}\"}}"));
                sb.AppendLine("  FrameTicks->{{").Append(leftTicks).Append("},{Automatic,Automatic}},");
                string horizontalLines =
                    string.Join(
                        $",",
                        versions.Select((v, i) => $"InfiniteLine[{{{{0,{i}}},{{1,{i}}}}}]"));
                sb.Append("  Epilog->{").Append(horizontalLines).AppendLine("}");
                sb.AppendLine("];");

                string relPath = Path.Combine(platformName, scenarioName, graphFileName);
                string fullPath = Path.Combine(resultDir, relPath);
                string escaped = fullPath.Replace("\\", "\\\\");
                sb.AppendLine($"Export[\"{escaped}\", plot, ImageResolution->144]");
                graphs.Add(relPath);
            }

            AppendGraph(overlaps, "Average overlap", "average_overlap.png");
            AppendGraph(meanSquaredErrors, "Mean squared errors", "mean_squared_errors.png");
            AppendGraph(matchingFgs, "# Matching FGs", "matching_fgs.png");
            AppendGraph(mismatchingFgs, "# Mismatching FGs", "mismatching_fgs.png");
            AppendGraph(lessThan50s, "# Matches with < 50% overlap", "low_overlap_matches.png");
            return new PlatformScenarioData
            {
                Platform = platformName,
                Scenario = scenarioName,
                MathematicaCodeToCreateGraphs = sb.ToString(),
                Graphs = graphs
            };
        }

        private static List<string> ExecuteAndReadStdOut(string name, params string[] args)
        {
            var psi = new ProcessStartInfo(name);
            psi.StandardOutputEncoding = Encoding.UTF8;
            foreach (string a in args)
                psi.ArgumentList.Add(a);

            Console.WriteLine("{0} {1}", name, string.Join(" ", args));

            psi.RedirectStandardOutput = true;
            using Process p = Process.Start(psi);
            var lines = new List<string>();
            while (!p.StandardOutput.EndOfStream)
            {
                lines.Add(p.StandardOutput.ReadLine());
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
                throw new Exception("Process returned exit code " + p.ExitCode);
            return lines;
        }

        private class PlatformScenarioData
        {
            public string Platform { get; init; }
            public string Scenario { get; init; }
            public List<string> Graphs { get; init; }
            public string MathematicaCodeToCreateGraphs { get; init; }
        }
    }
}
