# NuGet Vulnerability and Deprecation Scanner

A .NET Framework console tool that scans `.NET` projects (`.csproj`, `.vbproj`, `packages.config`) for NuGet package dependencies, and checks them for:

-  Known vulnerabilities (via NuGet's registration API)
-  Deprecation notices (via `PackageDeprecationMetadataResource`)

This was created to gate build server builds with nuget package issues. The dotnet command line has a similar tool but it does not support non-embedded (packages.config). The dotnet cli also does not appear to support returning a different result code when package issues are seen so it is less ideal for build server automation.

## TeamCity build agent usage

- When the binary runs on a TeamCity agent (`TEAMCITY_VERSION` is set) it now emits TeamCity service messages for every vulnerable or deprecated package, plus a final failed build status summary.
- If no `--filePath` argument is provided, the tool defaults to the TeamCity checkout directory (`TEAMCITY_BUILD_CHECKOUTDIR`); otherwise it accepts the same optional `-f|--filePath` override.
- Build problems are surfaced with identities like `nugetscanner_{package}_{version}` so they appear directly in the build log and Build Problems tab.
- Exit codes remain unchanged (0 success, 10 missing directory, 20 issues found, 30 unexpected error) so existing command-line usage still works.

### Packaging as a TeamCity plugin (agent-only)

- Plugin archive layout produced by CI/Release pipelines:
  ```
  plugin.zip
    teamcity-plugin.xml      (server descriptor)
    agent/
      NugetScanner-agent.zip
        teamcity-plugin.xml  (agent descriptor, tool deployment)
        lib/
          NugetScanner.exe + deps
  ```
- Manual build steps (if not using GitHub Actions):
  1. Build Release: `msbuild NugetScanner.csproj /p:Configuration=Release`. This now also writes the payload to `NugetScanner\bin\Release\agent\lib\`.
  2. After the build, a ready-to-zip staging folder is produced at `NugetScanner\bin\Release\plugin-package\` containing:
     - `teamcity-plugin.xml` (server descriptor)
     - `agent/NugetScanner-agent.zip` with its own `teamcity-plugin.xml` (agent descriptor) and `lib/*`.
  3. Zip the *contents* of `NugetScanner\bin\Release\plugin-package\` (not the folder itself) to `NugetScanner-teamcity-plugin.zip`.
  4. Upload the ZIP under Administration → Plugins List → Upload plugin zip on your TeamCity server.
     The plugin is agent-only; no server UI is required.

## Usage in a TeamCity build

1. Install the plugin ZIP in TeamCity (Administration → Plugins List → Upload plugin zip) and let agents restart.
2. In the build configuration, add a Command Line build step before tests/packages:
   ```
   "%teamcity.tool.NugetScanner%\NugetScanner.exe" -f "%teamcity.build.checkoutDir%"
   ```
   You may omit `-f` to let the tool default to `TEAMCITY_BUILD_CHECKOUTDIR`.
3. Keep “Fail build on non-zero exit code” enabled. The scanner exits non-zero when it finds vulnerable or deprecated packages (exit code 20) or on errors (exit code 30).
4. Results:
   - Vulnerable or deprecated packages are reported as TeamCity build problems with IDs `nugetscanner_{package}_{version}`.
   - The build status is set to failure when issues are found.
5. Optional: add the step to a template and apply it only to projects/builds that should be scanned.
