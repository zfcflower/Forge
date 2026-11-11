using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

public class TrashServiceTests
{
    private static readonly DateTime FixedNow = new(2026, 3, 7, 9, 5, 2, DateTimeKind.Local);

    private static string MakeTree(TestDirectory directory, string name, string content = "payload")
    {
        var root = directory.File(name);
        Directory.CreateDirectory(Path.Combine(root, "sub", "deeper"));
        File.WriteAllText(Path.Combine(root, "a.txt"), content);
        File.WriteAllText(Path.Combine(root, "sub", "deeper", "b.txt"), content + "-deep");
        return root;
    }

    private static string InfoOf(string trashRoot, string name) =>
        File.ReadAllText(Path.Combine(trashRoot, "info", name + ".trashinfo"));

    [Fact]
    public async Task Xdg_moves_a_directory_with_all_content_and_removes_the_source()
    {
        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");
        var trashRoot = directory.File("Trash");

        await new XdgTrash(trashRoot, () => FixedNow).MoveToTrashAsync(source, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        var moved = Path.Combine(trashRoot, "files", "wt");
        Assert.Equal("payload", File.ReadAllText(Path.Combine(moved, "a.txt")));
        Assert.Equal("payload-deep", File.ReadAllText(Path.Combine(moved, "sub", "deeper", "b.txt")));
    }

    [Fact]
    public async Task Xdg_moves_a_single_file()
    {
        using var directory = new TestDirectory();
        var source = directory.File("note.txt");
        File.WriteAllText(source, "one file");
        var trashRoot = directory.File("Trash");

        await new XdgTrash(trashRoot, () => FixedNow).MoveToTrashAsync(source, CancellationToken.None);

        Assert.False(File.Exists(source));
        Assert.Equal("one file", File.ReadAllText(Path.Combine(trashRoot, "files", "note.txt")));
        Assert.True(File.Exists(Path.Combine(trashRoot, "info", "note.txt.trashinfo")));
    }

    [Fact]
    public async Task Xdg_trashinfo_has_the_freedesktop_format()
    {
        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");
        var trashRoot = directory.File("Trash");

        await new XdgTrash(trashRoot, () => FixedNow).MoveToTrashAsync(source, CancellationToken.None);

        var info = InfoOf(trashRoot, "wt");
        var lines = info.Split('\n');
        Assert.Equal("[Trash Info]", lines[0]);
        Assert.Equal("Path=" + XdgTrash.EncodePath(Path.GetFullPath(source)), lines[1]);
        Assert.Equal("DeletionDate=2026-03-07T09:05:02", lines[2]);
        Assert.Equal(string.Empty, lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.DoesNotContain('\r', info);
        Assert.NotEqual(0xEF, File.ReadAllBytes(Path.Combine(trashRoot, "info", "wt.trashinfo"))[0]);
    }

    [Fact]
    public void Xdg_path_is_percent_encoded_per_segment_and_keeps_separators()
    {
        Assert.Equal("/home/u/my%20proj/%E9%A1%B9%E7%9B%AE/a%25b%23c%2Bd", XdgTrash.EncodePath("/home/u/my proj/项目/a%b#c+d"));
        Assert.Equal("/plain/Name-1_2.~x", XdgTrash.EncodePath("/plain/Name-1_2.~x"));
    }

    [Fact]
    public async Task Xdg_path_in_the_info_file_decodes_back_to_the_original_location()
    {
        using var directory = new TestDirectory();
        var source = MakeTree(directory, "my wt #1 项目");
        var trashRoot = directory.File("Trash");

        await new XdgTrash(trashRoot, () => FixedNow).MoveToTrashAsync(source, CancellationToken.None);

        var pathLine = InfoOf(trashRoot, "my wt #1 项目").Split('\n')[1];
        Assert.StartsWith("Path=", pathLine);
        Assert.DoesNotContain(' ', pathLine);
        Assert.DoesNotContain('#', pathLine);
        var decoded = Uri.UnescapeDataString(pathLine["Path=".Length..]);
        Assert.Equal(Path.GetFullPath(source).Replace('\\', '/'), decoded);
    }

    [Fact]
    public async Task Xdg_name_collisions_get_distinct_suffixes_with_matching_info_files()
    {
        using var directory = new TestDirectory();
        var trashRoot = directory.File("Trash");
        var trash = new XdgTrash(trashRoot, () => FixedNow);
        var first = MakeTree(directory, "a" + Path.DirectorySeparatorChar + "wt", "first");
        var second = MakeTree(directory, "b" + Path.DirectorySeparatorChar + "wt", "second");
        var third = MakeTree(directory, "c" + Path.DirectorySeparatorChar + "wt", "third");

        await trash.MoveToTrashAsync(first, CancellationToken.None);
        await trash.MoveToTrashAsync(second, CancellationToken.None);
        await trash.MoveToTrashAsync(third, CancellationToken.None);

        var files = Path.Combine(trashRoot, "files");
        Assert.Equal("first", File.ReadAllText(Path.Combine(files, "wt", "a.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(files, "wt.2", "a.txt")));
        Assert.Equal("third", File.ReadAllText(Path.Combine(files, "wt.3", "a.txt")));
        Assert.Contains(XdgTrash.EncodePath(Path.GetFullPath(second)), InfoOf(trashRoot, "wt.2"));
        Assert.Contains(XdgTrash.EncodePath(Path.GetFullPath(third)), InfoOf(trashRoot, "wt.3"));
    }

    [Fact]
    public async Task Xdg_stale_info_file_without_an_item_still_forces_a_new_name()
    {
        using var directory = new TestDirectory();
        var trashRoot = directory.File("Trash");
        Directory.CreateDirectory(Path.Combine(trashRoot, "info"));
        var stale = Path.Combine(trashRoot, "info", "wt.trashinfo");
        File.WriteAllText(stale, "stale");
        var source = MakeTree(directory, "wt");

        await new XdgTrash(trashRoot, () => FixedNow).MoveToTrashAsync(source, CancellationToken.None);

        Assert.Equal("stale", File.ReadAllText(stale));
        Assert.True(Directory.Exists(Path.Combine(trashRoot, "files", "wt.2")));
    }

    [Fact]
    public async Task Xdg_missing_source_throws_and_leaves_no_info_file()
    {
        using var directory = new TestDirectory();
        var trashRoot = directory.File("Trash");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new XdgTrash(trashRoot, () => FixedNow).MoveToTrashAsync(directory.File("nope"), CancellationToken.None));

        Assert.False(Directory.Exists(trashRoot) && Directory.GetFiles(trashRoot, "*", SearchOption.AllDirectories).Length > 0);
    }

    [Fact]
    public async Task Xdg_failed_move_rolls_the_info_file_back_and_keeps_the_source()
    {
        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");
        var trashRoot = directory.File("Trash");
        // A file where the "files" directory has to be makes every move fail without touching the source.
        Directory.CreateDirectory(trashRoot);
        File.WriteAllText(Path.Combine(trashRoot, "files"), "blocker");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            new XdgTrash(trashRoot, () => FixedNow).MoveToTrashAsync(source, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
    }

    [Fact]
    public async Task Xdg_cancelled_token_throws_before_touching_anything()
    {
        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new XdgTrash(directory.File("Trash")).MoveToTrashAsync(source, cts.Token));

        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.False(Directory.Exists(directory.File("Trash")));
    }

    [Theory]
    [InlineData("data", "data")]
    [InlineData(null, "home/.local/share")]
    [InlineData("", "home/.local/share")]
    [InlineData("relative/dir", "home/.local/share")]
    public async Task Xdg_root_comes_from_xdg_data_home_and_defaults_to_local_share(string? variable, string expectedBase)
    {
        using var directory = new TestDirectory();
        var home = directory.File("home");
        var configured = variable == "data" ? directory.File("data") : variable;
        var source = directory.File("item");
        File.WriteAllText(source, "x");

        var trash = XdgTrash.FromEnvironment(name => name == "XDG_DATA_HOME" ? configured : null, home, () => FixedNow);
        await trash.MoveToTrashAsync(source, CancellationToken.None);

        var expectedRoot = Path.Combine(variable == "data" ? configured! : directory.File(expectedBase), "Trash");
        Assert.True(File.Exists(Path.Combine(expectedRoot, "files", "item")));
        Assert.True(File.Exists(Path.Combine(expectedRoot, "info", "item.trashinfo")));
    }

    [Fact]
    public async Task Mac_moves_into_the_trash_directory_and_removes_the_source()
    {
        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");
        var trash = directory.File(".Trash");

        await new MacTrash(trash).MoveToTrashAsync(source, CancellationToken.None);

        Assert.False(Directory.Exists(source));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(trash, "wt", "a.txt")));
        Assert.Equal("payload-deep", File.ReadAllText(Path.Combine(trash, "wt", "sub", "deeper", "b.txt")));
    }

    [Fact]
    public async Task Mac_name_collisions_get_suffixes_for_directories_and_files()
    {
        using var directory = new TestDirectory();
        var trash = directory.File(".Trash");
        var mover = new MacTrash(trash);
        var first = MakeTree(directory, "a" + Path.DirectorySeparatorChar + "wt", "first");
        var second = MakeTree(directory, "b" + Path.DirectorySeparatorChar + "wt", "second");
        var fileOne = directory.File("f1" + Path.DirectorySeparatorChar + "n.txt");
        var fileTwo = directory.File("f2" + Path.DirectorySeparatorChar + "n.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(fileOne)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fileTwo)!);
        File.WriteAllText(fileOne, "one");
        File.WriteAllText(fileTwo, "two");

        await mover.MoveToTrashAsync(first, CancellationToken.None);
        await mover.MoveToTrashAsync(second, CancellationToken.None);
        await mover.MoveToTrashAsync(fileOne, CancellationToken.None);
        await mover.MoveToTrashAsync(fileTwo, CancellationToken.None);

        Assert.Equal("first", File.ReadAllText(Path.Combine(trash, "wt", "a.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(trash, "wt.2", "a.txt")));
        Assert.Equal("one", File.ReadAllText(Path.Combine(trash, "n.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(trash, "n.txt.2")));
    }

    [Fact]
    public async Task Mac_missing_source_throws()
    {
        using var directory = new TestDirectory();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new MacTrash(directory.File(".Trash")).MoveToTrashAsync(directory.File("nope"), CancellationToken.None));
    }

    [Fact]
    public async Task Mac_failed_move_keeps_the_source_and_does_not_delete_it()
    {
        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");
        var blocker = directory.File("blocker");
        File.WriteAllText(blocker, "a file where the trash directory has to be");

        await Assert.ThrowsAnyAsync<Exception>(() => new MacTrash(blocker).MoveToTrashAsync(source, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
    }

    [Fact]
    public void The_default_service_matches_the_platform()
    {
        var service = TrashService.CreateDefault();

        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsRecycleBinTrash>(service);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacTrash>(service);
        }
        else
        {
            Assert.IsType<XdgTrash>(service);
        }
    }

    [Fact]
    public async Task Windows_recycle_bin_missing_source_throws()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();

        await Assert.ThrowsAnyAsync<IOException>(() =>
            new WindowsRecycleBinTrash().MoveToTrashAsync(directory.File("nope"), CancellationToken.None));
    }

    [Theory]
    [InlineData(@"\\server\share\wt", DriveType.Fixed)]
    [InlineData(@"\\?\UNC\server\share\wt", DriveType.Fixed)]
    [InlineData(@"D:\wt", DriveType.Network)]
    [InlineData(@"E:\wt", DriveType.Removable)]
    [InlineData(@"F:\wt", DriveType.CDRom)]
    [InlineData(@"G:\wt", DriveType.Ram)]
    [InlineData(@"H:\wt", DriveType.Unknown)]
    [InlineData(@"I:\wt", DriveType.NoRootDirectory)]
    [InlineData(@"\\.\UNC\server\share\wt", DriveType.Fixed)]
    [InlineData(@"\\?\Volume{12345678-1234-1234-1234-123456789abc}\wt", DriveType.Fixed)]
    public void Windows_recycle_bin_refuses_a_location_without_a_bin(string path, DriveType type)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var refusal = WindowsRecycleBinTrash.RecycleBinRefusal(path, _ => type);

        Assert.NotNull(refusal);
        Assert.Contains("nothing was deleted", refusal, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\work\wt")]
    [InlineData(@"\\?\C:\work\wt")]
    [InlineData(@"\\.\C:\work\wt")]
    public void Windows_recycle_bin_accepts_a_fixed_local_drive_and_asks_with_its_root(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var asked = new List<string>();

        Assert.Null(WindowsRecycleBinTrash.RecycleBinRefusal(path, root =>
        {
            asked.Add(root);
            return DriveType.Fixed;
        }));
        Assert.Equal([@"C:\"], asked);
    }

    [Fact]
    public void Windows_recycle_bin_refuses_a_drive_whose_type_cannot_be_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var refusal = WindowsRecycleBinTrash.RecycleBinRefusal(@"C:\wt", _ => throw new IOException("device not ready"));

        Assert.Contains("device not ready", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Windows_recycle_bin_on_a_drive_without_a_bin_fails_and_leaves_the_directory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            new WindowsRecycleBinTrash(_ => DriveType.Removable).MoveToTrashAsync(source, CancellationToken.None));

        Assert.Contains("Removable", failure.Message, StringComparison.Ordinal);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(source, "a.txt")));
        Assert.Equal("payload-deep", File.ReadAllText(Path.Combine(source, "sub", "deeper", "b.txt")));
    }

    [Fact]
    public void Windows_recycle_bin_judges_the_volume_a_junction_on_the_path_leads_to()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var local = directory.File("local");
        Directory.CreateDirectory(Path.Combine(local, "wt"));
        var localRoot = Path.GetPathRoot(local)!;

        // A junction into the local folder: same fixed drive, accepted.
        var near = directory.File("near");
        Junction(near, local);
        Assert.Null(WindowsRecycleBinTrash.RecycleBinRefusal(Path.Combine(near, "wt"), static _ => DriveType.Fixed));

        // A junction to another drive (the test assembly's folder, read only; junctions need a real local volume), which
        // the fake reports removable: refused, naming the target. Skipped when the temp folder is on that drive too.
        var elsewhere = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (string.Equals(Path.GetPathRoot(elsewhere), localRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var far = directory.File("far");
        Junction(far, elsewhere);
        DriveType TypeOf(string root) =>
            string.Equals(root, localRoot, StringComparison.OrdinalIgnoreCase) ? DriveType.Fixed : DriveType.Removable;

        var refusal = WindowsRecycleBinTrash.RecycleBinRefusal(Path.Combine(far, "wt"), TypeOf);

        Assert.NotNull(refusal);
        Assert.Contains(elsewhere, refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Removable", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Subst_listing_lines_are_parsed_into_drive_mappings()
    {
        var mappings = WindowsRecycleBinTrash.ParseSubstListing(
            "W:\\: => C:\\Users\\me\\AppData\\Local\\Temp\\probe\r\n"
            + "x:\\: => D:\\work\\项目 二\r\n"
            + "\r\n"
            + "garbage line\r\n"
            + "Y:\\: => relative\\path\r\n");

        Assert.Equal(2, mappings.Count);
        Assert.Equal(@"C:\Users\me\AppData\Local\Temp\probe", mappings['W']);
        Assert.Equal(@"D:\work\项目 二", mappings['X']);
        Assert.Empty(WindowsRecycleBinTrash.ParseSubstListing(string.Empty));
    }

    [Fact]
    public void A_subst_path_is_translated_to_the_folder_it_maps_and_a_misread_mapping_is_ignored()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var real = directory.File("real");
        Directory.CreateDirectory(Path.Combine(real, "wt"));
        var free = FreeDriveLetter();

        Assert.Equal(
            Path.Combine(real, "wt"),
            WindowsRecycleBinTrash.ResolveSubst(free + @":\wt", new Dictionary<char, string> { [free] = real }));
        Assert.Equal(real, WindowsRecycleBinTrash.ResolveSubst(free + @":\", new Dictionary<char, string> { [free] = real }));
        Assert.Equal(@"C:\x", WindowsRecycleBinTrash.ResolveSubst(@"C:\x", new Dictionary<char, string>()));

        // A mapping that does not lead to the existing item (a misread target) is not used.
        var existing = Path.Combine(real, "wt");
        var root = char.ToUpperInvariant(existing[0]);
        Assert.Equal(
            existing,
            WindowsRecycleBinTrash.ResolveSubst(existing, new Dictionary<char, string> { [root] = directory.File("nowhere") }));
    }

    [Fact]
    public async Task Windows_recycle_bin_judges_and_recycles_the_real_folder_behind_a_subst_drive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");
        var free = FreeDriveLetter();
        var realRoot = Path.GetPathRoot(source)!;

        // The real drive is faked removable, so nothing is recycled; the refusal names the real path, not the subst one.
        var failure = await Assert.ThrowsAsync<IOException>(() =>
            new WindowsRecycleBinTrash(
                    root => string.Equals(root, realRoot, StringComparison.OrdinalIgnoreCase) ? DriveType.Removable : DriveType.Fixed,
                    () => new Dictionary<char, string> { [free] = directory.Path })
                .MoveToTrashAsync(free + @":\wt", CancellationToken.None));

        Assert.Contains(source, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(source, "a.txt")));
    }

    [Fact]
    public void The_subst_listing_of_this_machine_can_be_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var (drive, target) in WindowsRecycleBinTrash.ReadSubstMappings())
        {
            Assert.True(char.IsAsciiLetterUpper(drive));
            Assert.True(Path.IsPathFullyQualified(target));
        }
    }

    private static char FreeDriveLetter()
    {
        var used = DriveInfo.GetDrives().Select(static d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        return "QRSTUVWXYZ".First(letter => !used.Contains(letter));
    }

    private static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit();
        Assert.True(mklink.ExitCode == 0, "mklink /J failed: " + mklink.StandardError.ReadToEnd());
    }

    [Fact]
    public void Windows_recycle_bin_reads_the_real_drive_of_the_temp_folder_as_one_with_a_bin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory();

        Assert.Null(WindowsRecycleBinTrash.RecycleBinRefusal(directory.File("wt"), static root => new DriveInfo(root).DriveType));
    }

    /// <summary>Touches the real Recycle Bin, so CI does not run it: set FORGE_IO_TEST_REAL_RECYCLE_BIN=1 locally.</summary>
    [Fact]
    public async Task Windows_recycle_bin_really_removes_the_directory_when_enabled()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("FORGE_IO_TEST_REAL_RECYCLE_BIN") != "1")
        {
            return;
        }

        using var directory = new TestDirectory();
        var source = MakeTree(directory, "wt");

        await new WindowsRecycleBinTrash().MoveToTrashAsync(source, CancellationToken.None);

        Assert.False(Directory.Exists(source));
    }
}
