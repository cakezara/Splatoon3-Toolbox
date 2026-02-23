using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;
using Octokit;

namespace Updater
{
    class Program
    {
        static Release[] releases;

        static readonly string RepositoryOwner = GetAppSetting("UpdateRepositoryOwner", "KillzXGaming");
        static readonly string RepositoryName = GetAppSetting("UpdateRepositoryName", "Switch-Toolbox");
        static readonly string ReleaseAssetName = GetAppSetting("UpdateReleaseAssetName", "");

        static string execDirectory = "";
        static string folderDir = "";
        static bool foundRelease = false;

        static void Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

            execDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location);
            folderDir = execDirectory;

            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var client = new GitHubClient(new ProductHeaderValue("ST_UpdateTool"));
            GetReleases(client).Wait();

            string versionTxt = Path.Combine(execDirectory, "Version.txt");
            if (!File.Exists(versionTxt))
            {
                using (File.Create(versionTxt))
                {
                }
            }

            string[] versionInfo = File.ReadLines(versionTxt).ToArray();

            string programVersion = "";
            string compileDate = "";
            string commitInfo = "";
            if (versionInfo.Length >= 3)
            {
                programVersion = versionInfo[0];
                compileDate = versionInfo[1];
                commitInfo = versionInfo[2];
            }

            foreach (string arg in args)
            {
                switch (arg)
                {
                    case "-d":
                    case "--download":
                        Download(compileDate);
                        break;
                    case "-i":
                    case "--install":
                        Install();
                        break;
                    case "-b":
                    case "--boot":
                        Boot();
                        Environment.Exit(0);
                        break;
                    case "-e":
                    case "--exit":
                        Environment.Exit(0);
                        break;
                }
            }
            Console.Read();
        }

        static void Boot()
        {
            Console.WriteLine("Booting...");
            Thread.Sleep(3000);
            System.Diagnostics.Process.Start(Path.Combine(folderDir, "Toolbox.exe"));
        }

        static void Install()
        {
            Console.WriteLine("Installing...");
            string extractRoot = GetExtractRoot("master");
            if (!Directory.Exists(extractRoot))
            {
                Console.WriteLine("No extracted update directory was found.");
                return;
            }

            foreach (string dir in Directory.GetDirectories(extractRoot))
            {
                SetAccessRule(folderDir);
                SetAccessRule(dir);

                string dirName = new DirectoryInfo(dir).Name;
                string destDir = Path.Combine(folderDir, dirName + @"\");

                if (dirName.Equals("Hashes", StringComparison.CurrentCultureIgnoreCase))
                    continue;

                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, true);

                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, true);

                Directory.Move(dir, destDir);
            }

            foreach (string file in Directory.GetFiles(extractRoot))
            {
                if (file.Contains("Updater.exe") || file.Contains("Updater.exe.config")
                    || file.Contains("Updater.pdb") || file.Contains("Octokit.dll"))
                    continue;

                SetAccessRule(file);
                SetAccessRule(folderDir);

                string destFile = Path.Combine(folderDir, Path.GetFileName(file));
                if (File.Exists(destFile))
                    File.Delete(destFile);

                File.Move(file, destFile);
            }
        }

        static string GetExtractRoot(string baseDirectory)
        {
            if (!Directory.Exists(baseDirectory))
                return baseDirectory;

            string[] childDirectories = Directory.GetDirectories(baseDirectory);
            string[] childFiles = Directory.GetFiles(baseDirectory);
            if (childDirectories.Length == 1 && childFiles.Length == 0)
                return childDirectories[0];

            return baseDirectory;
        }

        static void SetAccessRule(string directory)
        {
            try
            {
                DirectorySecurity sec = Directory.GetAccessControl(directory);
                FileSystemAccessRule accRule = new FileSystemAccessRule(Environment.UserDomainName + "\\" + Environment.UserName, FileSystemRights.FullControl, AccessControlType.Allow);
                sec.AddAccessRule(accRule);
                Directory.SetAccessControl(directory, sec);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to set access rule for directory '{directory}': {ex.Message}");
            }
        }

        static void Download(string compileDate)
        {
            foreach (Release latest in releases)
            {
                ReleaseAsset asset = GetPreferredAsset(latest);
                if (asset == null)
                    continue;

                Console.WriteLine("Checking Update");
                if (!foundRelease)
                {
                    if (!asset.UpdatedAt.ToString().Equals(compileDate))
                    {
                        Console.WriteLine("Downloading release...");
                        bool isDownloaded = DownloadedProgram(latest, asset);

                        if (isDownloaded)
                            Console.WriteLine("Downloaded update successfully!");
                        else
                            Console.WriteLine("Failed to download update!");
                    }
                }
                foundRelease = true;
            }
        }

        static bool DownloadedProgram(Release release, ReleaseAsset asset)
        {
            return DownloadRelease(
                "master",
                asset.BrowserDownloadUrl,
                release.TagName,
                asset.UpdatedAt.ToString(),
                release.TargetCommitish);
        }

        static bool DownloadRelease(string downloadName, string url, string programVersion, string compileDate, string commitInfo)
        {
            try
            {
                using (var webClient = new WebClient())
                {
                    webClient.DownloadFile(url, downloadName + ".zip");
                }

                if (Directory.Exists(downloadName + "/"))
                    Directory.Delete(downloadName + "/", true);

                ZipFile.ExtractToDirectory(downloadName + ".zip", downloadName + "/");
                File.Delete(downloadName + ".zip");

                string extractRoot = GetExtractRoot(downloadName);
                string versionTxt = Path.Combine(Path.GetFullPath(extractRoot), "Version.txt");

                using (StreamWriter writer = new StreamWriter(versionTxt))
                {
                    writer.WriteLine($"{programVersion}");
                    writer.WriteLine($"{compileDate}");
                    writer.WriteLine($"{commitInfo}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download update! {ex}");
                return false;
            }
        }

        static async Task GetReleases(GitHubClient client)
        {
            List<Release> releaseList = new List<Release>();
            foreach (Release release in await client.Repository.Release.GetAll(RepositoryOwner, RepositoryName))
                releaseList.Add(release);
            releases = releaseList.ToArray();
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

        static string GetAppSetting(string key, string fallback)
        {
            string value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            return value.Trim();
        }
    }
}
