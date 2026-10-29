using System.Diagnostics;
using MedalSync.Config;
using MedalSync.Library;
using MedalSync.Platform;
using MedalSync.Session;
using MedalSync.Storage;
using MedalSync.Tests.Fixtures;
using MedalSync.Util;

namespace MedalSync.Tests;

/// <summary>Disks mounted in folders, extra drive letters, removable and network drives, file systems and rclone remote names.</summary>
public sealed class StorageCompatibilityTests : IDisposable
{
    private const long GiB = 1L << 30;
    private readonly MedalFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    private static MedalSyncConfig Config() => new(1, "0123456789abcdef0123456789abcdef", "12345",
        @"\\server\Data\Coordination", @"\\server\Data\Videos", @"M:\Clips", @"E:\Cache", @"C:\Tools\rclone.exe",
        new MountConfig(5, 20, 20, 300, 30, 1000, 2, 53682), 4, []);

    private static VolumeInfo Volume(string mount, string identity, long freeGiB, string fileSystem = "NTFS", VolumeKind kind = VolumeKind.Fixed) =>
        new(mount, identity, kind, fileSystem, freeGiB * GiB, 2000 * GiB);

    /// <summary>Answers from the longest matching mount, as Windows does for a disk mounted inside a folder.</summary>
    private static Func<string, VolumeInfo?> Volumes(params VolumeInfo[] volumes) => path =>
        volumes.Where(v => path.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.MountPath.Length).FirstOrDefault();

    [Theory]
    [InlineData("FAT32", true)]
    [InlineData("FAT", true)]
    [InlineData("vfat", true)]
    [InlineData("exFAT", false)]
    [InlineData("NTFS", false)]
    [InlineData("ReFS", false)]
    public void FatVolumesCannotHoldLongRecordings(string fileSystem, bool refused)
    {
        foreach (var role in new[] { StorageRole.Cache, StorageRole.Media })
        {
            var problems = StorageVolume.Problems(role, @"F:\Medal", Volume(@"F:\", "F", 100, fileSystem, VolumeKind.Removable));
            Assert.Equal(refused, problems.Any(p => p.Contains("4 GiB", StringComparison.Ordinal)));
            Assert.Equal(refused ? 1 : 0, problems.Count);
        }
    }

    [Theory]
    [InlineData("Network", true, false)]
    [InlineData("RamDisk", true, true)]
    [InlineData("Optical", true, true)]
    [InlineData("Removable", false, false)]
    [InlineData("Fixed", false, false)]
    [InlineData("Unknown", false, false)]
    public void CacheMustBeInsideThisPcWhilePersistentClipsMayUseAMappedShare(string kind, bool cacheRefused, bool mediaRefused)
    {
        var volume = Volume(@"Z:\", "Z", 100, kind: Enum.Parse<VolumeKind>(kind));
        Assert.Equal(cacheRefused, StorageVolume.Problems(StorageRole.Cache, @"Z:\Cache", volume).Count > 0);
        Assert.Equal(mediaRefused, StorageVolume.Problems(StorageRole.Media, @"Z:\Library", volume).Count > 0);
    }

    [Fact]
    public void RecordingAdmissionMeasuresADiskMountedInsideAFolderNotItsDriveLetter()
    {
        var volumes = Volumes(Volume(@"C:\", "NTFS:00000001", 1), Volume(@"C:\Disks\Fast\", "NTFS:00000002", 500));
        var mounted = Config() with { CacheRoot = @"C:\Disks\Fast\MedalCache" };
        var verdict = Assert.Single(CapacityAdmission.ForRecording(mounted, mounted.SessionCacheDirectory(new string('1', 32)), 8, 0, volumes));
        Assert.True(verdict.Admitted, verdict.Summary);
        var system = Config() with { CacheRoot = @"C:\MedalCache" };
        Assert.False(Assert.Single(CapacityAdmission.ForRecording(system, system.SessionCacheDirectory(new string('1', 32)), 8, 0, volumes)).Admitted);
    }

    [Fact]
    public void TwoLettersForOneDiskShareOneBudget()
    {
        // 8 Mb/s for 4 hours is 14.4 GB per copy; each 40 GiB letter covers one copy and the 20 GiB reserve, not two.
        var config = Config() with { Replication = new(@"S:\MedalLibrary", null, []) };
        var separate = CapacityAdmission.ForRecording(config, @"E:\Cache\session", 8, 0,
            Volumes(Volume(@"E:\", "NTFS:000000AA", 40), Volume(@"S:\", "NTFS:000000BB", 40)));
        Assert.Equal(2, separate.Count);
        Assert.All(separate, v => Assert.True(v.Admitted, v.Summary));
        var shared = Assert.Single(CapacityAdmission.ForRecording(config, @"E:\Cache\session", 8, 0,
            Volumes(Volume(@"E:\", "NTFS:000000AA", 40), Volume(@"S:\", "ntfs:000000aa", 40))));
        Assert.False(shared.Admitted);
    }

    [Fact]
    public void UnpluggedPersistentDiskWaitsAndUnsuitableDisksAreRefusedBeforeRecording()
    {
        var config = Config() with { StorageMode = StorageMode.Local, Remote = @"F:\MedalLibrary", CoordinationRoot = @"C:\State\coordination" };
        Assert.Equal(@"F:\MedalLibrary", config.PersistentMediaRoot);
        var offline = Assert.Throws<StorageOfflineException>(() =>
            CapacityAdmission.ForRecording(config, @"E:\Cache\session", 8, 0, Volumes(Volume(@"E:\", "NTFS:1", 500))));
        Assert.Contains(@"F:\", offline.Message, StringComparison.Ordinal);
        Assert.Throws<StorageOfflineException>(() => CapacityAdmission.ForRecording(config, @"E:\Cache\session", 8, 0, _ => null));
        var fat = Assert.Throws<UnsafeOperationException>(() => CapacityAdmission.ForRecording(config, @"E:\Cache\session", 8, 0,
            Volumes(Volume(@"E:\", "NTFS:1", 500), Volume(@"F:\", "FAT32:2", 500, "FAT32", VolumeKind.Removable))));
        Assert.Contains("4 GiB", fat.Message, StringComparison.Ordinal);
        var network = Assert.Throws<UnsafeOperationException>(() => CapacityAdmission.ForRecording(config, @"E:\Cache\session", 8, 0,
            Volumes(Volume(@"E:\", @"\\NAS\CACHE\", 500, kind: VolumeKind.Network), Volume(@"F:\", "NTFS:2", 500))));
        Assert.Contains("network drive", network.Message, StringComparison.Ordinal);
        Assert.Equal(2, CapacityAdmission.ForRecording(config, @"E:\Cache\session", 8, 0,
            Volumes(Volume(@"E:\", "NTFS:1", 500), Volume(@"F:\", "exFAT:2", 500, "exFAT", VolumeKind.Removable))).Count);
        Assert.Null(Config().PersistentMediaRoot);
    }

    private sealed class SizedPrimary(long bytes) : IAssetChecker
    {
        public void RequireServer() { }
        public bool ExistsOnServer(string capturePath) => true;
        public long? SizeOnServer(string capturePath) => bytes;
        public AssetReport ThroughMount(LibrarySnapshot snapshot) => throw new NotSupportedException();
        public AssetReport OnServer(LibrarySnapshot snapshot) => throw new NotSupportedException();
    }

    [Fact]
    public void HydrationWaitsForAnUnpluggedDiskAndRefusesUnsuitableOrTooSmallDisksBeforeDownloading()
    {
        var config = Config() with { Remote = "onedrive:MedalSync", Replication = new(@"Q:\MedalLibrary", @"C:\Private\rclone.conf", []) };
        string db = _fixture.CreateLibrary("source");
        MedalFixture.AddContent(db, "a", video: @"M:\Clips\one.mp4");
        MedalFixture.AddContent(db, "b", video: @"M:\Clips\two.mp4");
        var snapshot = LibraryDatabase.ReadSnapshot(db, MedalFixture.AccountId);
        var state = new LocalState(_fixture.PathFor("state"));
        StorageReplication Replication(long bytes, Func<string, VolumeInfo?> volumes) =>
            new(config, state, () => { }, _ => Assert.Fail("Nothing may be downloaded."), primary: new SizedPrimary(bytes), volumes: volumes);

        var unplugged = Assert.Throws<StorageOfflineException>(() => Replication(GiB, _ => null).Prepare(snapshot));
        Assert.Contains(@"Q:\", unplugged.Message, StringComparison.Ordinal);
        Assert.Throws<UnsafeOperationException>(() => Replication(GiB, Volumes(Volume(@"Q:\", "FAT32:1", 500, "FAT32"))).Prepare(snapshot));
        // Two 5 GiB clips do not fit in 25 GiB while keeping the 20 GiB reserve.
        var small = Assert.Throws<UnsafeOperationException>(() => Replication(5 * GiB, Volumes(Volume(@"Q:\", "NTFS:1", 25))).Prepare(snapshot));
        Assert.Contains("2 clip file(s)", small.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(config.MountedCapture.Display));
        Assert.False(Directory.Exists(state.Root));
    }

    [Fact]
    public void SetupReviewAcceptsAnRclonePrimaryAndChecksTheVolumesThatHoldEachFolder()
    {
        string credentials = _fixture.PathFor("rclone.conf");
        File.WriteAllText(credentials, "[synthetic]\n");
        var config = Config() with { Remote = "sftp-home:/srv/Medal", Replication = new(@"F:\MedalLibrary", credentials, []) };
        var volumes = Volumes(Volume(@"E:\", "NTFS:1", 500), Volume(@"F:\", "exFAT:2", 500, "exFAT", VolumeKind.Removable));
        List<string> checkedFolders = [];
        Assert.Empty(StorageReadiness.Problems(config, folder => { checkedFolders.Add(folder); return true; }, volumes));
        Assert.DoesNotContain(config.Remote, checkedFolders);
        Assert.Contains(@"F:\MedalLibrary", checkedFolders);
        Assert.Contains(config.CoordinationRoot, checkedFolders);

        var tight = Volumes(Volume(@"E:\", "NTFS:1", 10), Volume(@"F:\", "FAT32:2", 500, "FAT32"));
        var problems = StorageReadiness.Problems(config, _ => true, tight);
        Assert.Contains(problems, p => p.Contains("free-space reserve", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("4 GiB", StringComparison.Ordinal));

        File.Delete(credentials);
        Assert.Contains(StorageReadiness.Problems(config, _ => true, volumes), p => p.Contains("provider configuration file", StringComparison.Ordinal));
        var unavailable = StorageReadiness.Problems(Config(), _ => false, volumes);
        Assert.Equal(3, unavailable.Count(p => p.StartsWith("Storage folder is unavailable", StringComparison.Ordinal)));
    }

    [Fact]
    public void InventoryReportsTheRealVolumeAndBlocksUnsuitableStorage()
    {
        var config = Config() with { Replication = new(@"F:\MedalLibrary", null, []) };
        var summaries = StorageVolume.Summaries(config, Volumes(Volume(@"C:\Disks\Fast\", "NTFS:1", 500), Volume(@"F:\", "FAT32:2", 500, "FAT32")));
        Assert.Equal(new[] { "cache", "persistent clips" }, summaries.Select(s => s.Role));
        Assert.Contains(summaries, s => s.Role == "cache" && s.Volume is null && s.Problems.Single().Contains("not connected", StringComparison.Ordinal));
        var persistent = summaries.Single(s => s.Role == "persistent clips");
        Assert.Equal("FAT32", persistent.FileSystem);
        Assert.Contains("4 GiB", Assert.Single(persistent.Problems), StringComparison.Ordinal);

        var report = new InventoryReport(DateTimeOffset.UnixEpoch, "PC", "user", "Windows", "1", "2", MedalRunning: false,
            Library: null, LibraryError: "not found", CaptureTree: null, Drives: [], ProposedDrive: "M:\\",
            ProposedDriveFree: true, Capacity: null, StartupEntries: new Dictionary<string, string?>(),
            SyncFolders: [], SyncExclusions: [], Dependencies: new Dictionary<string, string>(),
            Server: new Dictionary<string, string>(), StorageVolumes: summaries);
        string text = InventoryText.Render(report);
        Assert.Contains(@"Storage persistent clips F:\MedalLibrary: FAT32 Fixed volume F:\, 536.9 GB free", text, StringComparison.Ordinal);
        string blockers = text[text.IndexOf("Blockers before Gate B:", StringComparison.Ordinal)..];
        Assert.Contains("4 GiB", blockers, StringComparison.Ordinal);
        Assert.Contains("not connected", blockers, StringComparison.Ordinal);
        Assert.Empty(StorageVolume.Summaries(null));
    }

    [Fact]
    public void DescribesTheRealVolumeOfAFolderThatDoesNotExistYet()
    {
        string existing = _fixture.PathFor("cache");
        Directory.CreateDirectory(existing);
        var volume = StorageVolume.Describe(existing);
        var planned = StorageVolume.Describe(Path.Combine(existing, "library", "session"));
        Assert.NotNull(volume);
        Assert.NotNull(planned);
        Assert.True(StorageVolume.SameVolume(volume, planned));
        Assert.Equal(volume.MountPath, planned.MountPath);
        Assert.True(volume.TotalBytes > 0 && volume.FreeBytes >= 0);
        Assert.False(Directory.Exists(Path.Combine(existing, "library")));
        if (!OperatingSystem.IsWindows()) return;
        Assert.False(string.IsNullOrEmpty(volume.FileSystem));
        Assert.EndsWith(@"\", volume.MountPath, StringComparison.Ordinal);
        string? absent = Enumerable.Range('D', 23).Select(c => $"{(char)c}:\\").FirstOrDefault(root => !Directory.Exists(root));
        if (absent is not null) Assert.Null(StorageVolume.Describe(absent + @"MedalLibrary\Clips"));
    }

    [Fact]
    public void SubstLetterIsRecognizedAsTheSameVolume()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = _fixture.PathFor("aliased");
        Directory.CreateDirectory(folder);
        string? letter = Enumerable.Range('P', 10).Select(c => $"{(char)c}:").FirstOrDefault(l => !Directory.Exists(l + "\\"));
        if (letter is null || Subst(letter, folder) != 0) return; // No free letter, or subst is unavailable on this machine.
        try
        {
            var alias = StorageVolume.Describe(letter + @"\Cache");
            var original = StorageVolume.Describe(folder);
            Assert.NotNull(alias);
            Assert.NotNull(original);
            Assert.True(StorageVolume.SameVolume(alias, original), $"{alias.Identity} != {original.Identity}");
        }
        finally { Subst(letter, "/D"); }
    }

    private static int Subst(string letter, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("subst.exe") { ArgumentList = { letter, target }, UseShellExecute = false, CreateNoWindow = true })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    [Theory]
    [InlineData("my nas:Media", true)]
    [InlineData("onedrive.work:MedalSync", true)]
    [InlineData("gdrive+family@home:Clips", true)]
    [InlineData("Ünïcode_drive:Medal", true)]
    [InlineData("_private:Medal", true)]
    [InlineData("sftp-home:/srv/media/Medal", true)]
    [InlineData("sftp-home:/srv/media/Medal/", true)]
    [InlineData("X:/srv", false)]
    [InlineData(" nas:Media", false)]
    [InlineData("nas :Media", false)]
    [InlineData("-nas:Media", false)]
    [InlineData(".nas:Media", false)]
    [InlineData("nas://srv", false)]
    [InlineData("nas:/", false)]
    [InlineData("nas:/../srv", false)]
    [InlineData("nas:/srv//Medal", false)]
    public void RemoteNamesFollowRcloneRulesWithoutLookingLikeFlagsOrDrives(string remote, bool valid) =>
        Assert.Equal(valid, ReplicationConfig.ValidRemote(remote));

    [Fact]
    public void AbsoluteServerFoldersWorkAsPrimaryAndStillCannotOverlapReplicas()
    {
        var replication = new ReplicationConfig(@"E:\MedalLibrary", @"C:\Private\rclone.conf", []);
        var config = Config() with { Remote = "my nas:/srv/Media", Replication = replication };
        config.Validate();
        Assert.True(config.PrimaryIsRclone);
        Assert.Equal("my nas:/srv/Media/Clips", config.PrimaryCapture);
        Assert.Throws<InvalidDataException>((config with { Replication = replication with { Targets = [new("copy", "my nas:/srv/Media/Copy")] } }).Validate);
        (config with { Replication = replication with { Targets = [new("copy", "onedrive.work:MedalSync")] } }).Validate();
    }

    [Fact]
    public void AtomicReplaceWaitsOutABriefLockFromAnotherProgram()
    {
        string path = _fixture.PathFor("journal.json");
        File.WriteAllText(path, "old");
        string replacement = _fixture.PathFor("journal.new");
        File.WriteAllText(replacement, "new");
        // Like a virus scanner or indexer: open for reading without delete sharing.
        var scanner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        int waits = 0;
        try { AtomicFile.MoveOver(replacement, path, sleep: _ => { waits++; scanner.Dispose(); }); }
        finally { scanner.Dispose(); }
        Assert.Equal("new", File.ReadAllText(path));
        Assert.False(File.Exists(replacement));
        if (OperatingSystem.IsWindows()) Assert.Equal(1, waits);
    }

    [Fact]
    public void AtomicReplaceStillReportsALockThatDoesNotClear()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = _fixture.PathFor("journal.json");
        File.WriteAllText(path, "old");
        string replacement = _fixture.PathFor("journal.new");
        File.WriteAllText(replacement, "new");
        int waits = 0;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.ThrowsAny<Exception>(() => AtomicFile.MoveOver(replacement, path, sleep: _ => waits++));
            Assert.True(AtomicFile.IsTransientLock(error), error.ToString());
        }
        Assert.Equal(7, waits);
        Assert.Equal("old", File.ReadAllText(path));
        Assert.Equal("new", File.ReadAllText(replacement));
    }

    [Fact]
    public void OnlyLockingFailuresAreRetried()
    {
        Assert.True(AtomicFile.IsTransientLock(new UnauthorizedAccessException()));
        Assert.True(AtomicFile.IsTransientLock(new IOException("sharing violation", unchecked((int)0x80070020))));
        Assert.True(AtomicFile.IsTransientLock(new IOException("lock violation", unchecked((int)0x80070021))));
        Assert.False(AtomicFile.IsTransientLock(new IOException("disk full", unchecked((int)0x80070070))));
        Assert.False(AtomicFile.IsTransientLock(new FileNotFoundException()));
        Assert.False(AtomicFile.IsTransientLock(new InvalidDataException()));
    }
}
