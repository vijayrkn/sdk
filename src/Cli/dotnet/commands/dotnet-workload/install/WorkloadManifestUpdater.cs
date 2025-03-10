// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Cli.NuGetPackageDownloader;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Configurer;
using Microsoft.DotNet.MSBuildSdkResolver;
using Microsoft.DotNet.ToolPackage;
using Microsoft.DotNet.Workloads.Workload.Install.InstallRecord;
using Microsoft.Extensions.EnvironmentAbstractions;
using Microsoft.NET.Sdk.WorkloadManifestReader;
using NuGet.Common;
using NuGet.Versioning;

namespace Microsoft.DotNet.Workloads.Workload.Install
{
    internal class WorkloadManifestUpdater : IWorkloadManifestUpdater
    {
        private readonly IReporter _reporter;
        private readonly IWorkloadResolver _workloadResolver;
        private readonly INuGetPackageDownloader _nugetPackageDownloader;
        private readonly SdkFeatureBand _sdkFeatureBand;
        private readonly string _userProfileDir;
        private readonly string _tempDirPath;
        private readonly PackageSourceLocation _packageSourceLocation;
        Func<string, string> _getEnvironmentVariable;
        private readonly IWorkloadInstallationRecordRepository _workloadRecordRepo;
        private readonly bool _displayManifestUpdates;

        public WorkloadManifestUpdater(IReporter reporter,
            IWorkloadResolver workloadResolver,
            INuGetPackageDownloader nugetPackageDownloader,
            string userProfileDir,
            string tempDirPath,
            IWorkloadInstallationRecordRepository workloadRecordRepo,
            PackageSourceLocation packageSourceLocation = null,
            Func<string, string> getEnvironmentVariable = null,
            bool displayManifestUpdates = true)
        {
            _reporter = reporter;
            _workloadResolver = workloadResolver;
            _userProfileDir = userProfileDir;
            _tempDirPath = tempDirPath;
            _nugetPackageDownloader = nugetPackageDownloader;
            _sdkFeatureBand = new SdkFeatureBand(_workloadResolver.GetSdkFeatureBand());
            _packageSourceLocation = packageSourceLocation;
            _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
            _workloadRecordRepo = workloadRecordRepo;
            _displayManifestUpdates = displayManifestUpdates;
        }

        private static WorkloadManifestUpdater GetInstance(string userProfileDir)
        {
            var reporter = new NullReporter();
            var dotnetPath = Path.GetDirectoryName(Environment.ProcessPath);
            var sdkVersion = Product.Version;
            var workloadManifestProvider = new SdkDirectoryWorkloadManifestProvider(dotnetPath, sdkVersion, userProfileDir);
            var workloadResolver = WorkloadResolver.Create(workloadManifestProvider, dotnetPath, sdkVersion, userProfileDir);
            var tempPackagesDir = new DirectoryPath(Path.Combine(Path.GetTempPath(), "dotnet-sdk-advertising-temp"));
            var nugetPackageDownloader = new NuGetPackageDownloader(tempPackagesDir,
                                          filePermissionSetter: null,
                                          new FirstPartyNuGetPackageSigningVerifier(tempPackagesDir, new NullLogger()),
                                          new NullLogger(),
                                          reporter,
                                          verifySignatures: SignCheck.IsDotNetSigned());
            var workloadRecordRepo = WorkloadInstallerFactory.GetWorkloadInstaller(reporter, new SdkFeatureBand(sdkVersion),
                workloadResolver, Cli.VerbosityOptions.normal, userProfileDir, verifySignatures: false)
                .GetWorkloadInstallationRecordRepository();

            return new WorkloadManifestUpdater(reporter, workloadResolver, nugetPackageDownloader, userProfileDir, tempPackagesDir.Value, workloadRecordRepo);
        }

        public async Task UpdateAdvertisingManifestsAsync(bool includePreviews, DirectoryPath? offlineCache = null)
        {
            // this updates all the manifests 
            var manifests = _workloadResolver.GetInstalledManifests();
            await Task.WhenAll(manifests.Select(manifest => UpdateAdvertisingManifestAsync(manifest, includePreviews, offlineCache)))
                .ConfigureAwait(false);
            WriteUpdatableWorkloadsFile();
        }

        public async static Task BackgroundUpdateAdvertisingManifestsAsync(string userProfileDir)
        {
            try
            {
                var manifestUpdater = WorkloadManifestUpdater.GetInstance(userProfileDir);
                await manifestUpdater.BackgroundUpdateAdvertisingManifestsWhenRequiredAsync();
            }
            catch (Exception)
            {
                // Never surface messages on background updates
            }
        }

        public async Task BackgroundUpdateAdvertisingManifestsWhenRequiredAsync()
        {
            if (!BackgroundUpdatesAreDisabled() &&
                AdManifestSentinelIsDueForUpdate() &&
                UpdatedAdManifestPackagesExistAsync().GetAwaiter().GetResult())
            {
                await UpdateAdvertisingManifestsAsync(false);
                var sentinelPath = GetAdvertisingManifestSentinelPath(_sdkFeatureBand);
                if (File.Exists(sentinelPath))
                {
                    File.SetLastAccessTime(sentinelPath, DateTime.Now);
                }
                else
                {
                    File.Create(sentinelPath).Close();
                }
            }
        }

        private void WriteUpdatableWorkloadsFile()
        {
            var installedWorkloads = _workloadRecordRepo.GetInstalledWorkloads(_sdkFeatureBand);
            var updatableWorkloads = GetUpdatableWorkloadsToAdvertise(installedWorkloads);
            var filePath = GetAdvertisingWorkloadsFilePath(_sdkFeatureBand);
            var jsonContent = JsonSerializer.Serialize(updatableWorkloads.Select(workload => workload.ToString()).ToArray());
            if (Directory.Exists(Path.GetDirectoryName(filePath)))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            }
            File.WriteAllText(filePath, jsonContent);
        }

        public void DeleteUpdatableWorkloadsFile()
        {
            var filePath = GetAdvertisingWorkloadsFilePath(_sdkFeatureBand);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }

        public static void AdvertiseWorkloadUpdates()
        {
            try
            {
                var backgroundUpdatesDisabled = bool.TryParse(Environment.GetEnvironmentVariable(EnvironmentVariableNames.WORKLOAD_UPDATE_NOTIFY_DISABLE), out var disableEnvVar) && disableEnvVar;
                SdkFeatureBand featureBand = new SdkFeatureBand(Product.Version);
                var adUpdatesFile = GetAdvertisingWorkloadsFilePath(CliFolderPathCalculator.DotnetUserProfileFolderPath, featureBand);
                if (!backgroundUpdatesDisabled && File.Exists(adUpdatesFile))
                {
                    var updatableWorkloads = JsonSerializer.Deserialize<string[]>(File.ReadAllText(adUpdatesFile));
                    if (updatableWorkloads != null && updatableWorkloads.Any())
                    {
                        Console.WriteLine();
                        Console.WriteLine(LocalizableStrings.WorkloadUpdatesAvailable);
                    }
                }
            }
            catch (Exception)
            {
                // Never surface errors
            }
        }

        public IEnumerable<(
            ManifestVersionUpdate manifestUpdate,
            Dictionary<WorkloadId, WorkloadDefinition> Workloads
            )>
            CalculateManifestUpdates()
        {
            var manifestUpdates =
                new List<(ManifestVersionUpdate manifestUpdate,
                    Dictionary<WorkloadId, WorkloadDefinition> Workloads)>();
            var currentManifestIds = GetInstalledManifestIds();
            foreach (var manifestId in currentManifestIds)
            {
                var currentManifestVersion = GetInstalledManifestVersion(manifestId);
                var advertisingManifestVersionAndWorkloads = GetAdvertisingManifestVersionAndWorkloads(manifestId);
                if (advertisingManifestVersionAndWorkloads == null)
                {
                    continue;
                }

                if (advertisingManifestVersionAndWorkloads != null &&
                    ((advertisingManifestVersionAndWorkloads.Value.ManifestVersion.CompareTo(currentManifestVersion.manifestVersion) > 0 
                        && advertisingManifestVersionAndWorkloads.Value.ManifestFeatureBand.Equals(currentManifestVersion.sdkFeatureBand)) ||
                    advertisingManifestVersionAndWorkloads.Value.ManifestFeatureBand.CompareTo(currentManifestVersion.sdkFeatureBand) > 0)) 
                {
                    manifestUpdates.Add((new ManifestVersionUpdate(manifestId, currentManifestVersion.manifestVersion, currentManifestVersion.sdkFeatureBand.ToString(),
                        advertisingManifestVersionAndWorkloads.Value.ManifestVersion, advertisingManifestVersionAndWorkloads.Value.ManifestFeatureBand.ToString()),
                        advertisingManifestVersionAndWorkloads.Value.Workloads));
                }
            }

            return manifestUpdates;
        }

        public IEnumerable<WorkloadId> GetUpdatableWorkloadsToAdvertise(IEnumerable<WorkloadId> installedWorkloads)
        {
            try
            {
                var overlayProvider = new TempDirectoryWorkloadManifestProvider(Path.Combine(_userProfileDir, "sdk-advertising", _sdkFeatureBand.ToString()), _sdkFeatureBand.ToString());
                var advertisingManifestResolver = _workloadResolver.CreateOverlayResolver(overlayProvider);
                return _workloadResolver.GetUpdatedWorkloads(advertisingManifestResolver, installedWorkloads);
            }
            catch
            {
                return Array.Empty<WorkloadId>();
            }
        }

        public IEnumerable<ManifestVersionUpdate> CalculateManifestRollbacks(string rollbackDefinitionFilePath)
        {
            var currentManifestIds = GetInstalledManifestIds();
            var manifestRollbacks = ParseRollbackDefinitionFile(rollbackDefinitionFilePath);

            var unrecognizedManifestIds = manifestRollbacks.Where(rollbackManifest => !currentManifestIds.Contains(rollbackManifest.Item1));
            if (unrecognizedManifestIds.Any())
            {
                _reporter.WriteLine(string.Format(LocalizableStrings.RollbackDefinitionContainsExtraneousManifestIds, rollbackDefinitionFilePath, string.Join(" ", unrecognizedManifestIds)).Yellow());
                manifestRollbacks = manifestRollbacks.Where(rollbackManifest => currentManifestIds.Contains(rollbackManifest.Item1));
            }

            var manifestUpdates = manifestRollbacks
                .Select(manifest =>
                {
                    var installedManifestInfo = GetInstalledManifestVersion(manifest.id);
                    return new ManifestVersionUpdate(manifest.id, installedManifestInfo.manifestVersion, installedManifestInfo.sdkFeatureBand.ToString(),
                        manifest.version, manifest.featureBand.ToString());
                });

            return manifestUpdates;
        }

        public async Task<IEnumerable<string>> DownloadManifestPackagesAsync(bool includePreviews, DirectoryPath downloadPath)
        {
            var manifests = GetInstalledManifestIds();
            var packagePaths = new List<string>();
            foreach (var manifest in manifests)
            {
                try
                {
                    var packagePath = await _nugetPackageDownloader.DownloadPackageAsync(
                        GetManifestPackageId(_sdkFeatureBand, manifest),
                        packageSourceLocation: _packageSourceLocation,
                        includePreview: includePreviews, downloadFolder: downloadPath);
                    packagePaths.Add(packagePath);
                }
                catch (Exception)
                {
                    _reporter.WriteLine(string.Format(LocalizableStrings.FailedToDownloadPackageManifest, manifest));
                }
            }
            return packagePaths;
        }

        public async Task ExtractManifestPackagesToTempDirAsync(IEnumerable<string> manifestPackages, DirectoryPath tempDir)
        {
            Directory.CreateDirectory(tempDir.Value);
            foreach (var manifestPackagePath in manifestPackages)
            {
                var manifestId = Path.GetFileNameWithoutExtension(manifestPackagePath);
                var extractionDir = Path.Combine(tempDir.Value, manifestId);
                Directory.CreateDirectory(extractionDir);
                await _nugetPackageDownloader.ExtractPackageAsync(manifestPackagePath, new DirectoryPath(extractionDir));
                File.Copy(Path.Combine(extractionDir, "data", "WorkloadManifest.json"), Path.Combine(tempDir.Value, manifestId, "WorkloadManifest.json"));
            }
        }

        public IEnumerable<string> GetManifestPackageUrls(bool includePreviews)
        {
            var packageIds = GetInstalledManifestIds()
                .Select(manifestId => GetManifestPackageId(_sdkFeatureBand, manifestId));

            var packageUrls = new List<string>();
            foreach (var packageId in packageIds)
            {
                try
                {
                    packageUrls.Add(_nugetPackageDownloader.GetPackageUrl(packageId, packageSourceLocation: _packageSourceLocation, includePreview: includePreviews).GetAwaiter().GetResult());
                }
                catch
                {
                    _reporter.WriteLine(string.Format(LocalizableStrings.FailedToGetPackageManifestUrl, packageId));
                }
            }
            return packageUrls;
        }

        private IEnumerable<ManifestId> GetInstalledManifestIds()
        {
            return _workloadResolver.GetInstalledManifests().Select(manifest => new ManifestId(manifest.Id));
        }

        private async Task UpdateAdvertisingManifestAsync(WorkloadManifestInfo manifest, bool includePreviews, DirectoryPath? offlineCache = null)
        {
            string packagePath = null;
            string extractionPath = null;
            var manifestId = new ManifestId(manifest.Id);
            string currentFeatureBand = _sdkFeatureBand.ToString();

            try
            {
                var adManifestPath = GetAdvertisingManifestPath(_sdkFeatureBand, manifestId);
                
                bool success;
                (success, packagePath) = await GetManifestPackageUpdate(_sdkFeatureBand, manifestId, includePreviews, offlineCache);
                if (!success)
                {
                    if (!(manifest.ManifestFeatureBand).Equals(_sdkFeatureBand))
                    {
                        (success, packagePath) = await GetManifestPackageUpdate(new SdkFeatureBand(manifest.ManifestFeatureBand), manifestId, includePreviews, offlineCache);
                        currentFeatureBand = manifest.ManifestFeatureBand.ToString();
                    }
                }
                if (!success)
                {
                    _reporter.WriteLine(string.Format(LocalizableStrings.AdManifestPackageDoesNotExist, manifestId));
                    return;
                }
                
                extractionPath = Path.Combine(_tempDirPath, "dotnet-sdk-advertising-temp", $"{manifestId}-extracted");
                Directory.CreateDirectory(extractionPath);
                var resultingFiles = await _nugetPackageDownloader.ExtractPackageAsync(packagePath, new DirectoryPath(extractionPath));

                if (Directory.Exists(adManifestPath))
                {
                    Directory.Delete(adManifestPath, true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(adManifestPath));
                FileAccessRetrier.RetryOnMoveAccessFailure(() => DirectoryPath.MoveDirectory(Path.Combine(extractionPath, "data"), adManifestPath));

                // add file that contains the advertisted manifest feature band so GetAdvertisingManifestVersionAndWorkloads will use correct feature band, regardless of if rollback occurred or not
                File.WriteAllText(Path.Combine(adManifestPath, "AdvertisedManifestFeatureBand.txt"), currentFeatureBand);

                if (_displayManifestUpdates)
                {
                    _reporter.WriteLine(string.Format(LocalizableStrings.AdManifestUpdated, manifestId));
                }

            }
            catch (Exception e)
            {
                _reporter.WriteLine(string.Format(LocalizableStrings.FailedAdManifestUpdate, manifestId, e.Message));
            }
            finally
            {
                if (!string.IsNullOrEmpty(extractionPath) && Directory.Exists(extractionPath))
                {
                    Directory.Delete(extractionPath, true);
                }

                if (!string.IsNullOrEmpty(packagePath) && File.Exists(packagePath) && (offlineCache == null || !offlineCache.HasValue))
                {
                    File.Delete(packagePath);
                }
                if (!string.IsNullOrEmpty(packagePath) && (offlineCache == null || !offlineCache.HasValue))
                {
                    var versionDir = Path.GetDirectoryName(packagePath);

                    if (Directory.Exists(versionDir) && !Directory.GetFileSystemEntries(versionDir).Any())
                    {
                        Directory.Delete(versionDir);
                        var idDir = Path.GetDirectoryName(versionDir);
                        if (Directory.Exists(idDir) && !Directory.GetFileSystemEntries(idDir).Any())
                        {
                            Directory.Delete(idDir);
                        }
                    }
                }
            }
        }

        private (ManifestVersion ManifestVersion, SdkFeatureBand ManifestFeatureBand, Dictionary<WorkloadId, WorkloadDefinition> Workloads)?
            GetAdvertisingManifestVersionAndWorkloads(ManifestId manifestId)
        {
            var manifestPath = Path.Combine(GetAdvertisingManifestPath(_sdkFeatureBand, manifestId),
                "WorkloadManifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            using (FileStream fsSource = new FileStream(manifestPath, FileMode.Open, FileAccess.Read))
            {
                var manifest = WorkloadManifestReader.ReadWorkloadManifest(manifestId.ToString(), fsSource, manifestPath);
                // we need to know the feature band of the advertised manifest (read it from the AdvertisedManifestFeatureBand.txt file)
                // if we don't find the file then use the current feature band
                var adManifestFeatureBandPath = Path.Combine(GetAdvertisingManifestPath(_sdkFeatureBand, manifestId), "AdvertisedManifestFeatureBand.txt");

                SdkFeatureBand adManifestFeatureBand = _sdkFeatureBand;
                if (File.Exists(adManifestFeatureBandPath))
                {
                    adManifestFeatureBand = new SdkFeatureBand(File.ReadAllText(adManifestFeatureBandPath));
                }
                              

                return (new ManifestVersion(manifest.Version), adManifestFeatureBand, manifest.Workloads.Values.OfType<WorkloadDefinition>().ToDictionary(w => w.Id));
            }
        }

        private (ManifestVersion manifestVersion, SdkFeatureBand sdkFeatureBand) GetInstalledManifestVersion(ManifestId manifestId)
        {

            var manifest = _workloadResolver.GetInstalledManifests()
                .FirstOrDefault(manifest => manifest.Id.ToLowerInvariant().Equals(manifestId.ToString()));
            if (manifest == null)
            {
                throw new Exception(string.Format(LocalizableStrings.ManifestDoesNotExist, manifestId.ToString()));
            }
            return (new ManifestVersion(manifest.Version), new SdkFeatureBand(manifest.ManifestFeatureBand));
        }

        private bool AdManifestSentinelIsDueForUpdate()
        {
            var sentinelPath = GetAdvertisingManifestSentinelPath(_sdkFeatureBand);
            int updateIntervalHours;
            if (!int.TryParse(_getEnvironmentVariable(EnvironmentVariableNames.WORKLOAD_UPDATE_NOTIFY_INTERVAL_HOURS), out updateIntervalHours))
            {
                updateIntervalHours = 24;
            }

            if (File.Exists(sentinelPath))
            {
                var lastAccessTime = File.GetLastAccessTime(sentinelPath);
                if (lastAccessTime.AddHours(updateIntervalHours) > DateTime.Now)
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> UpdatedAdManifestPackagesExistAsync()
        {
            var manifests = GetInstalledManifestIds();
            var availableUpdates = await Task.WhenAll(manifests.Select(manifest => NewerManifestPackageExists(manifest)))
                .ConfigureAwait(false);
            return availableUpdates.Any();
        }

        private async Task<bool> NewerManifestPackageExists(ManifestId manifest)
        {
            try
            {
                var currentVersion = NuGetVersion.Parse(_workloadResolver.GetManifestVersion(manifest.ToString()));
                var latestVersion = await _nugetPackageDownloader.GetLatestPackageVerion(GetManifestPackageId(_sdkFeatureBand, manifest));
                return latestVersion > currentVersion;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private IEnumerable<(ManifestId id, ManifestVersion version, SdkFeatureBand featureBand)> ParseRollbackDefinitionFile(string rollbackDefinitionFilePath)
        {
            string fileContent;

            if (Uri.TryCreate(rollbackDefinitionFilePath, UriKind.Absolute, out var rollbackUri) && !rollbackUri.IsFile)
            {
                fileContent = (new HttpClient()).GetStringAsync(rollbackDefinitionFilePath).Result;
            }
            else
            {
                if (File.Exists(rollbackDefinitionFilePath))
                {
                    fileContent = File.ReadAllText(rollbackDefinitionFilePath);
                }
                else
                {
                    throw new ArgumentException(string.Format(LocalizableStrings.RollbackDefinitionFileDoesNotExist, rollbackDefinitionFilePath));
                }
            }
            return JsonSerializer.Deserialize<IDictionary<string, string>>(fileContent)
                .Select(manifest =>
                {
                    ManifestVersion manifestVersion;
                    SdkFeatureBand manifestFeatureBand;
                    var parts = manifest.Value.Split('/');
                    
                    string manifestVersionString = (parts[0]);
                    if (!FXVersion.TryParse(manifestVersionString, out FXVersion version))
                    {
                        throw new FormatException(String.Format(LocalizableStrings.InvalidVersionForWorkload, manifest.Key, manifestVersionString));
                    } 

                    manifestVersion = new ManifestVersion(parts[0]);
                    if (parts.Length == 1)
                    {
                        manifestFeatureBand = _sdkFeatureBand;
                    }
                    else
                    {
                        manifestFeatureBand = new SdkFeatureBand(parts[1]);
                    }
                    return (new ManifestId(manifest.Key), manifestVersion, manifestFeatureBand);
                });
        }

        private bool BackgroundUpdatesAreDisabled() =>
            bool.TryParse(_getEnvironmentVariable(EnvironmentVariableNames.WORKLOAD_UPDATE_NOTIFY_DISABLE), out var disableEnvVar) && disableEnvVar;

        private string GetAdvertisingManifestSentinelPath(SdkFeatureBand featureBand) => Path.Combine(_userProfileDir, $".workloadAdvertisingManifestSentinel{featureBand}");

        private string GetAdvertisingWorkloadsFilePath(SdkFeatureBand featureBand) => GetAdvertisingWorkloadsFilePath(_userProfileDir, featureBand);

        private static string GetAdvertisingWorkloadsFilePath(string userProfileDir, SdkFeatureBand featureBand) => Path.Combine(userProfileDir, $".workloadAdvertisingUpdates{featureBand}");

        private async Task<String> GetOnlinePackagePath(SdkFeatureBand sdkFeatureBand, ManifestId manifestId, bool includePreviews) 
            {
                string packagePath = await _nugetPackageDownloader.DownloadPackageAsync(
                    GetManifestPackageId(sdkFeatureBand, manifestId),
                    packageSourceLocation: _packageSourceLocation,
                    includePreview: includePreviews);
                
                return packagePath;
            }

        private string GetOfflinePackagePath(SdkFeatureBand sdkFeatureBand, ManifestId manifestId, DirectoryPath? offlineCache = null)
        {
            string packagePath = Directory.GetFiles(offlineCache.Value.Value)
                .Where(path => path.EndsWith(".nupkg"))
                .Where(path =>
                {
                    var manifestPackageId = GetManifestPackageId(sdkFeatureBand, manifestId).ToString();
                    return Path.GetFileName(path).StartsWith(manifestPackageId, StringComparison.OrdinalIgnoreCase);
                })
                .Max();

            return packagePath;
        }

        private async Task<(bool, string)> GetManifestPackageUpdate(SdkFeatureBand sdkFeatureBand, ManifestId manifestId, bool includePreviews, DirectoryPath? offlineCache = null)
        {
            if (offlineCache == null || !offlineCache.HasValue)
            {
                try 
                {
                    string packagePath = await GetOnlinePackagePath(sdkFeatureBand, manifestId, includePreviews);
                    return (true, packagePath);
                }
                catch (NuGetPackageNotFoundException)
                {
                    return (false, null);
                }
            }
            else
            {
                string packagePath = GetOfflinePackagePath(sdkFeatureBand, manifestId, offlineCache);
                return (packagePath != null, packagePath);
            }
        }

        private string GetAdvertisingManifestPath(SdkFeatureBand featureBand, ManifestId manifestId) =>
            Path.Combine(_userProfileDir, "sdk-advertising", featureBand.ToString(), manifestId.ToString());

        internal static PackageId GetManifestPackageId(SdkFeatureBand featureBand, ManifestId manifestId) =>
            GetManifestPackageId(featureBand, manifestId, InstallType.FileBased);

        internal static PackageId GetManifestPackageId(SdkFeatureBand featureBand, ManifestId manifestId, InstallType installType) =>
            installType switch
            {
                InstallType.FileBased => new PackageId($"{manifestId}.Manifest-{featureBand}"),
                InstallType.Msi => new PackageId($"{manifestId}.Manifest-{featureBand}.Msi.{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}"),
                _ => throw new ArgumentException(String.Format(LocalizableStrings.UnknownInstallType, (int)installType)),
            };
    }
}
