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
using System.Text;

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
                var reporter = BuildReporter();

                var filePath = ResolveFilePath(args, reporter);
                reporter.Info($"Scanning {filePath} on {Environment.MachineName}");
                if (string.IsNullOrWhiteSpace(filePath) || !Directory.Exists(filePath))
                {
                    reporter.BuildProblem("nugetscanner_path_missing",
                        $"Scan path '{filePath}' does not exist on {Environment.MachineName}");
                    Environment.Exit((int)ExitCode.DirectoryToScanDoesNotExist);
                }

                var packageReferences = new List<PackageRef>();
                var hasIssues = false;
                var issueCount = 0;

                reporter.Info("Scanning for packages.config files...");
                foreach (var configFile in Directory.GetFiles(filePath, "packages.config", SearchOption.AllDirectories))
                {
                    reporter.Debug($"Found: {configFile}");
                    packageReferences.AddRange(ReadPackagesConfig(configFile));
                }

                reporter.Info("Scanning for .csproj/.vbproj files...");
                foreach (var projectFile in Directory.GetFiles(filePath, "*.*proj", SearchOption.AllDirectories)
                             .Where(file => file.EndsWith(".csproj") || file.EndsWith(".vbproj")))
                {
                    reporter.Debug($"Found: {projectFile}");
                    packageReferences.AddRange(ReadProjectFile(projectFile));
                }

                var distinctPackages = packageReferences.Distinct().ToList();
                reporter.Info($"Found {distinctPackages.Count} unique packages.");

                foreach (var pkg in distinctPackages)
                {
                    var status = QueryNuGet(pkg.Id, pkg.Version);
                    reporter.Info(
                        $"{pkg.Id} {pkg.Version} => {(status.IsVulnerable ? "[VULNERABLE]" : "")} {(status.IsDeprecated ? "[DEPRECATED]" : "")}");

                    if (status.IsDeprecated || status.IsVulnerable)
                    {
                        hasIssues = true;
                        issueCount++;
                        var reasons = new List<string>();
                        if (status.IsVulnerable) reasons.Add("vulnerable");
                        if (status.IsDeprecated) reasons.Add("deprecated");
                        reporter.BuildProblem(
                            $"nugetscanner_{pkg.Id}_{pkg.Version}".ToLowerInvariant(),
                            $"Package {pkg.Id} {pkg.Version} is {string.Join(" and ", reasons)}");
                    }
                }


                reporter.Info("Scan complete.");

                if (hasIssues)
                {
                    reporter.BuildStatusFailure($"NuGetScanner found {issueCount} package issue(s).");
                }

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
            [Option('f', "filePath", Required = false, HelpText = "File Path to the project you would like to scan")]
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

        private static string ResolveFilePath(string[] args, IReporter reporter)
        {
            string filePath = null;

            Parser.Default.ParseArguments<Options>(args).WithParsed(o => { filePath = o.FilePath; });

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                return Path.GetFullPath(filePath);
            }

            var teamcityPath = Environment.GetEnvironmentVariable("TEAMCITY_BUILD_CHECKOUTDIR");
            if (!string.IsNullOrWhiteSpace(teamcityPath))
            {
                reporter.Info($"No filePath provided, defaulting to TeamCity checkout directory '{teamcityPath}'.");
                return Path.GetFullPath(teamcityPath);
            }

            reporter.Info("No filePath provided, defaulting to current directory.");
            return Directory.GetCurrentDirectory();
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

        private static IReporter BuildReporter()
        {
            var reporters = new List<IReporter>
            {
                new LogReporter(Log)
            };

            if (TeamCityReporter.IsTeamCity)
            {
                reporters.Add(new TeamCityReporter());
            }

            return new CompositeReporter(reporters);
        }
    }

    internal interface IReporter
    {
        void Info(string message);
        void Debug(string message);
        void BuildProblem(string identity, string description);
        void BuildStatusFailure(string text);
    }

    internal class CompositeReporter : IReporter
    {
        private readonly IEnumerable<IReporter> _reporters;

        public CompositeReporter(IEnumerable<IReporter> reporters)
        {
            _reporters = reporters;
        }

        public void Info(string message)
        {
            foreach (var reporter in _reporters) reporter.Info(message);
        }

        public void Debug(string message)
        {
            foreach (var reporter in _reporters) reporter.Debug(message);
        }

        public void BuildProblem(string identity, string description)
        {
            foreach (var reporter in _reporters) reporter.BuildProblem(identity, description);
        }

        public void BuildStatusFailure(string text)
        {
            foreach (var reporter in _reporters) reporter.BuildStatusFailure(text);
        }
    }

    internal class LogReporter : IReporter
    {
        private readonly ILog _log;

        public LogReporter(ILog log)
        {
            _log = log;
        }

        public void Info(string message) => _log.Info(message);
        public void Debug(string message) => _log.Debug(message);

        public void BuildProblem(string identity, string description)
        {
            _log.Error($"{identity}: {description}");
        }

        public void BuildStatusFailure(string text)
        {
            _log.Error(text);
        }
    }

    internal class TeamCityReporter : IReporter
    {
        public static bool IsTeamCity => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEAMCITY_VERSION"));

        public void Info(string message)
        {
            Console.WriteLine($"##teamcity[message text='{Escape(message)}' status='NORMAL']");
        }

        public void Debug(string message)
        {
            Console.WriteLine($"##teamcity[message text='{Escape(message)}' status='NORMAL']");
        }

        public void BuildProblem(string identity, string description)
        {
            Console.WriteLine($"##teamcity[buildProblem identity='{Escape(identity)}' description='{Escape(description)}']");
        }

        public void BuildStatusFailure(string text)
        {
            Console.WriteLine($"##teamcity[buildStatus status='FAILURE' text='{Escape(text)}']");
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '|':
                        builder.Append("||");
                        break;
                    case '\'':
                        builder.Append("|'");
                        break;
                    case '\n':
                        builder.Append("|n");
                        break;
                    case '\r':
                        builder.Append("|r");
                        break;
                    case '[':
                        builder.Append("|[");
                        break;
                    case ']':
                        builder.Append("|]");
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
