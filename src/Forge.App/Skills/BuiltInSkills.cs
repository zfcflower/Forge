using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Forge.App.Services;

namespace Forge.App.Skills;

/// <summary>One skill the product ships: its folder name, the front matter's description and the whole SKILL.md text.</summary>
internal sealed record BuiltInSkill(string Name, string Description, string Text);

/// <summary>
/// The skills the product itself ships. They are embedded in this assembly as
/// <c>Skills/BuiltIn/&lt;name&gt;/SKILL.md</c>, read once and materialized under
/// <c>&lt;user data directory&gt;/builtin-skills/&lt;content version&gt;/&lt;name&gt;/SKILL.md</c>, so the ordinary skill
/// discovery (<see cref="SkillCatalog"/>) can list them from disk like any other folder and the settings page can show where
/// they came from.
/// <para>The list is ordered ordinally by name and the version is derived from the names and texts, so the same build
/// materializes into the same directory name and a changed skill lands in a new one; the stale versions beside it are
/// removed afterwards. Only the file system is touched here: no log sink, because this runs from the background start-up
/// path and a failure to write a read-only cache must never be more than "no product skills this run".</para>
/// </summary>
internal static class BuiltInSkills
{
    /// <summary>Suffix of every embedded skill resource name; the segment before it is the skill's folder name.</summary>
    private const string ResourceSuffix = ".SKILL.md";

    /// <summary>The file a materialized skill folder holds.</summary>
    private const string FileName = "SKILL.md";

    /// <summary>The folder under the user data directory holding one subdirectory per content version.</summary>
    private const string RootFolder = "builtin-skills";

    /// <summary>UTF-8 without a byte order mark, which is what the skill files are and what the materialized copies are.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The separator a hashed field is followed by, so a name and a text that run together still hash apart.</summary>
    private static readonly byte[] FieldSeparator = [(byte)'\n'];

    private static readonly Lazy<IReadOnlyList<BuiltInSkill>> Skills = new(ReadSkills);

    private static readonly Lazy<string> Version = new(() => Hash(Skills.Value));

    /// <summary>Every embedded SKILL.md, ordered ordinally by name. Empty when the assembly embeds none.</summary>
    public static IReadOnlyList<BuiltInSkill> All => Skills.Value;

    /// <summary>16 lowercase hex chars of SHA256 over every skill's name and text in order; names the materialized directory.</summary>
    public static string ContentVersion => Version.Value;

    /// <summary>
    /// Materializes the skills under <c>&lt;userDataDirectory&gt;/builtin-skills/&lt;ContentVersion&gt;/&lt;name&gt;/SKILL.md</c>
    /// and returns that version directory; null when it could not be written or when the assembly embeds no skill. A version
    /// directory that already holds every file is returned without writing anything, so a second call is free. Sibling
    /// versions left by an older build are deleted afterwards, best effort. Never throws; safe to call from a pool thread.
    /// </summary>
    public static string? Materialize(string? userDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(userDataDirectory))
        {
            return null;
        }

        try
        {
            var skills = All;
            if (skills.Count == 0)
            {
                return null;
            }

            var root = Path.Combine(userDataDirectory, RootFolder);
            var directory = Path.Combine(root, ContentVersion);
            if (!IsComplete(directory, skills))
            {
                foreach (var skill in skills)
                {
                    var folder = Path.Combine(directory, skill.Name);
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, FileName), skill.Text, Utf8NoBom);
                }
            }

            PruneVersions(root, ContentVersion);
            return directory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The embedded skills: every resource whose name ends in <c>.SKILL.md</c>. The folder segment before the suffix is the
    /// fallback name, the front matter's <c>name</c> and <c>description</c> go through
    /// <see cref="SkillCatalog.ParseFrontMatter"/> so an embedded skill reads exactly like one discovered on disk. Text is
    /// folded to LF while reading, so neither the version nor a materialized file depends on the checkout's line endings.
    /// </summary>
    private static IReadOnlyList<BuiltInSkill> ReadSkills()
    {
        var assembly = typeof(BuiltInSkills).Assembly;
        var skills = new List<BuiltInSkill>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var withoutSuffix = resource[..^ResourceSuffix.Length];
            var separator = withoutSuffix.LastIndexOf('.');
            var folder = separator < 0 ? withoutSuffix : withoutSuffix[(separator + 1)..];
            var text = ReadResource(assembly, resource);
            if (folder.Length == 0 || text is null)
            {
                continue;
            }

            var (name, description) = SkillCatalog.ParseFrontMatter(text);
            skills.Add(new BuiltInSkill(
                string.IsNullOrWhiteSpace(name) ? folder : name,
                description ?? string.Empty,
                text));
        }

        // Ordered by name so the version, the prompt index and the materialized files are stable across runs. The text only
        // settles two resources that resolve to the same name, which would otherwise order by the resource enumeration.
        skills.Sort(static (left, right) =>
        {
            var byName = string.CompareOrdinal(left.Name, right.Name);
            return byName != 0 ? byName : string.CompareOrdinal(left.Text, right.Text);
        });
        return skills;
    }

    /// <summary>The resource's text as UTF-8; null when the assembly advertises a name it cannot open.</summary>
    private static string? ReadResource(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().ReplaceLineEndings("\n");
    }

    /// <summary>Whether every skill already has its file in <paramref name="directory"/>, in which case writing it again adds nothing.</summary>
    private static bool IsComplete(string directory, IReadOnlyList<BuiltInSkill> skills)
    {
        foreach (var skill in skills)
        {
            if (!File.Exists(Path.Combine(directory, skill.Name, FileName)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Deletes the versions beside the current one, so a build only ever leaves its own copy behind; failures are ignored.</summary>
    private static void PruneVersions(string root, string current)
    {
        string[] versions;
        try
        {
            versions = Directory.GetDirectories(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var version in versions)
        {
            if (string.Equals(Path.GetFileName(version), current, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                Directory.Delete(version, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A version another running instance still has open is left where it is; the next start tries again. It can be
                // half deleted by the time Windows refuses a file, so a stale version is not guaranteed to stay readable - the
                // index of a running instance then reports that skill as unreadable rather than crashing on it.
            }
        }
    }

    /// <summary>The first 8 bytes of SHA256 over every skill's name and text in order, as 16 lowercase hex chars.</summary>
    private static string Hash(IReadOnlyList<BuiltInSkill> skills)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var skill in skills)
        {
            Append(hash, skill.Name);
            Append(hash, skill.Text);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 8));
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(FieldSeparator);
    }
}