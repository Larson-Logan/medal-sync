using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using MedalSync.Cli;
using MedalSync.Config;
using MedalSync.Coordination;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

public sealed class CloudHandoffTests : IDisposable
{
    private readonly MedalFixture fixture = new();
    private const string LibraryId = "0123456789abcdef0123456789abcdef";
    private const string FileId = "synthetic-journal-id";
    private static readonly DeviceIdentity First = new(new string('a', 32), "First PC");
    private static readonly DeviceIdentity Second = new(new string('b', 32), "Second PC");
    private static readonly MedalVersions Versions = new("2643.583.1", "2643.3040.1");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    public void Dispose() => fixture.Dispose();

    private MedalSyncConfig Config(string pc) => new(1, LibraryId, MedalFixture.AccountId, fixture.PathFor(pc, "journal"),
        "gdrive:MedalSync", @"M:\Clips", fixture.PathFor(pc, "cache"), @"C:\Tools\rclone.exe", new(5, 20, 1, 300, 30, 1000, 2, MountConfig.DefaultControlPort), 4, [])
    {
        StorageMode = StorageMode.Cloud, CloudJournalId = FileId,
        Replication = new(fixture.PathFor(pc, "media"), fixture.PathFor(pc, "private", "rclone.conf"), []),
    };

    private LibrarySnapshot Snapshot(string name, params string[] clips)
    {
        string db = fixture.CreateLibrary(name);
        foreach (string clip in clips) MedalFixture.AddContent(db, clip, video: @"M:\Clips\" + clip + ".mp4");
        return LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
    }

    // This models the API's atomic metadata/ETag operation, not a local filesystem lock.
    private sealed class Drive : HttpMessageHandler
    {
        public CloudJournal? Value;
        public bool Offline, IgnoreConditions, ApplyThenDisconnect;
        public HttpStatusCode? Failure;
        /// <summary>When it returns true, a journal write is lost before it applies, as a dropped connection would.</summary>
        public Func<bool>? DropWrite;
        public int Revision = 1, Writes, Reads;
        public readonly List<HttpMethod> Methods = [];
        private readonly object gate = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("synthetic-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host);
            if (Offline) throw new HttpRequestException("Synthetic offline");
            if (Failure is { } failure) return Reply(failure);
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (gate)
            {
                Methods.Add(request.Method);
                if (request.Method == HttpMethod.Post)
                {
                    if (Value is not null) return Reply(HttpStatusCode.Conflict);
                    using var create = JsonDocument.Parse(body);
                    Assert.Equal(FileId, create.RootElement.GetProperty("id").GetString());
                    Assert.Equal("drive-folder-id", create.RootElement.GetProperty("parents")[0].GetProperty("id").GetString());
                    Value = Json.Deserialize<CloudJournal>(Encoding.UTF8.GetBytes(create.RootElement.GetProperty("description").GetString()!), "synthetic");
                    return Reply(HttpStatusCode.OK);
                }
                if (Value is null) return Reply(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Get)
                {
                    Reads++;
                    return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new
                    {
                        id = FileId, mimeType = "application/json", description = Encoding.UTF8.GetString(Json.Serialize(Value)),
                        etag = "\"" + Revision + "\"", labels = new { trashed = false },
                    }));
                }
                Assert.Equal(HttpMethod.Patch, request.Method);
                if (!IgnoreConditions && request.Headers.IfMatch.Single().Tag != "\"" + Revision + "\"") return Reply(HttpStatusCode.PreconditionFailed);
                using var patch = JsonDocument.Parse(body);
                if (patch.RootElement.TryGetProperty("description", out var description))
                {
                    if (DropWrite?.Invoke() == true) throw new HttpRequestException("Synthetic dropped write");
                    Value = Json.Deserialize<CloudJournal>(Encoding.UTF8.GetBytes(description.GetString()!), "synthetic");
                    Writes++;
                }
                Revision++;
                if (ApplyThenDisconnect && body != "{}") { ApplyThenDisconnect = false; throw new HttpRequestException("lost acknowledgement"); }
                return Reply(HttpStatusCode.OK);
            }
        }
        private static HttpResponseMessage Reply(HttpStatusCode code, string body = "{}") => new(code) { Content = new StringContent(body) };
    }

    private GoogleDriveJournal Api(Drive drive) => new(FileId, () => "synthetic-token", new HttpClient(drive, disposeHandler: false));
    private CloudJournal Initial() => new(1, new(1, LibraryId, MedalFixture.AccountId, Now, First),
        Ownership.Initial(LibraryId, MedalFixture.AccountId, Now), [], "drive-folder-id", @"M:\Clips", new string('0', 64));

    private bool failUpload;
    private bool failDownload;
    private string checksumName = "md5";
    private readonly List<string> downloadedFiles = [];
    private readonly List<string> commands = [];
    private string Run(IReadOnlyList<string> args)
    {
        commands.Add(args[0]);
        if (args[0] == "lsjson" && args.Contains("--stat")) return """{"ID":"drive-folder-id","IsDir":true}""";
        string PathFor(string value) => value.StartsWith("gdrive:", StringComparison.Ordinal)
            ? fixture.PathFor("drive", value[(value.IndexOf(':') + 1)..].Replace('/', Path.DirectorySeparatorChar)) : value;
        string source = PathFor(args[1]);
        if (args[0] == "lsjson")
        {
            if (!Directory.Exists(source)) throw new RcloneFailedException("Synthetic missing folder", 3);
            return JsonSerializer.Serialize(Directory.GetFileSystemEntries(source, "*", SearchOption.AllDirectories).Select(p => new
            {
                Path = Path.GetRelativePath(source, p).Replace('\\', '/'), IsDir = Directory.Exists(p),
                Hashes = Directory.Exists(p) ? null : new Dictionary<string, string> { [checksumName] = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(p))) },
            }));
        }
        string destination = PathFor(args[2]);
        bool upload = args[2].StartsWith("gdrive:", StringComparison.Ordinal);
        if (upload && failUpload) throw new RcloneFailedException("Synthetic quota failure", 7, hints: RcloneHints.Quota);
        if (!Directory.Exists(source)) throw new RcloneFailedException("Synthetic missing folder", 3);
        int listIndex = args.ToList().IndexOf("--files-from-raw");
        var selected = listIndex < 0 ? null : File.ReadAllLines(args[listIndex + 1]).ToHashSet(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (selected is not null && !selected.Contains(relative)) continue;
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (args[0] == "check" || File.Exists(target)) Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(target));
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); }
            if (!upload && args[0] == "copy")
            {
                downloadedFiles.Add(relative);
                if (failDownload) throw new RcloneFailedException("Synthetic interrupted download", 5, hints: RcloneHints.Network);
            }
        }
        return "";
    }

    private CoordinationStore Seed(Drive drive, MedalSyncConfig config, LibrarySnapshot snapshot)
    {
        var backend = new CloudCoordination(config, Api(drive), Run);
        backend.Create(snapshot, new(1, [], []), First, new string('1', 32), Versions, Now);
        return CoordinationStore.OpenCloud(config, backend);
    }

    private void Publish(CoordinationStore store, long number)
    {
        string session = new('1', 32);
        long parent = store.ReadJournal().CommittedGeneration;
        store.Update(j => Ownership.Acquire(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.MarkActive(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.BeginDrain(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.BeginPublish(j, First, session, Now), TimeSpan.Zero);
        store.Generations.Publish(number, parent, session, First, Snapshot("generation-" + number, "clip-" + number), new(1, [], []), Versions, Now);
        store.Update(j => Ownership.CompletePublish(j, First, session, number, Now), TimeSpan.Zero);
    }

    [Theory]
    [InlineData("md5")]
    [InlineData("MD5")]
    public void LaterHandoffsDownloadOnlyMissingGenerationsAndVerifyTheWholeCommittedChain(string hashName)
    {
        checksumName = hashName;
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var config = Config("second");
        var backend = new CloudCoordination(config, Api(drive), Run);
        var second = CoordinationStore.OpenCloud(config, backend);
        Assert.Equal(3, downloadedFiles.Count);
        Publish(first, 1);
        downloadedFiles.Clear();

        Assert.Equal(1, second.ReadJournal().CommittedGeneration);
        Assert.Equal(3, downloadedFiles.Count);
        Assert.All(downloadedFiles, file => Assert.StartsWith(GenerationStore.Name(1) + "/", file));
        Assert.Equal(2, second.Generations.Chain(1).Count());

        Publish(first, 2);
        Publish(first, 3);
        downloadedFiles.Clear();
        Assert.Equal(3, second.ReadJournal().CommittedGeneration);
        Assert.Equal(6, downloadedFiles.Count);
        Assert.All(downloadedFiles, file => Assert.True(file.StartsWith(GenerationStore.Name(2) + "/", StringComparison.Ordinal)
            || file.StartsWith(GenerationStore.Name(3) + "/", StringComparison.Ordinal)));
        downloadedFiles.Clear();
        backend.Download();
        Assert.Empty(downloadedFiles);
        Assert.Empty(Directory.GetDirectories(config.LibraryCoordinationDirectory, ".download-*"));
    }

    [Theory]
    [InlineData("library.json")]
    [InlineData("assets.json")]
    [InlineData("manifest.json")]
    public void LaterDownloadRefusesACachedGenerationThatDiffersFromDrive(string file)
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var config = Config("second");
        var second = CoordinationStore.OpenCloud(config, new(config, Api(drive), Run));
        string cached = Path.Combine(second.Generations.Directory, GenerationStore.Name(0), file);
        byte[] original = File.ReadAllBytes(cached);
        Publish(first, 1);
        string remote = fixture.PathFor("drive", "MedalSync", ".medal-sync", LibraryId, "generations", GenerationStore.Name(0), file);
        File.AppendAllText(remote, "edited");
        byte[] journal = Json.Serialize(drive.Value);
        downloadedFiles.Clear();

        Assert.Throws<InvalidDataException>(second.ReadJournal);
        Assert.Equal(original, File.ReadAllBytes(cached));
        Assert.Equal(journal, Json.Serialize(drive.Value));
        Assert.Empty(downloadedFiles);
        Assert.Empty(Directory.GetDirectories(config.LibraryCoordinationDirectory, ".download-*"));
    }

    [Fact]
    public void InterruptedDownloadCleansScratchAndRetriesWithoutChangingOwnershipOrCachedFiles()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var config = Config("second");
        var backend = new CloudCoordination(config, Api(drive), Run);
        var second = CoordinationStore.OpenCloud(config, backend);
        byte[] cached = File.ReadAllBytes(Path.Combine(second.Generations.Directory, GenerationStore.Name(0), "library.json"));
        Publish(first, 1);
        byte[] journal = Json.Serialize(drive.Value);
        string leftover = Path.Combine(config.LibraryCoordinationDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "partial.json"), "left by a terminated process");
        failDownload = true;

        for (int i = 0; i < 3; i++)
        {
            Assert.Throws<RcloneFailedException>(second.ReadJournal);
            Assert.Empty(Directory.GetDirectories(config.LibraryCoordinationDirectory, ".download-*"));
            Assert.False(second.Generations.Exists(1));
            Assert.Equal(journal, Json.Serialize(drive.Value));
        }
        failDownload = false;
        Assert.Equal(1, second.ReadJournal().CommittedGeneration);
        Assert.Equal(cached, File.ReadAllBytes(Path.Combine(second.Generations.Directory, GenerationStore.Name(0), "library.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StatusPollsPauseARefusedHistoryDownloadAndRetryAfterThePause(bool missingManifest)
    {
        using var drive = new Drive();
        Seed(drive, Config("first"), Snapshot("seed", "one"));
        var config = Config("second");
        var state = HistoryState(config);
        var now = Now;
        int connections = 0;
        var source = new DesktopStatusSource(() => state, () => false, c =>
        {
            connections++;
            return new(c, Api(drive), Run) { Now = () => now };
        });
        string manifest = fixture.PathFor("drive", "MedalSync", ".medal-sync", LibraryId, "generations", GenerationStore.Name(0), "manifest.json");
        byte[] original = File.ReadAllBytes(manifest);
        if (missingManifest) File.Delete(manifest);
        else File.AppendAllText(manifest, "edited");
        Assert.Throws<InvalidDataException>(source.Read);
        int before = commands.Count;

        Assert.Throws<InvalidDataException>(source.Read);
        Assert.Throws<InvalidDataException>(source.Read);
        Assert.Equal(before, commands.Count);
        Assert.Equal(1, connections);
        Assert.Empty(Directory.GetDirectories(config.LibraryCoordinationDirectory, ".download-*"));

        File.WriteAllBytes(manifest, original);
        now += TimeSpan.FromMinutes(1);
        Assert.Equal(SessionPhase.Ready, source.Read().Phase);
        Assert.Equal(1, connections);
    }

    [Fact]
    public void MissingDriveChecksumsRefuseWithoutPublishingDownloadedGenerations()
    {
        using var drive = new Drive();
        Seed(drive, Config("first"), Snapshot("seed", "one"));
        var config = Config("second");
        var backend = new CloudCoordination(config, Api(drive), args => Run(args).Replace("\"md5\":", "\"unavailable\":", StringComparison.Ordinal));
        byte[] journal = Json.Serialize(drive.Value);

        Assert.Throws<InvalidDataException>(backend.Download);
        Assert.Equal(journal, Json.Serialize(drive.Value));
        Assert.Empty(Directory.GetDirectories(config.LibraryCoordinationDirectory, ".download-*"));
        Assert.Empty(new GenerationStore(Path.Combine(config.LibraryCoordinationDirectory, "generations"), LibraryId, MedalFixture.AccountId).Numbered());
    }

    [Fact]
    public void ConcurrentDownloadCannotRemoveTheActiveDownloadsScratchFiles()
    {
        using var drive = new Drive();
        Seed(drive, Config("first"), Snapshot("seed", "one"));
        var config = Config("second");
        var backend = new CloudCoordination(config, Api(drive), Run);
        Directory.CreateDirectory(config.LibraryCoordinationDirectory);
        string scratch = Path.Combine(config.LibraryCoordinationDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        string file = Path.Combine(scratch, "active.json");
        File.WriteAllText(file, "an active transfer");
        using (new FileStream(Path.Combine(config.LibraryCoordinationDirectory, ".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(backend.Download);
            Assert.True(File.Exists(file));
        }
        backend.Download();
        Assert.False(Directory.Exists(scratch));
    }

    private LocalState HistoryState(MedalSyncConfig config)
    {
        var state = new LocalState(fixture.PathFor("history-state"));
        Directory.CreateDirectory(state.Root);
        File.WriteAllBytes(state.ConfigPath, Json.Serialize(config));
        return state;
    }

    [SupportedOSPlatform("windows")]
    private static int History(bool diff, LocalState state, TextWriter output, Func<MedalSyncConfig, CoordinationStore> open)
    {
        var args = Arguments.Parse(diff ? ["--from", "0", "--to", "1", "--json"] : ["--json"],
            new HashSet<string> { "--json" }, new HashSet<string> { "--from", "--to" });
        return diff ? HistoryCommands.Diff(args, output, state, open) : HistoryCommands.List(args, output, state, open);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void HistoryDownloadsAnAbsentCloudCacheWithoutChangingAnotherPcsOwnership(bool diff)
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        string session = new('1', 32);
        first.Update(j => Ownership.Acquire(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.MarkActive(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.BeginDrain(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.BeginPublish(j, First, session, Now), TimeSpan.Zero);
        first.Generations.Publish(1, 0, session, First, Snapshot("next", "one", "two"), new(1, [], []), Versions, Now);
        first.Update(j => Ownership.CompletePublish(j, First, session, 1, Now), TimeSpan.Zero);
        first.Update(j => Ownership.Acquire(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.MarkActive(j, First, session, Now), TimeSpan.Zero);
        byte[] journal = Json.Serialize(drive.Value);
        int writes = drive.Writes, revision = drive.Revision;
        drive.Methods.Clear();
        commands.Clear();

        var config = Config("second");
        var state = HistoryState(config);
        Assert.False(Directory.Exists(config.CoordinationRoot));
        var output = new StringWriter();
        string ReadOnlyRun(IReadOnlyList<string> args)
        {
            Assert.True(args[0] == "lsjson" || args[0] == "copy" && args[1].StartsWith("gdrive:", StringComparison.Ordinal)
                && Path.GetFullPath(args[2]).StartsWith(config.LibraryCoordinationDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            return Run(args);
        }

        Assert.Equal(0, History(diff, state, output, c => CoordinationStore.OpenCloud(c, new(c, Api(drive), ReadOnlyRun))));

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("head").GetInt64());
        if (diff) Assert.Equal(1, json.RootElement.GetProperty("counts").GetProperty("added").GetInt64());
        else Assert.Equal(2, json.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Contains("copy", commands);
        Assert.True(Directory.Exists(Path.Combine(config.LibraryCoordinationDirectory, "generations", GenerationStore.Name(1))));
        Assert.Equal(journal, Json.Serialize(drive.Value));
        Assert.Equal(writes, drive.Writes);
        Assert.Equal(revision, drive.Revision);
        Assert.NotEmpty(drive.Methods);
        Assert.All(drive.Methods, method => Assert.Equal(HttpMethod.Get, method));
        Assert.Equal([state.ConfigPath], Directory.GetFiles(state.Root));
        Assert.False(File.Exists(Path.Combine(config.LibraryCoordinationDirectory, "writer.lock")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void HistoryStillWaitsForAMissingServerRootBeforeOpeningIt(bool diff)
    {
        var config = Config("server") with
        {
            StorageMode = StorageMode.Server, CloudJournalId = null,
            CoordinationRoot = @"\\127.0.0.1\medal-sync-synthetic-" + Guid.NewGuid().ToString("N"),
        };
        var state = HistoryState(config);
        var output = new StringWriter();
        Assert.Equal(3, History(diff, state, output, _ => throw new InvalidOperationException("An absent server must not be opened.")));
        Assert.Contains("Waiting for storage", output.ToString());
        Assert.Equal([state.ConfigPath], Directory.GetFiles(state.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("windows")]
    public void HistoryDistinguishesCloudOutageAccessRefusalAndInvalidConfiguration(bool diff)
    {
        using var drive = new Drive { Value = Initial(), Offline = true };
        var config = Config("second");
        var state = HistoryState(config);
        var output = new StringWriter();
        CoordinationStore Open(MedalSyncConfig c) => CoordinationStore.OpenCloud(c, new(c, Api(drive), Run));

        Assert.Equal(3, History(diff, state, output, Open));
        Assert.Contains("Waiting for storage", output.ToString());
        Assert.False(Directory.Exists(config.CoordinationRoot));

        drive.Offline = false;
        drive.Failure = HttpStatusCode.Unauthorized;
        output.GetStringBuilder().Clear();
        Assert.Throws<StorageActionRequiredException>(() => History(diff, state, output, Open));
        Assert.Equal("", output.ToString());

        drive.Failure = null;
        foreach (var hint in new[] { RcloneHints.SignIn, RcloneHints.Config })
        {
            var refusal = Assert.Throws<StorageActionRequiredException>(() => History(diff, state, output,
                c => CoordinationStore.OpenCloud(c, new(c, Api(drive),
                    _ => throw new RcloneFailedException("synthetic", 1, hints: hint, command: "lsjson")))));
            Assert.Contains(hint == RcloneHints.SignIn ? "sign-in" : "configuration", refusal.Message);
        }

        File.WriteAllBytes(state.ConfigPath, Json.Serialize(config with { CloudJournalId = null }));
        Assert.Throws<InvalidDataException>(() => History(diff, state, output,
            _ => throw new InvalidOperationException("An invalid configuration must not reach the provider.")));
        Assert.False(Directory.Exists(config.CoordinationRoot));
        Assert.Equal(0, drive.Writes);
        Assert.Equal([state.ConfigPath], Directory.GetFiles(state.Root));
    }

    [Fact]
    public void CloudConfigurationRequiresNoServerAndRoundTripsWithoutWeakeningExistingModes()
    {
        var config = Config("first");
        config.Validate();
        Assert.True(config.PrimaryIsRclone);
        Assert.Equal("gdrive:MedalSync/Clips", config.PrimaryCapture);
        Assert.Equal(config.Replication!.LocalCopyRoot, config.MountRemote);
        Json.Deserialize<MedalSyncConfig>(Json.Serialize(config), "config").Validate();
        Assert.Throws<InvalidDataException>((config with { CloudJournalId = null }).Validate);
        Assert.Throws<InvalidDataException>((config with { CoordinationRoot = @"\\server\journal" }).Validate);
        Assert.Throws<InvalidDataException>((config with { StorageMode = StorageMode.Server }).Validate);
        Assert.Throws<InvalidDataException>((config with { Replication = config.Replication with { LocalCopyRoot = null } }).Validate);
        Assert.Throws<InvalidDataException>((config with { CaptureFolder = @"M:\.medal-sync\Clips" }).Validate);
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, CommandRunner.Run(["config", "create", "--storage-mode", "cloud", "--account-id", "12345", "--new-library",
            "--remote", config.Remote, "--cloud-journal-id", FileId, "--local-copy-root", config.Replication.LocalCopyRoot!,
            "--rclone-config", config.Replication.RcloneConfigPath!, "--cache-root", config.CacheRoot, "--out", fixture.PathFor("candidate.json")], output, error));
        Assert.False(File.Exists(fixture.PathFor("candidate.json")));
        Assert.Contains("CLOUD", output.ToString());
    }

    [Fact]
    public void StatusViewKeepsTheOpenLibraryBetweenReadsAndReopensItAfterAConfigChangeOrFailure()
    {
        using var drive = new Drive();
        var config = Config("first");
        Seed(drive, config, Snapshot("seed", "one"));
        var state = new LocalState(fixture.PathFor("first", "state"));
        state.Device();
        File.WriteAllBytes(state.ConfigPath, Json.Serialize(config));
        int connections = 0;
        var source = new DesktopStatusSource(() => state, () => false, c =>
        {
            connections++;
            return new CloudCoordination(c, Api(drive), Run);
        });
        int commandsBefore = commands.Count;

        // Unreachable at first: each read is one failed request on the same connection and starts no rclone.
        drive.Offline = true;
        Assert.Throws<StorageOfflineException>(() => source.Read());
        Assert.Throws<StorageOfflineException>(() => source.Read());
        drive.Offline = false;
        Assert.Equal(1, connections);
        Assert.Empty(commands.Skip(commandsBefore));

        int readsBefore = drive.Reads;
        Assert.Equal(SessionPhase.Ready, source.Read().Phase);
        int firstReadRequests = drive.Reads - readsBefore;
        Assert.Equal(SessionPhase.Ready, source.Read().Phase);
        Assert.Equal(SessionPhase.Ready, source.Read().Phase);

        // Opening runs rclone once to find the Drive folder; later reads are one journal request each and run nothing.
        Assert.Equal(1, connections);
        Assert.Equal(["lsjson"], commands.Skip(commandsBefore));
        Assert.Equal(firstReadRequests + 2, drive.Reads - readsBefore);

        // An outage after opening keeps the open library too.
        drive.Offline = true;
        Assert.Throws<StorageOfflineException>(() => source.Read());
        drive.Offline = false;
        Assert.Equal(SessionPhase.Ready, source.Read().Phase);
        Assert.Equal(1, connections);

        // A journal for another Drive folder is refused, and the next read connects and opens the library afresh.
        var good = drive.Value!;
        drive.Value = good with { StorageFolderId = "another-drive-folder" };
        Assert.Throws<IncompatibleLibraryException>(() => source.Read());
        drive.Value = good;
        Assert.Equal(SessionPhase.Ready, source.Read().Phase);
        Assert.Equal(2, connections);
        Assert.Equal(2, commands.Skip(commandsBefore).Count());

        // So does any change to config.json.
        File.WriteAllBytes(state.ConfigPath, Json.Serialize(config with { MaxSessionHours = 3 }));
        Assert.Equal(SessionPhase.Ready, source.Read().Phase);
        Assert.Equal(3, connections);
    }

    [Fact]
    public void StaleConditionalClaimCannotOverwriteTheWinningPc()
    {
        using var drive = new Drive { Value = Initial() };
        var first = Api(drive); var second = Api(drive);
        var a = first.Read(); var b = second.Read();
        var winning = Ownership.Acquire(a.Value.Journal, First, new string('1', 32), Now);
        Assert.True(first.CompareExchange(a, a.Value with { Journal = winning }));
        Assert.False(second.CompareExchange(b, b.Value with { Journal = Ownership.Acquire(b.Value.Journal, Second, new string('2', 32), Now) }));
        Assert.Equal(First, drive.Value!.Journal.Owner);
        Assert.Equal(1, drive.Writes);
    }

    [Fact]
    public void EndpointThatIgnoresEtagNeverReceivesAnOwnershipMutation()
    {
        using var drive = new Drive { Value = Initial(), IgnoreConditions = true };
        var api = Api(drive); var before = api.Read();
        Assert.Throws<UnsafeOperationException>(() => api.CompareExchange(before, before.Value with
        { Journal = Ownership.Acquire(before.Value.Journal, First, new string('1', 32), Now) }));
        Assert.Equal(0, drive.Writes);
        Assert.Equal(OwnershipState.Clean, drive.Value!.Journal.State);
    }

    [Fact]
    public void LostAcknowledgementKeepsTheClaimAndNeverFallsBackToCachedReady()
    {
        using var drive = new Drive();
        var store = Seed(drive, Config("first"), Snapshot("seed", "one"));
        drive.ApplyThenDisconnect = true;
        Assert.Throws<StorageOfflineException>(() => store.Update(j => Ownership.Acquire(j, First, new string('1', 32), Now), TimeSpan.Zero));
        Assert.Equal(First, drive.Value!.Journal.Owner);
        drive.Offline = true;
        Assert.Throws<StorageOfflineException>(() => store.ReadJournal());
        drive.Offline = false;
        Assert.Equal(OwnershipState.Starting, store.ReadJournal().State);
        Assert.Throws<OwnershipConflictException>(() => store.Update(j => Ownership.Acquire(j, Second, new string('2', 32), Now), TimeSpan.Zero));
    }

    [Fact]
    public void WithdrawingAWaitRemovesOnlyThatExactRequest()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var second = CoordinationStore.OpenCloud(Config("second"), new(Config("second"), Api(drive), Run));
        var waited = new HandoffRequest(1, Second, new string('3', 32), Now);
        var newer = waited with { RequestId = new string('4', 32) };
        second.WriteRequest(waited);
        second.WriteRequest(newer);

        Assert.False(second.WithdrawRequest(waited));
        Assert.Equal(newer, Assert.Single(first.PendingRequests(Now)));
        Assert.True(second.WithdrawRequest(newer));
        Assert.Empty(first.PendingRequests(Now));
    }

    [Fact]
    public async Task SimultaneousStartsGrantExactlyOnePcAndPreserveTheLosersRequest()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var second = CoordinationStore.OpenCloud(Config("second"), new(Config("second"), Api(drive), Run));
        // Open both caches before racing, so only the journal CAS is under contention.
        int acquired = 0;
        async Task Claim(CoordinationStore store, DeviceIdentity device, char session)
        {
            await Task.Yield();
            try { store.Update(j => Ownership.Acquire(j, device, new string(session, 32), Now), TimeSpan.Zero); Interlocked.Increment(ref acquired); }
            catch (OwnershipConflictException) { store.WriteRequest(new(1, device, Guid.NewGuid().ToString("N"), Now)); }
        }
        await Task.WhenAll(Claim(first, First, '1'), Claim(second, Second, '2'));
        Assert.Equal(1, acquired);
        Assert.Single(first.PendingRequests(Now));
        Assert.Equal(first.ReadJournal(), second.ReadJournal());
    }

    [Fact]
    public void FailedGenerationUploadRetainsOwnershipAndAnotherPcCanResumeAfterVerifiedRetry()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        string session = new('1', 32);
        first.Update(j => Ownership.Acquire(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.MarkActive(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.BeginDrain(j, First, session, Now), TimeSpan.Zero);
        first.Update(j => Ownership.BeginPublish(j, First, session, Now), TimeSpan.Zero);
        var next = Snapshot("next", "one", "two");
        first.Generations.Publish(1, 0, session, First, next, new(1, [], []), Versions, Now);
        failUpload = true;
        Assert.Throws<RcloneFailedException>(() => first.Update(j => Ownership.CompletePublish(j, First, session, 1, Now), TimeSpan.Zero));
        Assert.Equal(OwnershipState.Publishing, drive.Value!.Journal.State);
        Assert.Equal(0, drive.Value.Journal.CommittedGeneration);
        failUpload = false;
        first.Update(j => Ownership.CompletePublish(j, First, session, 1, Now), TimeSpan.Zero);
        Assert.Contains("check", commands);
        var second = CoordinationStore.OpenCloud(Config("second"), new(Config("second"), Api(drive), Run));
        Assert.Equal(next.Digest(), second.Generations.Read(second.ReadJournal().CommittedGeneration).Snapshot.Digest());
        Assert.Equal(2, second.Generations.Chain(1).Count());
        second.Update(j => Ownership.Acquire(j, Second, new string('2', 32), Now), TimeSpan.Zero);
        Assert.Equal(Second, first.ReadJournal().Owner);
        Assert.Throws<UnsafeOperationException>(() => second.AcquireWriterLock(TimeSpan.Zero));
    }

    [Fact]
    public void AnotherAccountOrMediaFolderIsRefusedBeforeAnyJournalWrite()
    {
        using var drive = new Drive { Value = Initial() };
        var config = Config("first") with { AccountId = "999" };
        Assert.Throws<IncompatibleLibraryException>(() => new CloudCoordination(config, Api(drive), Run).Read());
        config = Config("first");
        Assert.Throws<IncompatibleLibraryException>(() => new CloudCoordination(config, Api(drive), _ => """{"ID":"wrong-folder","IsDir":true}""").Read());
        Assert.Equal(0, drive.Writes);
    }

    [Theory]
    [InlineData("alias")]
    [InlineData("onedrive")]
    public void OnlyDirectGoogleDriveOAuthRemotesCanCoordinate(string type)
    {
        var token = new DriveAccessToken("synthetic", "gdrive:MedalSync", "private.conf", execute: _ => JsonSerializer.Serialize(new
        { gdrive = new { type, token = "{}" } }));
        Assert.Throws<UnsafeOperationException>(() => token.Get());
    }

    [Fact]
    public void ExpiredOAuthTokenRefreshesThroughRcloneWithoutReturningConfigContents()
    {
        bool refreshed = false;
        List<string> calls = [];
        var token = new DriveAccessToken("synthetic", "gdrive:MedalSync", "private.conf", execute: args =>
        {
            calls.Add(string.Join(' ', args.Take(2)));
            if (args[0] == "about") { refreshed = true; return "{}"; }
            return JsonSerializer.Serialize(new { gdrive = new { type = "drive", token = JsonSerializer.Serialize(new
            { access_token = refreshed ? "fresh-token" : "expired-token", expiry = refreshed ? Now.AddHours(1) : Now.AddHours(-1) }) } });
        });
        Assert.Equal("fresh-token", token.Get());
        Assert.Equal("fresh-token", token.Get());
        Assert.Equal(new[] { "config dump", "about gdrive:", "config dump" }, calls);
    }

    [Fact]
    public void InterruptedInitializationResumesOnlyTheSameDeviceAndSnapshot()
    {
        using var drive = new Drive();
        var config = Config("first");
        var backend = new CloudCoordination(config, Api(drive), Run);
        var snapshot = Snapshot("seed", "one");
        failUpload = true;
        Assert.Throws<RcloneFailedException>(() => backend.Create(snapshot, new(1, [], []), First, new string('1', 32), Versions, Now));
        Assert.Null(drive.Value);
        failUpload = false;
        Assert.Throws<UnsafeOperationException>(() => backend.Create(snapshot, new(1, [], []), Second, new string('2', 32), Versions, Now));
        backend.Create(snapshot, new(1, [], []), First, new string('3', 32), Versions, Now.AddMinutes(1));
        Assert.Equal(OwnershipState.Clean, backend.ReadJournal().State);
        Assert.Equal(Now, drive.Value!.Identity.CreatedAt);
    }

    [Fact]
    public void PartialAbandonedRemoteGenerationIsKeptAndItsNumberIsNeverReused()
    {
        using var drive = new Drive();
        var config = Config("first");
        var store = Seed(drive, config, Snapshot("seed", "one"));
        string orphan = fixture.PathFor("drive", "MedalSync", ".medal-sync", LibraryId, "generations", GenerationStore.Name(1));
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "library.json"), "incomplete abandoned upload");
        string session = new('1', 32);
        store.Update(j => Ownership.Acquire(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.MarkActive(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.BeginDrain(j, First, session, Now), TimeSpan.Zero);
        store.Update(j => Ownership.BeginPublish(j, First, session, Now), TimeSpan.Zero);
        Assert.Equal(2, store.Generations.Next(0));
        Assert.False(store.Generations.Exists(1));
        Assert.True(File.Exists(Path.Combine(orphan, "library.json")));
        store.Generations.Publish(2, 0, session, First, Snapshot("new", "one", "two"), new(1, [], []), Versions, Now);
        store.Update(j => Ownership.CompletePublish(j, First, session, 2, Now), TimeSpan.Zero);
        var other = Config("second");
        var second = CoordinationStore.OpenCloud(other, new(other, Api(drive), Run));
        Assert.Equal(2, second.ReadJournal().CommittedGeneration);
        Assert.Equal(2, second.Generations.Chain(2).Count());
    }

    [Fact]
    public void DuplicateDriveHistoryPathsRefuseBeforeCopyOrOwnershipChanges()
    {
        using var drive = new Drive { Value = Initial() };
        var backend = new CloudCoordination(Config("first"), Api(drive), args =>
        {
            Assert.Equal("lsjson", args[0]);
            return """[{"Path":"0000000000/library.json"},{"Path":"0000000000/library.json"}]""";
        });
        Assert.Throws<InvalidDataException>(backend.Download);
        Assert.Equal(0, drive.Writes);
    }

    [Fact]
    public void CachedUnpublishedGenerationCannotMasqueradeAsAnotherPcsCommittedGenerationWithTheSameNumber()
    {
        using var drive = new Drive();
        var first = Seed(drive, Config("first"), Snapshot("seed", "one"));
        var original = Snapshot("unpublished", "one", "private");
        first.Generations.Publish(1, 0, new string('1', 32), First, original, new(1, [], []), Versions, Now);
        var other = Config("second");
        var second = CoordinationStore.OpenCloud(other, new(other, Api(drive), Run));
        string session = new('2', 32);
        second.Update(j => Ownership.Acquire(j, Second, session, Now), TimeSpan.Zero);
        second.Update(j => Ownership.MarkActive(j, Second, session, Now), TimeSpan.Zero);
        second.Update(j => Ownership.BeginDrain(j, Second, session, Now), TimeSpan.Zero);
        second.Update(j => Ownership.BeginPublish(j, Second, session, Now), TimeSpan.Zero);
        second.Generations.Publish(1, 0, session, Second, Snapshot("published", "one", "shared"), new(1, [], []), Versions, Now);
        second.Update(j => Ownership.CompletePublish(j, Second, session, 1, Now), TimeSpan.Zero);
        Assert.Throws<InvalidDataException>(first.ReadJournal);
        Assert.Equal(original.Digest(), first.Generations.Read(1).Snapshot.Digest());
        Assert.Equal(OwnershipState.Clean, second.ReadJournal().State);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; private set; } = CloudHandoffTests.Now;
        public void Sleep(TimeSpan duration) => Now += duration;
    }
    private sealed class Medal : IMedalProcess
    {
        public bool Running;
        public bool IsRunning() => Running;
        public void Launch() => Running = true;
    }
    private sealed class Mount : IClipMount
    {
        public bool IsRunning { get; private set; }
        public void Start(string session, bool forRecording) => IsRunning = true;
        public MountHealth Health() => new(true, "synthetic");
        public DrainVerdict Drain() => new(DrainState.Drained, [], 0, 0);
        public bool Stop() { IsRunning = false; return true; }
        public void Forget(string session, long generation) { }
        public void RetainAbortedStart(string session) { }
    }
    private sealed class Assets : IAssetChecker
    {
        private static AssetReport Report(LibrarySnapshot snapshot) => new(AssetInventory.Collect(snapshot)
            .Select(r => new AssetStatus(r, AssetProblem.None, 1, DateTime.UnixEpoch, null)).ToArray());
        public AssetReport ThroughMount(LibrarySnapshot snapshot) => Report(snapshot);
        public AssetReport OnServer(LibrarySnapshot snapshot) => Report(snapshot);
        public bool ExistsOnServer(string capturePath) => true;
        public void RequireServer() { }
    }

    [Fact]
    public void NormalCoordinatorLaunchFinishAndSecondPcRestoreUseCloudOwnership()
    {
        using var drive = new Drive();
        var config = Config("first");
        var store = Seed(drive, config, Snapshot("seed", "one"));
        var otherConfig = Config("second");
        var otherStore = CoordinationStore.OpenCloud(otherConfig, new(otherConfig, Api(drive), Run));
        var clock = new Clock();
        var firstMedal = new Medal(); var secondMedal = new Medal();
        string firstDb = fixture.CreateLibrary("first-db"), secondDb = fixture.CreateLibrary("second-db");
        using (var connection = MedalFixture.Open(secondDb)) MedalFixture.SetSetting(connection, "ClipFolder", JsonSerializer.Serialize(@"C:\OriginalClips"));
        string settingsBefore = MedalFixture.Settings(secondDb);
        SessionCoordinator Pc(CoordinationStore journal, DeviceIdentity device, string db, Medal medal, string name) => new(journal,
            new SqliteLocalLibrary(db, MedalFixture.AccountId, fixture.PathFor(name, "backups"), clock, @"M:\Clips"), new Mount(), medal,
            new Assets(), clock, new(device, config.Capture, Versions, TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1)));
        var first = Pc(store, First, firstDb, firstMedal, "first");
        var second = Pc(otherStore, Second, secondDb, secondMedal, "second");
        var started = first.Launch();
        Assert.Equal(LaunchStatus.Started, started.Status);
        Assert.Equal(LaunchStatus.Waiting, second.Launch().Status);
        MedalFixture.AddContent(firstDb, "two", video: @"M:\Clips\two.mp4");
        firstMedal.Running = false;
        Assert.Equal(FinishStatus.Published, first.Finish(started.Session!).Status);
        Assert.Equal(LaunchStatus.Started, second.Launch().Status);
        Assert.Equal(2, LibraryDatabase.ReadSnapshot(secondDb, MedalFixture.AccountId).RowCount("contents"));
        var library = new SqliteLocalLibrary(secondDb, MedalFixture.AccountId, fixture.PathFor("unused"), clock);
        Assert.Equal(@"M:\Clips\", library.CaptureFolder());
        using (var connection = MedalFixture.Open(secondDb)) MedalFixture.SetSetting(connection, "ClipFolder", JsonSerializer.Serialize(@"C:\OriginalClips"));
        Assert.Equal(settingsBefore, MedalFixture.Settings(secondDb)); // All other device settings survived the restore.
        Assert.NotEmpty(DatabaseBackup.List(fixture.PathFor("second", "backups"), MedalFixture.AccountId));
    }

    private sealed class LaunchMount : IClipMount
    {
        public bool IsRunning { get; private set; }
        public List<string> Starts { get; } = [];
        public List<string> Retained { get; } = [];
        public List<string> Forgotten { get; } = [];
        public void Start(string session, bool forRecording) { Starts.Add(session); IsRunning = true; }
        public MountHealth Health() => new(true, "synthetic");
        public DrainVerdict Drain() => new(DrainState.Drained, [], 0, 0);
        public bool Stop() { IsRunning = false; return true; }
        public void Forget(string session, long generation) => Forgotten.Add(session);
        public void RetainAbortedStart(string session) => Retained.Add(session);
    }

    private sealed class LaunchMedal : IMedalProcess
    {
        public int Launches { get; private set; }
        public bool IsRunning() => false;
        public void Launch() => Launches++;
    }

    private sealed class NoAssets : IAssetChecker
    {
        private static AssetReport Report(LibrarySnapshot snapshot) => new(AssetInventory.Collect(snapshot)
            .Select(r => new AssetStatus(r, AssetProblem.None, 1, DateTime.UnixEpoch, null)).ToArray());
        public AssetReport ThroughMount(LibrarySnapshot snapshot) => Report(snapshot);
        public AssetReport OnServer(LibrarySnapshot snapshot) => Report(snapshot);
        public bool ExistsOnServer(string capturePath) => true;
        public void RequireServer() { }
    }

    /// <summary>A cloud launch on the first PC; <paramref name="beforeActive"/> runs after storage is prepared, just before the session is marked Active.</summary>
    private (CoordinationStore Store, LaunchMount Mount, LaunchMedal Medal, LaunchOutcome Outcome) CloudLaunch(Drive drive, Action<CoordinationStore> beforeActive)
    {
        var store = Seed(drive, Config("first"), Snapshot("seed"));
        var mount = new LaunchMount();
        var medal = new LaunchMedal();
        var clock = new SystemClock();
        var coordinator = new SessionCoordinator(store,
            new SqliteLocalLibrary(fixture.CreateLibrary("launching"), MedalFixture.AccountId, fixture.PathFor("first", "backups"), clock),
            mount, medal, new NoAssets(), clock,
            new SessionOptions(First, WindowsPath.Parse(@"M:\Clips"), Versions, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(1)),
            prepareNativeBackup: () => beforeActive(store));
        return (store, mount, medal, coordinator.Launch());
    }

    [Fact]
    public void LaunchGivesTheStartBackWhenTheActiveWriteIsLost()
    {
        using var drive = new Drive();
        bool dropped = false;
        var (_, mount, medal, outcome) = CloudLaunch(drive, _ => drive.DropWrite = () => !dropped && (dropped = true));

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.True(outcome.StorageOffline);
        Assert.DoesNotContain("stay on this PC", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, medal.Launches);
        Assert.False(mount.IsRunning);
        Assert.Equal(mount.Starts, mount.Retained);
        Assert.Equal(OwnershipState.Clean, drive.Value!.Journal.State);
        Assert.Null(drive.Value.Journal.Owner);
    }

    [Fact]
    public void LaunchKeepsOwnershipAndCacheWhenTheDriveStaysUnreachableAfterTheActiveWriteFails()
    {
        using var drive = new Drive();
        var (_, mount, medal, outcome) = CloudLaunch(drive, _ => drive.Offline = true);
        drive.Offline = false;

        Assert.Equal(LaunchStatus.Waiting, outcome.Status);
        Assert.True(outcome.StorageOffline);
        Assert.Contains("stay on this PC", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(0, medal.Launches);
        Assert.False(mount.IsRunning);
        Assert.Empty(mount.Retained);
        Assert.Empty(mount.Forgotten);
        Assert.Equal(OwnershipState.Starting, drive.Value!.Journal.State);
        Assert.Equal(First, drive.Value.Journal.Owner);
    }

    [Fact]
    public void LaunchStartsMedalWhenItsOwnSwitchRequestCannotBeRemoved()
    {
        using var drive = new Drive();
        // Every write after the Active one, which is this PC's own request removal, is lost.
        int writesBefore = 0;
        var (store, mount, medal, outcome) = CloudLaunch(drive, s =>
        {
            s.WriteRequest(new HandoffRequest(1, First, new string('9', 32), DateTimeOffset.UtcNow));
            writesBefore = drive.Writes;
            drive.DropWrite = () => drive.Writes > writesBefore;
        });

        Assert.True(outcome.Status == LaunchStatus.Started, $"{outcome.Status}: {outcome.Message}");
        Assert.Equal(1, medal.Launches);
        Assert.True(mount.IsRunning);
        drive.DropWrite = null;
        Assert.Equal(OwnershipState.Active, drive.Value!.Journal.State);
        Assert.Single(drive.Value.Requests);
    }
}
