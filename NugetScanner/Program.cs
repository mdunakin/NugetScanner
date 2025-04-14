using CommandLine;
using log4net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Threading;
using System.Configuration;

namespace NugetScanner
{
    internal class Program
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Program));

        static void Main(string[] args)
        {
            try
            {
                log4net.Config.XmlConfigurator.Configure();
                var filePath = "";
                Parser.Default.ParseArguments<Options>(args).WithParsed(o => { filePath = o.FilePath; });
                Log.Info($"Scanning {filePath} on {Environment.MachineName}");
                if (!Directory.Exists(filePath))
                {
                    Log.Error($"file path {filePath} does not exist on machine {Environment.MachineName}");
                    Environment.Exit((int)ExitCode.DirectoryToScanDoesNotExist);
                }

                var packageReferences = new List<PackageRef>();
                var hasIssues = false;

                Log.Info("Scanning for packages.config files...");
                foreach (var configFile in Directory.GetFiles(filePath, "packages.config", SearchOption.AllDirectories))
                {
                    Log.Debug($"Found: {configFile}");
                    packageReferences.AddRange(ReadPackagesConfig(configFile));
                }

                Log.Info("Scanning for .csproj/.vbproj files...");
                foreach (var projectFile in Directory.GetFiles(filePath, "*.*proj", SearchOption.AllDirectories)
                             .Where(file => file.EndsWith(".csproj") || file.EndsWith(".vbproj")))
                {
                    Log.Debug($"Found: {projectFile}");
                    packageReferences.AddRange(ReadProjectFile(projectFile));
                }

                var distinctPackages = packageReferences.Distinct().ToList();
                Log.Info($"Found {distinctPackages.Count} unique packages.");

                foreach (var pkg in distinctPackages)
                {
                    var status = QueryNuGet(pkg.Id, pkg.Version);
                    Log.Info(
                        $"{pkg.Id} {pkg.Version} => {(status.IsVulnerable ? "[VULNERABLE]" : "")} {(status.IsDeprecated ? "[DEPRECATED]" : "")}");

                    if (status.IsDeprecated || status.IsVulnerable)
                    {
                        hasIssues = true;
                    }
                }


                Log.Info("Scan complete.");

                var exitCode = hasIssues ? (int)ExitCode.DeprecatedOrVulnerable : (int)ExitCode.Success;
                Environment.Exit(exitCode);
            }
            catch (Exception e)
            {
                Log.Error("error", e);
                Environment.Exit((int)ExitCode.Error);
            }


        }

        private enum ExitCode
        {
            Success = 0,
            DirectoryToScanDoesNotExist = 10,
            DeprecatedOrVulnerable = 20,
            Error = 30
        }

        public class Options
        {
            [Option('f', "filePath", Required = true, HelpText = "File Path to the project you would like to scan")]
            public string FilePath { get; set; }
        }

        private class PackageStatus
        {
            public bool IsDeprecated { get; set; }
            public bool IsVulnerable { get; set; }

        }


        private class PackageRef
        {
            public string Id { get; set; }
            public string Version { get; set; }

        }


        private static List<PackageRef> ReadPackagesConfig(string path)
        {
            var document = XDocument.Load(path);
            return document.Descendants("package")
                .Select(x => new PackageRef
                {
                    Id = x.Attribute("id")?.Value,
                    Version = x.Attribute("version")?.Value
                })
                .Where(p => !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(p.Version))
                .ToList();
        }

        private static List<PackageRef> ReadProjectFile(string path)
        {
            var document = XDocument.Load(path);
            var defaultNamespace = document.Root?.GetDefaultNamespace() ?? "";
            return document.Descendants(defaultNamespace + "PackageReference")
                .Select(xElement => new PackageRef
                {
                    Id = xElement.Attribute("Include")?.Value,
                    Version = xElement.Attribute("Version")?.Value ?? xElement.Element(defaultNamespace + "Version")?.Value
                })
                .Where(packageRef => !string.IsNullOrEmpty(packageRef.Id) && !string.IsNullOrEmpty(packageRef.Version))
                .ToList();
        }


        private static PackageStatus QueryNuGet(string packageId, string version)
        {
            var logger = NullLogger.Instance;
            var cache = new SourceCacheContext();

            var baseSource = ConfigurationManager.AppSettings["NuGetBaseUrl"] ?? "https://api.nuget.org/v3/index.json";

            var repo = Repository.Factory.GetCoreV3(baseSource);
            var metadataResource = repo.GetResourceAsync<PackageMetadataResource>().GetAwaiter().GetResult();

            var nugetVersion = NuGetVersion.Parse(version);
            var metadataList = metadataResource
                .GetMetadataAsync(packageId, includePrerelease: true, includeUnlisted: true, cache, logger,
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            var match = metadataList.FirstOrDefault(m => m.Identity.Version == nugetVersion);
            if (match == null)
            {

                return new PackageStatus();
            }

            var deprecated = match.GetDeprecationMetadataAsync().GetAwaiter().GetResult();
            var status = new PackageStatus();
            if (deprecated != null)
            {
                status.IsDeprecated = true;
            }

            if (match.Vulnerabilities != null)
            {
                status.IsVulnerable = true;
            }

            return status;
        }
    }
}
