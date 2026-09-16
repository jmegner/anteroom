using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Anteroom.App.Services;

public enum UpdateStatus { UpToDate, Available, Failed }

/// <summary>One downloadable file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A release newer than what is installed, paired with the asset matching this install.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, ReleaseAsset Asset, bool SelfContained)
{
    public string SizeText => Asset.Size > 0 ? $"{Asset.Size / 1024d / 1024d:N1} MB" : "unknown size";
    public string FlavourText => SelfContained ? "self-contained" : "framework-dependent";
}

public sealed record UpdateCheckResult(UpdateStatus Status, UpdateInfo? Update, string? Error);

/// <summary>
/// Checks the project's GitHub releases and replaces this installation in place.
///
/// In place matters: the absolute path to anteroom-hook.exe is written into Claude's settings.json,
/// so updating the folder where it already lives keeps that registration valid. Moving the install
/// somewhere new would mean reconnecting.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "parthybhatt/anteroom";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
    private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    // Anything the updater downloads gets executed, so it may only ever come from GitHub over HTTPS.
    private static readonly string[] AllowedHosts =
    {
        "api.github.com", "github.com", "objects.githubusercontent.com",
        "release-assets.githubusercontent.com", "github-releases.githubusercontent.com"
    };

    private readonly SettingsService _settings;

    public UpdateService(SettingsService settings) => _settings = settings;

    /// <summary>Folder this build runs from, and the folder an update replaces.</summary>
    public static string InstallDirectory =>
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    private static string StagingRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anteroom", "updates");

    public static Version CurrentVersion
    {
        get
        {
            try
            {
                var informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                // Informational versions can carry a +buildmetadata suffix, which Version cannot parse.
                if (informational is not null)
                {
                    var trimmed = informational.Split('+', '-')[0];
                    if (Version.TryParse(trimmed, out var parsed)) return Normalise(parsed);
                }

                var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
                if (assemblyVersion is not null) return Normalise(assemblyVersion);
            }
            catch
            {
                // Fall through to the placeholder below.
            }
            return new Version(0, 0, 0);
        }
    }

    private static Version Normalise(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    /// <summary>
    /// True when the runtime ships alongside the app. A self-contained install must be updated with
    /// a self-contained package: handing it the small one would leave a build that cannot start on a
    /// machine with no .NET Desktop Runtime.
    /// </summary>
    public static bool IsSelfContainedInstall()
    {
        var dir = InstallDirectory;

        // The runtime host sits next to the app only in a self-contained publish.
        if (File.Exists(Path.Combine(dir, "hostpolicy.dll")) || File.Exists(Path.Combine(dir, "coreclr.dll")))
            return true;

        // Corroborate with the runtime config: a framework-dependent build names a shared framework,
        // a self-contained one lists the frameworks it already carries.
        try
        {
            var config = Path.Combine(dir, "Anteroom.runtimeconfig.json");
            if (File.Exists(config))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(config));
                if (doc.RootElement.TryGetProperty("runtimeOptions", out var options))
                {
                    if (options.TryGetProperty("includedFrameworks", out _)) return true;
                    if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _))
                        return false;
                }
            }
        }
        catch
        {
            // Unreadable config: fall back to the file probe above.
        }

        return false;
    }

    /// <summary>True when this install can be overwritten without elevation.</summary>
    public static bool CanWriteToInstallDirectory()
    {
        try
        {
            var probe = Path.Combine(InstallDirectory, ".anteroom-write-test");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token = default)
    {
        try
        {
            using var http = CreateClient();
            using var response = await http.GetAsync(LatestReleaseApi, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reason = response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "No published release yet. Drafts and pre-releases are not offered as updates."
                    : $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.";
                return new UpdateCheckResult(UpdateStatus.Failed, null, reason);
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var release = doc.RootElement;

            var tag = Str(release, "tag_name") ?? "";
            var notes = Str(release, "body") ?? "";

            if (!TryParseTag(tag, out var latest))
                return new UpdateCheckResult(UpdateStatus.Failed, null, $"Could not read a version from the tag '{tag}'.");

            RecordCheckTime();

            if (latest <= CurrentVersion)
                return new UpdateCheckResult(UpdateStatus.UpToDate, null, null);

            bool selfContained = IsSelfContainedInstall();
            var asset = PickAsset(release, selfContained);
            if (asset is null)
            {
                var wanted = selfContained ? "self-contained" : "framework-dependent";
                return new UpdateCheckResult(UpdateStatus.Failed, null,
                    $"Release {tag} has no {wanted} package for this install.");
            }

            return new UpdateCheckResult(UpdateStatus.Available,
                new UpdateInfo(latest, tag, notes, asset, selfContained), null);
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, null, "The update check was cancelled.");
        }
        catch (Exception ex)
        {
            Log.Write($"update check failed: {ex.Message}");
            return new UpdateCheckResult(UpdateStatus.Failed, null, ex.Message);
        }
    }

    /// <summary>Picks the asset matching this install's flavour, never the other one.</summary>
    private static ReleaseAsset? PickAsset(JsonElement release, bool selfContained)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var entry in assets.EnumerateArray())
        {
            var name = Str(entry, "name") ?? "";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            bool assetIsSelfContained = name.Contains("self-contained", StringComparison.OrdinalIgnoreCase);
            if (assetIsSelfContained != selfContained) continue;

            var url = Str(entry, "browser_download_url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            long size = entry.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) ? bytes : 0;
            return new ReleaseAsset(name, url!, size);
        }
        return null;
    }

    /// <summary>
    /// Downloads the asset, checks it really is an Anteroom package, and unpacks it to a staging
    /// folder. Nothing in the install directory is touched until the applier runs.
    /// </summary>
    public async Task<string> DownloadAndStageAsync(
        UpdateInfo update, IProgress<(long Done, long Total, string Stage)>? progress, CancellationToken token)
    {
        RequireAllowedHost(update.Asset.Url);

        Directory.CreateDirectory(StagingRoot);
        var staging = Path.Combine(StagingRoot, update.Version.ToString());
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

        var archive = Path.Combine(StagingRoot, update.Asset.Name);
        if (File.Exists(archive)) File.Delete(archive);

        using (var http = CreateClient())
        using (var response = await http.GetAsync(update.Asset.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            // Redirects are followed, so the host that actually served the bytes matters too.
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? update.Asset.Url;
            RequireAllowedHost(finalUrl);

            long total = response.Content.Headers.ContentLength ?? update.Asset.Size;

            using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var destination = File.Create(archive);

            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                done += read;
                progress?.Report((done, total, "Downloading"));
            }
        }

        progress?.Report((1, 1, "Checking the package"));
        VerifyPackage(archive);

        progress?.Report((1, 1, "Unpacking"));
        ZipFile.ExtractToDirectory(archive, staging);

        try { File.Delete(archive); } catch { /* the staging folder is disposable anyway */ }

        // Belt and braces: the unpacked folder must hold both binaries, the same rule the release
        // workflow enforces, because Claude's settings.json points straight at the shim.
        foreach (var required in new[] { "Anteroom.exe", "anteroom-hook.exe" })
        {
            if (!File.Exists(Path.Combine(staging, required)))
                throw new InvalidDataException($"The downloaded package is missing {required}.");
        }

        Log.Write($"update staged at {staging}");
        return staging;
    }

    private static void VerifyPackage(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);

        bool hasApp = zip.Entries.Any(e => e.FullName.Equals("Anteroom.exe", StringComparison.OrdinalIgnoreCase));
        bool hasHook = zip.Entries.Any(e => e.FullName.Equals("anteroom-hook.exe", StringComparison.OrdinalIgnoreCase));

        if (!hasApp || !hasHook)
            throw new InvalidDataException("That download does not look like an Anteroom package.");

        // A zip entry that climbs out of the extraction folder could write anywhere on disk.
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(entry.FullName))
                throw new InvalidDataException("The package contains an unsafe path and was not installed.");
        }
    }

    /// <summary>
    /// Starts the freshly unpacked Anteroom in applier mode. It waits for this process to exit,
    /// swaps the files, and starts the installed copy again.
    /// </summary>
    public void StartApplier(string stagingDirectory)
    {
        // The swap is performed by a throwaway copy of the version running right now, never by the
        // downloaded one: only the running build is guaranteed to understand this protocol. An
        // older target build would simply ignore the arguments and start up as a second tray app.
        var applier = PrepareApplierCopy();

        var exe = Path.Combine(applier, "Anteroom.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException("Could not prepare the updater.", exe);

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = applier
        };
        start.ArgumentList.Add("--apply-update");
        start.ArgumentList.Add("--source");
        start.ArgumentList.Add(stagingDirectory);
        start.ArgumentList.Add("--target");
        start.ArgumentList.Add(InstallDirectory);
        start.ArgumentList.Add("--wait-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString());

        Process.Start(start);
        Log.Write($"applier launched from {applier}, installing {stagingDirectory}");
    }

    /// <summary>
    /// Copies this installation somewhere temporary so it can rewrite the folder it normally runs
    /// from. Windows will not let a running program overwrite its own files.
    /// </summary>
    private static string PrepareApplierCopy()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anteroom", "applier");

        var destination = root;
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // A previous applier may still hold it; use a fresh folder rather than failing.
            destination = root + "-" + DateTime.Now.ToString("HHmmss");
        }

        UpdateApplier.CopyDirectory(InstallDirectory, destination);
        return destination;
    }

    /// <summary>True when a periodic check is due.</summary>
    public bool IsCheckDue()
    {
        var settings = _settings.Current;
        if (!settings.CheckUpdatesPeriodically) return false;

        var interval = TimeSpan.FromHours(Math.Clamp(settings.UpdateCheckIntervalHours, 1, 24 * 14));
        return DateTime.UtcNow - settings.LastUpdateCheckUtc >= interval;
    }

    private void RecordCheckTime()
    {
        _settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
        _settings.Save();
    }

    /// <summary>Removes staging folders left behind by earlier updates.</summary>
    public static void PruneStaging()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;

            foreach (var directory in Directory.GetDirectories(StagingRoot))
            {
                try { Directory.Delete(directory, recursive: true); }
                catch { /* still in use by the applier that just ran; next launch gets it */ }
            }

            // The throwaway updater copies live next door and are equally disposable.
            var applierRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anteroom");
            foreach (var directory in Directory.GetDirectories(applierRoot, "applier*"))
            {
                try { Directory.Delete(directory, recursive: true); } catch { /* next launch */ }
            }
            foreach (var file in Directory.GetFiles(StagingRoot, "*.zip"))
            {
                try { File.Delete(file); } catch { /* ignore */ }
            }
        }
        catch
        {
            // Housekeeping only.
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub rejects requests with no User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Anteroom-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static void RequireAllowedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Refusing to fetch a malformed URL: {url}");

        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Refusing to download an update over plain HTTP.");

        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to download an update from {uri.Host}.");
    }

    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var cleaned = tag.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(cleaned, out var parsed)) return false;

        version = Normalise(parsed);
        return true;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
