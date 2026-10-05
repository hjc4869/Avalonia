# Building NuGet Packages

To build NuGet packages, one can use the `CreateNugetPackages` target:

Windows

```
.\build.ps1 CreateNugetPackages
```

Linux/macOS

```
./build.sh CreateNugetPackages
```

Or if you have Nuke's [dotnet global tool](https://nuke.build/docs/getting-started/installation/) installed:

```
nuke CreateNugetPackages
```

The produced NuGet packages will be placed in the `artifacts\nuget` directory.

> [!NOTE]
> The rest of this document will assume that you have the Nuke global tool installed, as the invocation is the same on all platforms. You can always replace `nuke` in the instructions below with the `build` script relvant to your platform.

By default the packages will be built in debug configuration. To build in relase configuration add the `--configuration` parameter, e.g.:

```
nuke CreateNugetPackages --configuration Release
```

To configure the version of the built packages, add the `--force-nuget-version` parameter, e.g.:

```
nuke CreateNugetPackages --force-nuget-version 12.1.4-lightplayer.14
```

An explicit version is used unchanged, including in CI; no CI suffix is added.

## Downloading CI Packages

The [Build workflow](../.github/workflows/build.yml) publishes downloadable files instead of uploading packages to a NuGet feed.

1. Open **Actions > Build > Run workflow** and select the branch to build.
2. Set the optional **version** input, for example `12.1.4-lightplayer.14`. Leave it empty to keep automatic versioning.
3. After the workflow succeeds, download and extract the **packages** artifact from the run's **Artifacts** section.

The archive contains the Windows-built NuGet packages plus the macOS-built `Avalonia.Native` package and their symbol packages. The `native-macos`, `sbom-macos`, and `sbom-windows` artifacts remain available separately. Artifacts follow the repository's retention settings.

Add the extracted package directory as a local NuGet source in the consuming environment:

```bash
dotnet nuget add source /absolute/path/to/extracted/packages --name avalonia-local
```

Then reference the generated version in the consuming project's Avalonia package references. Pull request builds also receive a comment linking to their artifacts.

## Building to the Local Cache

Building packages with the `CreateNugetPackages` target has a few gotchas:

- One needs to set up a local nuget feed to consume the packages
- When building on an operating system other than macOS, the Avalonia.Native package will not be built, resulting in a NuGet error when trying to use Avalonia.Desktop
- It's easy to introduce versioning problems

For these reasons, it is possible to build Avalonia directly to your machine's NuGet cache using the `BuildToNuGetCache` target:

```bash
nuke --target BuildToNuGetCache --configuration Release
```

This command will generate nuget packages and push them into your local NuGet cache (usually `~/.nuget/packages`) with a version of  `9999.0.0-localbuild`.

Each time local changes are made to Avalonia, running this command again will replace the old packages and reset the cache, meaning that the changes should be picked up automatically by msbuild.
