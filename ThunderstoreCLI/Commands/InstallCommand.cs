using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ThunderstoreCLI.Configuration;
using ThunderstoreCLI.Game;
using ThunderstoreCLI.Models;
using ThunderstoreCLI.Utils;

namespace ThunderstoreCLI.Commands;

public static partial class InstallCommand
{
    // will match either ab-cd or ab-cd-123.456.7890
    internal static Regex FullPackageNameRegex = new Regex(@"^(?<fullname>(?<namespace>[\w-\.]+)-(?<name>\w+))(?:|-(?<version>\d+\.\d+\.\d+))$");

    public static async Task<int> Run(Config config)
    {
        var defCollection = GameDefinitionCollection.FromDirectory(config.GeneralConfig.TcliConfig);
        var defs = defCollection.List;
        GameDefinition? def = defs.FirstOrDefault(x => x.Identifier == config.ModManagementConfig.GameIdentifer);
        if (def == null)
        {
            Write.ErrorExit($"Not configured for the game: {config.ModManagementConfig.GameIdentifer}");
            return 1;
        }

        ModProfile? profile = def.Profiles.FirstOrDefault(x => x.Name == config.ModManagementConfig.ProfileName);
        profile ??= new ModProfile(def, config.ModManagementConfig.ProfileName!, config.GeneralConfig.TcliConfig);

        string package = config.ModManagementConfig.Package!;

        HttpClient http = new();

        int returnCode;
        Match packageMatch = FullPackageNameRegex.Match(package);
        if (File.Exists(package))
        {
            returnCode = await InstallZip(config, http, def, profile, package, null, null);
        }
        else if (packageMatch.Success)
        {
            returnCode = await InstallFromRepository(config, http, def, profile, packageMatch);
        }
        else
        {
            throw new CommandFatalException($"Package given does not exist as a zip and is not a valid package identifier (namespace-name): {package}");
        }

        if (returnCode == 0)
            defCollection.Write();

        return returnCode;
    }

    private static async Task<int> InstallFromRepository(Config config, HttpClient http, GameDefinition game, ModProfile profile, Match packageMatch)
    {
        PackageVersionData? versionData = null;
        Write.Light($"Downloading main package: {packageMatch.Groups["fullname"].Value}");

        var ns = packageMatch.Groups["namespace"];
        var name = packageMatch.Groups["name"];
        var version = packageMatch.Groups["version"];
        if (version.Success)
        {
            var versionResponse = await http.SendAsync(config.Api.GetPackageVersionMetadata(ns.Value, name.Value, version.Value));
            versionResponse.EnsureSuccessStatusCode();
            versionData = (await PackageVersionData.DeserializeAsync(await versionResponse.Content.ReadAsStreamAsync()))!;
        }
        var packageResponse = await http.SendAsync(config.Api.GetPackageMetadata(ns.Value, name.Value));
        packageResponse.EnsureSuccessStatusCode();
        var packageData = await PackageData.DeserializeAsync(await packageResponse.Content.ReadAsStreamAsync());

        versionData ??= packageData!.LatestVersion!;

        var zipPath = await config.Cache.GetFileOrDownload($"{versionData.FullName}.zip", versionData.DownloadUrl!);
        var returnCode = await InstallZip(config, http, game, profile, zipPath, versionData.Namespace!, packageData!.CommunityListings!.First().Community);
        return returnCode;
    }

    private static async Task<int> InstallZip(Config config, HttpClient http, GameDefinition game, ModProfile profile, string zipPath, string? backupNamespace, string? sourceCommunity)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifestFile = zip.GetEntry("manifest.json") ?? throw new CommandFatalException("Package zip needs a manifest.json!");
        var manifest = await PackageManifestV1.DeserializeAsync(manifestFile.Open())
            ?? throw new CommandFatalException("Package manifest.json is invalid! Please check against https://thunderstore.io/tools/manifest-v1-validator/");

        manifest.Namespace ??= backupNamespace;

        var dependenciesToInstall = ModDependencyTree.Generate(config, http, manifest, sourceCommunity)
            .Where(dependency => !profile.InstalledModVersions.ContainsKey(dependency.Fullname!))
            .ToArray();

        if (dependenciesToInstall.Length > 0)
        {
            var totalSize = MiscUtils.GetSizeString(dependenciesToInstall.Select(d => d.Versions![0].FileSize).Sum());
            Write.Light($"Total estimated download size: ");

            var downloadTasks = dependenciesToInstall.Select(mod =>
            {
                var version = mod.Versions![0];
                return config.Cache.GetFileOrDownload($"{mod.Fullname}-{version.VersionNumber}.zip", version.DownloadUrl!);
            }).ToArray();

            var spinner = new ProgressSpinner("dependencies downloaded", downloadTasks);
            await spinner.Spin();

            foreach (var (tempZipPath, package) in downloadTasks.Select(x => x.Result).Zip(dependenciesToInstall))
            {
                var packageVersion = package.Versions![0];
                int returnCode = RunInstaller(game, profile, tempZipPath, package.Owner);
                if (returnCode == 0)
                {
                    Write.Success($"Installed mod: {package.Fullname}-{packageVersion.VersionNumber}");
                }
                else
                {
                    Write.Error($"Failed to install mod: {package.Fullname}-{packageVersion.VersionNumber}");
                    return returnCode;
                }
                profile.InstalledModVersions[package.Fullname!] = new PackageManifestV1(package, packageVersion);
            }
        }

        var exitCode = RunInstaller(game, profile, zipPath, backupNamespace);
        if (exitCode == 0)
        {
            profile.InstalledModVersions[manifest.FullName] = manifest;
            Write.Success($"Installed mod: {manifest.FullName}-{manifest.VersionNumber}");
        }
        else
        {
            Write.Error($"Failed to install mod: {manifest.FullName}-{manifest.VersionNumber}");
        }
        return exitCode;
    }

    // TODO: conflict handling
    private static int RunInstaller(GameDefinition game, ModProfile profile, string zipPath, string? backupNamespace)
    {
        // TODO: how to decide which installer to run?
        string installerName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "tcli-bepinex-installer.exe" : "tcli-bepinex-installer";
        var bepinexInstallerPath = Path.Combine(AppContext.BaseDirectory, installerName);

        ProcessStartInfo installerInfo = new(bepinexInstallerPath)
        {
            ArgumentList =
            {
                "install",
                game.InstallDirectory,
                profile.ProfileDirectory,
                zipPath
            },
            RedirectStandardError = true
        };
        if (backupNamespace != null)
        {
            installerInfo.ArgumentList.Add("--namespace-backup");
            installerInfo.ArgumentList.Add(backupNamespace);
        }

        var installerProcess = Process.Start(installerInfo)!;
        installerProcess.WaitForExit();

        string errors = installerProcess.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(errors))
        {
            Write.Error(errors);
        }

        return installerProcess.ExitCode;
    }
}