// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.DotNet.Git.IssueManager;
using Microsoft.DotNet.Maestro.Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MSBuild = Microsoft.Build.Utilities;

namespace Microsoft.DotNet.Build.Tasks.Feed
{
    /// <summary>
    /// The intended use of this task is to push artifacts described in
    /// a build manifest to a static package feed.
    /// </summary>
    public class PushArtifactsInManifestToFeed : MSBuild.Task
    {
        /// <summary>
        /// Target to where the assets should be published.
        /// E.g., https://blab.blob.core.windows.net/container/index.json
        /// </summary>
        [Required]
        public string ExpectedFeedUrl { get; set; }

        /// <summary>
        /// Account key to the feed storage.
        /// </summary>
        [Required]
        public string AccountKey { get; set; }

        /// <summary>
        /// Full path to the assets to publish manifest.
        /// </summary>
        [Required]
        public string AssetManifestPath { get; set; }

        /// <summary>
        /// Full path to the folder containing blob assets.
        /// </summary>
        [Required]
        public string BlobAssetsBasePath { get; set; }

        /// <summary>
        /// Full path to the folder containing package assets.
        /// </summary>
        [Required]
        public string PackageAssetsBasePath { get; set; }

        /// <summary>
        /// ID of the build (in BAR/Maestro) that produced the artifacts being published.
        /// This might change in the future as we'll probably fetch this ID from the manifest itself.
        /// </summary>
        [Required]
        public int BARBuildId { get; set; }

        /// <summary>
        /// Access point to the Maestro API to be used for accessing BAR.
        /// </summary>
        [Required]
        public string MaestroApiEndpoint { get; set; }

        /// <summary>
        /// Authentication token to be used when interacting with Maestro API.
        /// </summary>
        [Required]
        public string BuildAssetRegistryToken { get; set; }

        /// <summary>
        /// When set to true packages with the same name will be overriden on 
        /// the target feed.
        /// </summary>
        public bool Overwrite { get; set; }

        /// <summary>
        /// Enables idempotency when Overwrite is false.
        /// 
        /// false: (default) Attempting to upload an item that already exists fails.
        /// 
        /// true: When an item already exists, download the existing blob to check if it's
        /// byte-for-byte identical to the one being uploaded. If so, pass. If not, fail.
        /// </summary>
        public bool PassIfExistingItemIdentical { get; set; }

        /// <summary>
        /// Maximum number of concurrent pushes of assets to the flat container.
        /// </summary>
        public int MaxClients { get; set; } = 8;

        /// <summary>
        /// Maximum allowed timeout per upload request to the flat container.
        /// </summary>
        public int UploadTimeoutInMinutes { get; set; } = 5;

        /// <summary>
        /// The URL for the release which triggered this task.
        /// </summary>
        public string CurrentReleasePipelineUrl { get; set; }

        /// <summary>
        /// Title of the release which triggered this task.
        /// </summary>
        public string CurrentReleaseDescription { get; set; }

        /// <summary>
        /// The URL of the build linked to the running release definition.
        /// </summary>
        public string TriggeredByBuildUrl { get; set; }

        /// <summary>
        /// Token to be used by the Octokit client.
        /// </summary>
        public string GitHubPersonalAccessToken { get; set; }

        /// <summary>
        /// Token to be used to fetch commit information from Azure DevOps.
        /// </summary>
        public string AzureDevOpsPersonalAccessToken { get; set; }

        /// <summary>
        /// The repo where we will file the issues when something fails while publishing.
        /// Arcade repo will be used by default.
        /// </summary>
        public string RepoForFilingPublishingIssues { get; set; } = "https://github.com/dotnet/arcade";

        /// <summary>
        /// Who to tag in the created issue for them to investigate the failure.
        /// </summary>
        public string FyiHandles { get; set; } = "@JohnTortugo @jcagme";

        public override bool Execute()
        {
            return ExecuteAsync().GetAwaiter().GetResult();
        }

        public async Task<bool> ExecuteAsync()
        {
            string commit = null, repoUrl = null, buildId = null;

            try
            {
                Log.LogMessage(MessageImportance.High, "Performing push feeds.");

                if (string.IsNullOrWhiteSpace(ExpectedFeedUrl) || string.IsNullOrWhiteSpace(AccountKey))
                {
                    Log.LogError($"{nameof(ExpectedFeedUrl)} / {nameof(AccountKey)} is not set properly.");
                }
                else if (string.IsNullOrWhiteSpace(AssetManifestPath) || !File.Exists(AssetManifestPath))
                {
                    Log.LogError($"Problem reading asset manifest path from {AssetManifestPath}");
                }
                else if (MaxClients <= 0)
                {
                    Log.LogError($"{nameof(MaxClients)} should be greater than zero.");
                }
                else if (UploadTimeoutInMinutes <= 0)
                {
                    Log.LogError($"{nameof(UploadTimeoutInMinutes)} should be greater than zero.");
                }

                var buildModel = BuildManifestUtil.ManifestFileToModel(AssetManifestPath, Log);
                commit = buildModel.Identity.Commit;
                repoUrl = buildModel.Identity.Name;
                buildId = buildModel.Identity.BuildId;

                // Parsing the manifest may fail for several reasons
                if (Log.HasLoggedErrors)
                {
                    return false;
                }

                // Fetch Maestro record of the build. We're going to use it to get the BAR ID
                // of the assets being published so we can add a new location for them.
                IMaestroApi client = ApiFactory.GetAuthenticated(MaestroApiEndpoint, BuildAssetRegistryToken);
                Maestro.Client.Models.Build buildInformation = await client.Builds.GetBuildAsync(BARBuildId);

                var blobFeedAction = new BlobFeedAction(ExpectedFeedUrl, AccountKey, Log);
                var pushOptions = new PushOptions
                {
                    AllowOverwrite = Overwrite,
                    PassIfExistingItemIdentical = PassIfExistingItemIdentical
                };

                if (buildModel.Artifacts.Packages.Any())
                {
                    if (!Directory.Exists(PackageAssetsBasePath))
                    {
                        Log.LogError($"Invalid {nameof(PackageAssetsBasePath)} was supplied: {PackageAssetsBasePath}");
                        return false;
                    }

                    PackageAssetsBasePath = PackageAssetsBasePath.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                    var packages = buildModel.Artifacts.Packages.Select(p => $"{PackageAssetsBasePath}{p.Id}.{p.Version}.nupkg");

                    await blobFeedAction.PushToFeedAsync(packages, pushOptions);

                    foreach (var package in buildModel.Artifacts.Packages)
                    {
                        var assetRecord = buildInformation.Assets
                            .Where(a => a.Name.Equals(package.Id) && a.Version.Equals(package.Version))
                            .Single();

                        if (assetRecord == null)
                        {
                            Log.LogError($"Asset with Id {package.Id}, Version {package.Version} isn't registered on the BAR Build with ID {BARBuildId}");
                            continue;
                        }

                        await client.Assets.AddAssetLocationToAssetAsync(assetRecord.Id.Value, ExpectedFeedUrl, "NugetFeed");
                    }
                }

                if (buildModel.Artifacts.Blobs.Any())
                {
                    if (!Directory.Exists(BlobAssetsBasePath))
                    {
                        Log.LogError($"Invalid {nameof(BlobAssetsBasePath)} was supplied: {BlobAssetsBasePath}");
                        return false;
                    }

                    BlobAssetsBasePath = BlobAssetsBasePath.TrimEnd(Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                    var blobs = buildModel.Artifacts.Blobs
                        .Select(blob =>
                        {
                            var fileName = Path.GetFileName(blob.Id);
                            return new MSBuild.TaskItem($"{BlobAssetsBasePath}{fileName}", new Dictionary<string, string>
                            {
                                {"RelativeBlobPath", $"{BuildManifestUtil.AssetsVirtualDir}{blob.Id}"}
                            });
                        })
                        .ToArray();

                    await blobFeedAction.PublishToFlatContainerAsync(blobs, MaxClients, UploadTimeoutInMinutes, pushOptions);

                    foreach (var package in buildModel.Artifacts.Blobs)
                    {
                        var assetRecord = buildInformation.Assets
                            .Where(a => a.Name.Equals(package.Id))
                            .SingleOrDefault();

                        if (assetRecord == null)
                        {
                            Log.LogError($"Asset with Id {package.Id} isn't registered on the BAR Build with ID {BARBuildId}");
                            continue;
                        }

                        await client.Assets.AddAssetLocationToAssetAsync(assetRecord.Id.Value, ExpectedFeedUrl, "NugetFeed");
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e, true);
            }

            if (Log.HasLoggedErrors)
            {
                await CreateGitHubIssue(repoUrl, commit, buildId);
            }

            return !Log.HasLoggedErrors;
        }

        /// <summary>
        /// Creates a new GitHub issue.
        /// </summary>
        /// <param name="repoUrl">GitHub source repo IRL.</param>
        /// <param name="commit">Commit SHA in the source repo.</param>
        /// <param name="buildId">AzDO build id.</param>
        private async Task CreateGitHubIssue(string repoUrl, string commit, string buildId)
        {
            string userHandle = "Last commit author could not be determined...";
            string issueTitle = $"Release '{CurrentReleaseDescription}' failed";
            string issueDescription = $"Something failed while trying to publish artifacts for build [{buildId}]({TriggeredByBuildUrl})." +
                    $"{Environment.NewLine} {Environment.NewLine}" +
                    $"Please click [here]({CurrentReleasePipelineUrl}) to check the error logs." +
                    $"{Environment.NewLine} {Environment.NewLine}" +
                    $"Last commit by: {userHandle}" +
                    $" {Environment.NewLine} {Environment.NewLine}" +
                    $"/fyi: {FyiHandles}";

            try
            {
                IssueManager issueManager = new IssueManager(
                    GitHubPersonalAccessToken, 
                    AzureDevOpsPersonalAccessToken);

                try
                {
                    userHandle = await issueManager.GetCommitAuthorAsync(repoUrl, commit);
                }
                catch (Exception exc)
                {
                    Log.LogError($"Something failed while trying to get the author of commit '{commit}'. Exception: {exc}");
                }

                int issueId = await issueManager.CreateNewIssueAsync(
                    repoUrl,
                    issueTitle,
                    issueDescription);

                Log.LogMessage(MessageImportance.High, $"GitHub issue {issueId} to track the messages above.");
            }
            catch (Exception exc)
            {
                Log.LogErrorFromException(exc);
            }
        }

        /// <summary>
        /// Parse out the owner and repo from a repository url
        /// </summary>
        /// <param name="repoUrl">GitHub repository URL</param>
        /// <returns>Tuple of owner and repo</returns>
        private static (string owner, string repo) ParseRepoUri(string repoUrl)
        {
            Regex repositoryUriPattern = new Regex(@"^/(?<owner>[^/]+)/(?<repo>[^/]+)/?$");
            Uri uri = new Uri(repoUrl);

            Match match = repositoryUriPattern.Match(uri.AbsolutePath);

            if (!match.Success)
            {
                return default;
            }

            return (match.Groups["owner"].Value, match.Groups["repo"].Value);
        }
    }
}
