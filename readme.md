# NuGet Vulnerability and Deprecation Scanner

A .NET Framework console tool that scans `.NET` projects (`.csproj`, `.vbproj`, `packages.config`) for NuGet package dependencies, and checks them for:

- 🔒 Known vulnerabilities (via NuGet's registration API)
- ⚠️ Deprecation notices (via `PackageDeprecationMetadataResource`)

This was created to gate build server builds with nuget package issues. The dotnet cli has a similar tool but it does not support non-embedded (packages.config). The dotnet cli also does not appear to support returning a different result code when package issues are seen so it is less ideal for build server automation.