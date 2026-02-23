using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Octokit;
using Toolbox.Library;

namespace Toolbox
{
    public class UpdatePatchNote
    {
        public string Summary { get; set; }
        public string Details { get; set; }
        public string Date { get; set; }
    }

    public class UpdateProgram
    {
        static List<Release> Releases = new List<Release>();
        static readonly string RepositoryOwner = GetAppSetting("UpdateRepositoryOwner", "KillzXGaming");
        static readonly string RepositoryName = GetAppSetting("UpdateRepositoryName", "Switch-Toolbox");
        static readonly string ReleaseAssetName = GetAppSetting("UpdateReleaseAssetName", "");

        public static bool CanUpdate = false;
        public static bool Downloaded = false;
        public static Release LatestRelease;
        public static List<UpdatePatchNote> PatchNotes = new List<UpdatePatchNote>();
        public static string LatestReleaseTitle = "";
        public static DateTime LatestReleaseTime;

        public static void CheckLatest()
        {
            try
            {
                CanUpdate = false;
                Downloaded = false;
                LatestRelease = null;
                LatestReleaseTitle = "";
                LatestReleaseTime = DateTime.MinValue;
                PatchNotes.Clear();

                VersionCheck versionCheck = new VersionCheck(true);

                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var client = new GitHubClient(new ProductHeaderValue("ST_UpdateTool"));

                GetReleases(client).Wait();

                var info = client.GetLastApiInfo();
                if (info != null && info.RateLimit.Remaining <= 0)
                    return;

                var latestRelease = Releases.FirstOrDefault(x => GetPreferredAsset(x) != null);
                var latestAsset = GetPreferredAsset(latestRelease);
                if (latestRelease != null && latestAsset != null && latestAsset.UpdatedAt.ToString() == versionCheck.CompileDate)
                {
                    Runtime.ProgramVersion = latestRelease.TagName;
                    Runtime.CompileDate = latestAsset.UpdatedAt.ToString();
                    Runtime.CommitInfo = latestRelease.TargetCommitish;
                    return;
                }

                foreach (Release latest in Releases)
                {
                    var asset = GetPreferredAsset(latest);
                    if (asset == null)
                        continue;

                    Console.WriteLine(
                        "The latest release is tagged at {0} and is named {1} commit {2} date {3}",
                        latest.TagName,
                        latest.Name,
                        latest.TargetCommitish,
                        asset.UpdatedAt.ToString());

                    LatestReleaseTime = asset.UpdatedAt.DateTime;
                    LatestRelease = latest;
                    LatestReleaseTitle = string.IsNullOrWhiteSpace(latest.Name) ? latest.TagName : latest.Name;
                    LoadPatchNotes(latest);
                    CanUpdate = true;
                    break;
                }

                Releases.Clear();
            }
            catch
            {
            }
        }

        static async Task GetReleases(GitHubClient client)
        {
            Releases = new List<Release>();
            foreach (Release release in await client.Repository.Release.GetAll(RepositoryOwner, RepositoryName))
                Releases.Add(release);
        }

        static string GetAppSetting(string key, string fallback)
        {
            string value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            return value.Trim();
        }

        static ReleaseAsset GetPreferredAsset(Release release)
        {
            if (release == null || release.Assets == null || release.Assets.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(ReleaseAssetName))
            {
                var configured = release.Assets.FirstOrDefault(x =>
                    x.Name.Equals(ReleaseAssetName, StringComparison.OrdinalIgnoreCase));
                if (configured != null)
                    return configured;
            }

            var zipAsset = release.Assets.FirstOrDefault(x =>
                x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (zipAsset != null)
                return zipAsset;

            return release.Assets.First();
        }

        static void LoadPatchNotes(Release release)
        {
            PatchNotes.Clear();

            string dateText = (release.PublishedAt ?? release.CreatedAt).LocalDateTime.ToString();

            if (string.IsNullOrWhiteSpace(release.Body))
            {
                PatchNotes.Add(new UpdatePatchNote
                {
                    Summary = "No patch notes were provided for this release.",
                    Details = "This release does not include notes in the GitHub release body.",
                    Date = dateText
                });
                return;
            }

            string[] lines = release.Body.Replace("\r", "").Split('\n');
            foreach (string rawLine in lines)
            {
                string line = CleanPatchNoteLine(rawLine);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                PatchNotes.Add(new UpdatePatchNote
                {
                    Summary = line,
                    Details = line,
                    Date = dateText
                });
            }

            if (PatchNotes.Count == 0)
            {
                PatchNotes.Add(new UpdatePatchNote
                {
                    Summary = "No patch notes were provided for this release.",
                    Details = "This release body contains no readable patch-note lines.",
                    Date = dateText
                });
            }
        }

        static string CleanPatchNoteLine(string line)
        {
            if (line == null)
                return string.Empty;

            line = line.Trim();
            if (line.Length == 0)
                return line;

            if (line.StartsWith("- "))
                line = line.Substring(2).Trim();
            else if (line.StartsWith("* "))
                line = line.Substring(2).Trim();
            else if (line.StartsWith("• "))
                line = line.Substring(2).Trim();
            else if (line.StartsWith("## "))
                line = line.Substring(3).Trim();

            if (line.StartsWith("`") && line.EndsWith("`") && line.Length > 1)
                line = line.Substring(1, line.Length - 2);

            return line.Trim();
        }
    }
}
