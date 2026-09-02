using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace UAssetGUI
{
    public sealed class AssetOpenStressResult
    {
        public string AssetPath { get; set; }
        public string ExtractedPath { get; set; }
        public string Status { get; set; } = "failed";
        public bool Loaded { get; set; }
        public bool? BinaryEqualityVerified { get; set; }
        public bool HasUnversionedProperties { get; set; }
        public bool HadMappings { get; set; }
        public bool HasDuplicateNameMapEntries { get; set; }
        public int ExportCount { get; set; }
        public int RawExportCount { get; set; }
        public int RawStructCount { get; set; }
        public string[] UnknownTypes { get; set; } = Array.Empty<string>();
        public string[] RawStructTypes { get; set; } = Array.Empty<string>();
        public string[] FailedDependencies { get; set; } = Array.Empty<string>();
        public List<string> Notices { get; set; } = new List<string>();
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
        public string ExceptionStackTrace { get; set; }
        public double ExtractionMilliseconds { get; set; }
        public double LoadMilliseconds { get; set; }

        public void FinishStatus()
        {
            if (!Loaded || ExceptionType != null || BinaryEqualityVerified == false || RawExportCount > 0 ||
                (HasUnversionedProperties && !HadMappings))
            {
                Status = "failed";
                return;
            }

            Status = Notices.Count > 0 || UnknownTypes.Length > 0 || RawStructTypes.Length > 0 ||
                FailedDependencies.Length > 0 || HasDuplicateNameMapEntries ? "notice" : "ok";
        }
    }

    internal sealed class AssetOpenStressSummary
    {
        public string Kind { get; set; } = "UAssetGUI hierarchy asset-open stress test";
        public string Status { get; set; }
        public string StartedAtUtc { get; set; }
        public string CompletedAtUtc { get; set; }
        public string ContainerPath { get; set; }
        public string ContainerSha256 { get; set; }
        public StressContainerIdentity[] MountedContainers { get; set; } = Array.Empty<StressContainerIdentity>();
        public string Prefix { get; set; }
        public string EngineVersion { get; set; }
        public string MappingsPath { get; set; }
        public string MappingsSha256 { get; set; }
        public int RequestedLimit { get; set; }
        public int DiscoveredAssets { get; set; }
        public int AttemptedAssets { get; set; }
        public int OkAssets { get; set; }
        public int NoticeAssets { get; set; }
        public int FailedAssets { get; set; }
        public string ResultsFile { get; set; }
        public string FatalExceptionType { get; set; }
        public string FatalExceptionMessage { get; set; }
        public double ElapsedSeconds { get; set; }
    }

    internal sealed class StressContainerIdentity
    {
        public string Name { get; set; }
        public long Length { get; set; }
        public string Sha256 { get; set; }
    }

    internal static class AssetOpenStressRunner
    {
        private const string Usage =
            "stress-open <container.utoc> <virtual-prefix> <report-directory> <engine-version> <mappings.usmap> [limit] [--resume]";

        public static int Run(string[] args)
        {
            if (args.Length < 5 || args.Length > 7)
            {
                WriteEmergencyError("Invalid arguments. Usage: " + Usage);
                return 1;
            }

            string containerPath = Path.GetFullPath(args[0]);
            string prefix = NormalizePath(args[1]).TrimEnd('/') + "/";
            string reportDirectory = Path.GetFullPath(args[2]);
            string mappingsPath = Path.GetFullPath(args[4]);
            int limit = 0;
            bool resume = false;
            foreach (string optionalArgument in args.Skip(5))
            {
                if (optionalArgument.Equals("--resume", StringComparison.OrdinalIgnoreCase))
                {
                    resume = true;
                }
                else if (!int.TryParse(optionalArgument, out limit) || limit < 0)
                {
                    WriteEmergencyError("Limit must be a non-negative integer.");
                    return 1;
                }
            }

            EngineVersion engineVersion = ParseEngineVersion(args[3]);
            if (engineVersion == EngineVersion.UNKNOWN)
            {
                WriteEmergencyError("Unknown engine version: " + args[3]);
                return 1;
            }

            Directory.CreateDirectory(reportDirectory);
            string resultsPath = Path.Combine(reportDirectory, "results.jsonl");
            string summaryPath = Path.Combine(reportDirectory, "summary.json");
            bool hasExistingResults = File.Exists(resultsPath);
            bool hasExistingSummary = File.Exists(summaryPath);
            if ((hasExistingResults || hasExistingSummary) && !resume)
            {
                WriteEmergencyError("The report directory already contains stress-test output: " + reportDirectory);
                return 1;
            }
            if (resume && (!hasExistingResults || !hasExistingSummary))
            {
                WriteEmergencyError("A resumable run requires both results.jsonl and summary.json: " + reportDirectory);
                return 1;
            }

            DateTime startedAtUtc = DateTime.UtcNow;
            Stopwatch totalTimer = Stopwatch.StartNew();
            string containerSha256 = GetFileSha256(containerPath);
            string mappingsSha256 = GetFileSha256(mappingsPath);
            StressContainerIdentity[] mountedContainers = Directory
                .GetFiles(Path.GetDirectoryName(containerPath), "*.utoc", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new StressContainerIdentity
                {
                    Name = Path.GetFileName(path),
                    Length = new FileInfo(path).Length,
                    Sha256 = GetFileSha256(path)
                })
                .ToArray();
            AssetOpenStressSummary summary = resume
                ? JsonConvert.DeserializeObject<AssetOpenStressSummary>(File.ReadAllText(summaryPath))
                : new AssetOpenStressSummary
            {
                Status = "running",
                StartedAtUtc = startedAtUtc.ToString("o"),
                ContainerPath = containerPath,
                ContainerSha256 = containerSha256,
                MountedContainers = mountedContainers,
                Prefix = prefix,
                EngineVersion = engineVersion.ToString(),
                MappingsPath = mappingsPath,
                MappingsSha256 = mappingsSha256,
                RequestedLimit = limit,
                ResultsFile = resultsPath
            };

            if (resume && (summary == null ||
                !summary.ContainerPath.Equals(containerPath, StringComparison.OrdinalIgnoreCase) ||
                summary.ContainerSha256 != containerSha256 ||
                JsonConvert.SerializeObject(summary.MountedContainers) != JsonConvert.SerializeObject(mountedContainers) ||
                !summary.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                summary.EngineVersion != engineVersion.ToString() ||
                !summary.MappingsPath.Equals(mappingsPath, StringComparison.OrdinalIgnoreCase) ||
                summary.MappingsSha256 != mappingsSha256 ||
                summary.RequestedLimit != limit))
            {
                WriteEmergencyError("The existing report identity does not match the requested resumed run.");
                return 1;
            }

            double previousElapsedSeconds = resume ? summary.ElapsedSeconds : 0;
            var completedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (resume)
            {
                summary.AttemptedAssets = 0;
                summary.OkAssets = 0;
                summary.NoticeAssets = 0;
                summary.FailedAssets = 0;
                foreach (string line in File.ReadLines(resultsPath))
                {
                    AssetOpenStressResult priorResult = JsonConvert.DeserializeObject<AssetOpenStressResult>(line);
                    if (priorResult == null || string.IsNullOrEmpty(priorResult.AssetPath)) continue;
                    completedPaths.Add(priorResult.AssetPath);
                    summary.AttemptedAssets++;
                    if (priorResult.Status == "ok") summary.OkAssets++;
                    else if (priorResult.Status == "notice") summary.NoticeAssets++;
                    else summary.FailedAssets++;
                }
                summary.Status = "running";
                summary.CompletedAtUtc = null;
                summary.FatalExceptionType = null;
                summary.FatalExceptionMessage = null;
            }

            WriteSummary(summaryPath, summary);
            try
            {
                Usmap mappings = new Usmap(mappingsPath);
                using Form1 baseForm = new Form1();
                baseForm.PrepareForAssetOpenStressTest(engineVersion, mappings);
                UAGPalette.InitializeTheme();

                using FileContainerForm containerForm = new FileContainerForm
                {
                    BaseForm = baseForm,
                    CurrentContainerPath = containerPath
                };
                containerForm.LoadContainer(containerPath);
                if (!containerForm.DirectoryTreeMap.TryGetValue(containerForm.loadTreeView, out DirectoryTree tree) || tree == null)
                {
                    throw new InvalidOperationException("UAssetGUI did not build the container hierarchy.");
                }

                DirectoryTreeItem[] allAssets = tree.RootNodesFlat.Values
                    .Where(item => item.IsFile)
                    .Where(item => item.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Where(item =>
                    {
                        string extension = Path.GetExtension(item.FullPath);
                        return extension.Equals(".uasset", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".umap", StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                summary.DiscoveredAssets = allAssets.Length;
                IEnumerable<DirectoryTreeItem> limitedAssets = limit > 0 ? allAssets.Take(limit) : allAssets;
                DirectoryTreeItem[] selectedAssets = limitedAssets
                    .Where(item => !completedPaths.Contains(item.FullPath))
                    .ToArray();
                using var results = new StreamWriter(resultsPath, resume) { AutoFlush = true };

                foreach (DirectoryTreeItem item in selectedAssets)
                {
                    var extractionTimer = Stopwatch.StartNew();
                    AssetOpenStressResult result;
                    try
                    {
                        string extractedPath = item.SaveFileToTemp(containerForm.InteropType);
                        extractionTimer.Stop();
                        if (string.IsNullOrEmpty(extractedPath))
                        {
                            result = new AssetOpenStressResult
                            {
                                AssetPath = item.FullPath,
                                ExceptionType = typeof(IOException).FullName,
                                ExceptionMessage = "The hierarchy node could not be extracted by the configured container interop.",
                                ExtractionMilliseconds = extractionTimer.Elapsed.TotalMilliseconds
                            };
                        }
                        else
                        {
                            result = baseForm.LoadFileForAssetOpenStressTest(extractedPath, containerForm);
                            result.AssetPath = item.FullPath;
                            result.ExtractedPath = extractedPath;
                            result.ExtractionMilliseconds = extractionTimer.Elapsed.TotalMilliseconds;
                        }
                        result.FinishStatus();
                    }
                    catch (Exception exception)
                    {
                        extractionTimer.Stop();
                        result = new AssetOpenStressResult
                        {
                            AssetPath = item.FullPath,
                            ExceptionType = exception.GetType().FullName,
                            ExceptionMessage = exception.Message,
                            ExceptionStackTrace = exception.StackTrace,
                            ExtractionMilliseconds = extractionTimer.Elapsed.TotalMilliseconds
                        };
                        result.FinishStatus();
                    }

                    results.WriteLine(JsonConvert.SerializeObject(result, Formatting.None));
                    summary.AttemptedAssets++;
                    if (result.Status == "ok") summary.OkAssets++;
                    else if (result.Status == "notice") summary.NoticeAssets++;
                    else summary.FailedAssets++;

                    summary.ElapsedSeconds = previousElapsedSeconds + totalTimer.Elapsed.TotalSeconds;
                    WriteSummary(summaryPath, summary);
                }

                summary.Status = "complete";
                summary.CompletedAtUtc = DateTime.UtcNow.ToString("o");
                summary.ElapsedSeconds = previousElapsedSeconds + totalTimer.Elapsed.TotalSeconds;
                WriteSummary(summaryPath, summary);
                return summary.FailedAssets == 0 ? 0 : 2;
            }
            catch (Exception exception)
            {
                summary.Status = "fatal";
                summary.CompletedAtUtc = DateTime.UtcNow.ToString("o");
                summary.FatalExceptionType = exception.GetType().FullName;
                summary.FatalExceptionMessage = exception.Message;
                summary.ElapsedSeconds = previousElapsedSeconds + totalTimer.Elapsed.TotalSeconds;
                WriteSummary(summaryPath, summary);
                return 1;
            }
        }

        private static EngineVersion ParseEngineVersion(string text)
        {
            if (int.TryParse(text, out int rawVersion)) return EngineVersion.VER_UE4_0 + rawVersion;
            string enumName = text.Contains('.') ? "VER_UE" + text.Replace('.', '_') : text;
            if (enumName.StartsWith("UE", StringComparison.OrdinalIgnoreCase)) enumName = "VER_" + enumName;
            return Enum.TryParse(enumName, true, out EngineVersion version) ? version : EngineVersion.UNKNOWN;
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

        private static string GetFileSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static void WriteSummary(string path, AssetOpenStressSummary summary)
        {
            string temporaryPath = path + ".new-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(summary, Formatting.Indented) + Environment.NewLine);
            try
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(temporaryPath, path, true);
                        return;
                    }
                    catch (Exception exception) when (
                        attempt < 10 && (exception is IOException || exception is UnauthorizedAccessException))
                    {
                        Thread.Sleep(50 * attempt);
                    }
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void WriteEmergencyError(string message)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "UAssetGUI-stress-error.txt"), message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
