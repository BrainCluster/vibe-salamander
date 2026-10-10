using VibeSalamander.Core.Tools;

namespace VibeSalamander.Core.Tests;

public class UserMenuTests
{
    private static readonly UserMenuContext Context = new()
    {
        Path = @"C:\Work",
        OtherPath = @"D:\Backup",
        FullName = @"C:\Work\report.final.docx",
        Selected = new[] { @"C:\Work\a.txt", @"C:\Work\b c.txt" },
    };

    [Theory]
    [InlineData("\"$(FullName)\"", "\"C:\\Work\\report.final.docx\"")]
    [InlineData("$(Name)|$(NamePart)|$(ExtPart)", "report.final.docx|report.final|docx")]
    [InlineData("$(Path) -> $(OtherPath)", @"C:\Work -> D:\Backup")]
    [InlineData("$(SelectedNames)", "\"a.txt\" \"b c.txt\"")]
    [InlineData("$(fullname)", @"C:\Work\report.final.docx")]
    [InlineData("$(Unknown) stays", "$(Unknown) stays")]
    [InlineData("no variables", "no variables")]
    public void Expands(string text, string expected) => Assert.Equal(expected, UserMenu.Expand(text, Context));

    [Fact]
    public void DefaultsHaveCommands() =>
        Assert.All(UserMenu.CreateDefaults(), i => Assert.True(i.IsSeparator || i.Command.Length > 0));
}

public class DirectoryComparerTests
{
    [Fact]
    public async Task MarksMissingNewerAndDifferentSizes()
    {
        string root = Path.Combine(Path.GetTempPath(), "vs-cmp-" + Guid.NewGuid().ToString("N"));
        string left = Path.Combine(root, "L"), right = Path.Combine(root, "R");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        try
        {
            var t = new DateTime(2024, 1, 1, 12, 0, 0);
            var leftItems = new List<DirectoryComparer.Item>
            {
                new("same.txt", false, 10, t),
                new("newer-left.txt", false, 10, t.AddHours(1)),
                new("only-left.txt", false, 1, t),
                new("size.txt", false, 10, t),
            };
            var rightItems = new List<DirectoryComparer.Item>
            {
                new("same.txt", false, 10, t.AddSeconds(1)), // within the 2 s tolerance
                new("newer-left.txt", false, 10, t),
                new("only-right.txt", false, 1, t),
                new("size.txt", false, 11, t),
            };
            CompareResult result = await DirectoryComparer.CompareAsync(left, leftItems, right, rightItems,
                new CompareOptions { ByDate = true, BySize = true });

            Assert.Equal(new[] { "newer-left.txt", "only-left.txt", "size.txt" }, result.Left.OrderBy(n => n));
            Assert.Equal(new[] { "only-right.txt", "size.txt" }, result.Right.OrderBy(n => n));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ContentEqual()
    {
        string a = Path.GetTempFileName(), b = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(a, "hello");
            await File.WriteAllTextAsync(b, "hello");
            Assert.True(DirectoryComparer.ContentEqual(a, b));
            await File.WriteAllTextAsync(b, "hellO");
            Assert.False(DirectoryComparer.ContentEqual(a, b));
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }
}

public class KeyMapOverrideTests
{
    [Fact]
    public void OverridesRoundTrip()
    {
        var map = Commands.KeyMap.CreateSalamanderDefaults();
        map.Unbind(new Commands.KeyChord(Commands.VK.F7, Commands.KeyModifiers.None));          // remove "Create directory" from F7
        map.Bind(new Commands.KeyChord(0x4E, Commands.KeyModifiers.Ctrl | Commands.KeyModifiers.Shift), Commands.CommandId.CreateDirectory);
        map.Bind(new Commands.KeyChord(Commands.VK.F5, Commands.KeyModifiers.None), Commands.CommandId.Move); // reassign F5

        var overrides = map.GetOverrides();
        Assert.Equal(3, overrides.Count);

        var restored = Commands.KeyMap.Create(overrides);
        Assert.Equal(Commands.CommandId.None, restored.Resolve(Commands.VK.F7, Commands.KeyModifiers.None));
        Assert.Equal(Commands.CommandId.CreateDirectory, restored.Resolve(0x4E, Commands.KeyModifiers.Ctrl | Commands.KeyModifiers.Shift));
        Assert.Equal(Commands.CommandId.Move, restored.Resolve(Commands.VK.F5, Commands.KeyModifiers.None));
        Assert.Empty(Commands.KeyMap.CreateSalamanderDefaults().GetOverrides());
    }

    [Fact]
    public void EveryCommandHasAName() =>
        Assert.All(Enum.GetValues<Commands.CommandId>(), c => Assert.False(string.IsNullOrWhiteSpace(Commands.CommandCatalog.DisplayName(c))));
}
