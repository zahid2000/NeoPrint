using System.Text.RegularExpressions;

using NeoPrint.Application.Localization;
using NeoPrint.Domain.Common;

namespace NeoPrint.Application.Tests.Localization;

/// <summary>
/// The backend's text for people, in az, en and ru (plan section 2.1;
/// docs/TASKS.md T-043).
/// </summary>
public sealed partial class MessagesTests
{
    private static readonly string[] OtherLanguages = ["en", "ru"];

    /// <summary>Audit actions passed through a helper, where the statement does not say "audit".</summary>
    private static readonly HashSet<string> OtherCodes = new(["kiosks.limits"], StringComparer.Ordinal);

    [Fact]
    public void Every_language_has_every_key()
    {
        var az = Messages.Catalog("az").Keys.ToHashSet(StringComparer.Ordinal);
        az.Should().NotBeEmpty();

        foreach (var language in OtherLanguages)
        {
            var keys = Messages.Catalog(language).Keys.ToHashSet(StringComparer.Ordinal);
            keys.Except(az).Should().BeEmpty($"{language} has no key that az lacks");
            az.Except(keys).Should().BeEmpty($"{language} translates every key");
        }
    }

    [Fact]
    public void Every_translation_uses_the_same_placeholders()
    {
        foreach (var (key, text) in Messages.Catalog("az"))
        {
            var expected = Placeholders(text);

            foreach (var language in OtherLanguages)
            {
                Placeholders(Messages.Catalog(language)[key])
                    .Should().BeEquivalentTo(expected, $"{language} {key} fills the same values");
            }
        }
    }

    [Fact]
    public void No_text_is_left_empty_or_untranslated()
    {
        foreach (var language in Messages.Languages)
        {
            foreach (var (key, text) in Messages.Catalog(language))
            {
                text.Should().NotBeNullOrWhiteSpace($"{language} {key}");
            }
        }

        // A Russian text in Latin letters, or an English one with Azerbaijani
        // letters, is a copy that was never translated.
        foreach (var (key, text) in Messages.Catalog("ru"))
        {
            if (Placeholders(text).Count == 0 || text.Length > 20)
            {
                CyrillicLetter().IsMatch(text).Should().BeTrue($"ru {key} is Russian: {text}");
            }
        }

        foreach (var (key, text) in Messages.Catalog("en"))
        {
            AzerbaijaniLetter().IsMatch(text).Should().BeFalse($"en {key} is English: {text}");
        }

        // The same rule as the three apps' translation checks
        // (docs/DESIGN_SYSTEM.md, "Tərcümə yoxlaması").
        foreach (var (key, text) in Messages.Catalog("az"))
        {
            EnglishTechnicalWord().IsMatch(text).Should().BeFalse($"az {key} has no English technical word: {text}");
        }
    }

    [Theory]
    [InlineData("az", "Şifrə ən azı 8 simvol olmalıdır.")]
    [InlineData("en", "The password must be at least 8 characters long.")]
    [InlineData("ru", "Пароль должен содержать не менее 8 символов.")]
    public void A_message_is_written_in_the_language_asked_for(string language, string expected)
        => Messages.Write(UserMessage.Of("auth.passwordTooShort", ("min", 8)), language).Should().Be(expected);

    [Fact]
    public void An_unknown_language_falls_back_to_azerbaijani()
        => Messages.Write(new UserMessage("auth.passwordRequired"), "de").Should().Be("Şifrə tələb olunur.");

    [Fact]
    public void The_current_language_is_used_when_none_is_named()
    {
        Messages.Current = "en";

        try
        {
            Messages.Write(new UserMessage("auth.passwordRequired")).Should().Be("The password is required.");
        }
        finally
        {
            Messages.Current = "az";
        }
    }

    [Fact]
    public void A_stored_message_reads_in_the_reader_s_language()
    {
        var stored = UserMessage.Of("conversion.tooManyPages", ("count", 300), ("max", 200)).Encode();

        Messages.WriteStored(stored, "az").Should().Be("Sənəd 300 səhifədir; icazə verilən maksimum 200 səhifədir.");
        Messages.WriteStored(stored, "en").Should().Be("The document has 300 pages; the most allowed is 200.");
    }

    [Fact]
    public void Text_stored_before_the_catalogs_is_shown_as_it_is()
    {
        // Failure reasons written by step 2 are sentences, not keys.
        const string legacy = "Fayl zədələnib və ya düzgün PDF deyil.";

        Messages.WriteStored(legacy, "en").Should().Be(legacy);
        UserMessage.Decode(legacy).IsLiteral.Should().BeTrue();
    }

    [Fact]
    public void A_list_of_stored_messages_reads_as_one_line()
    {
        var stored = UserMessage.EncodeAll(
        [
            UserMessage.Of("files.note.pagesKept", ("kept", 2), ("total", 3)),
            new UserMessage("files.note.reordered"),
        ]);

        Messages.WriteStored(stored, "az").Should().Be("3 səhifədən 2-i saxlanıldı, sıra dəyişdi");
        Messages.WriteStored(stored, "en").Should().Be("2 of 3 pages kept, order changed");
    }

    [Fact]
    public void A_nested_message_argument_is_written_out_too()
    {
        var problem = UserMessage.EncodeAll(
        [
            UserMessage.Of("storage.test.failed", ("count", 1)),
            UserMessage.Of("storage.test.bucketFailed", ("bucket", "neoprint.files"), ("problemMessage", "storage.test.noBucket")),
        ]);

        Messages.Write(UserMessage.Of("storage.testFailed", ("problemMessage", problem)), "en")
            .Should().Be("The connection test failed: There is a problem in 1 buckets, neoprint.files — The bucket does not exist.");
    }

    [Theory]
    [InlineData("az", "12,5")]
    [InlineData("ru", "12,5")]
    [InlineData("en", "12.5")]
    public void A_decimal_value_is_written_in_the_reader_s_style(string language, string expected)
        => Messages.Write(UserMessage.Of("files.tooLarge", ("size", 12.5m), ("max", 50)), language)
            .Should().StartWith(language == "en" ? $"The file is {expected} MB" : language == "az" ? $"Fayl {expected} MB" : $"Размер файла {expected} МБ");

    [Theory]
    [InlineData("files.notReady", true)]
    [InlineData("auth.passwordTooShort", true)]
    [InlineData("Fayl tapılmadı.", false)]
    [InlineData("files", false)]
    [InlineData("Files.notReady", false)]
    [InlineData("files..notReady", false)]
    [InlineData("files.not ready", false)]
    public void A_key_looks_like_a_key(string value, bool expected)
        => UserMessage.LooksLikeKey(value).Should().Be(expected);

    [Fact]
    public void Encoding_round_trips_values_with_separators_in_them()
    {
        var message = UserMessage.Of("roles.nameTaken", ("name", "a&b=c?d"));

        UserMessage.Decode(message.Encode()).Should().BeEquivalentTo(message);
    }

    [Fact]
    public void Every_key_the_code_uses_has_a_text()
    {
        var catalog = Messages.Catalog("az");
        var prefixes = catalog.Keys.Select(key => key[..key.IndexOf('.', StringComparison.Ordinal)]).ToHashSet(StringComparer.Ordinal);

        // Permission codes and audit actions have the same dotted shape
        // (kiosks.view, roles.create) and are not messages.
        var permissions = typeof(NeoPrint.Domain.Admin.Permissions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match match in KeyLiteral().Matches(text))
            {
                var key = match.Groups[1].Value;

                if (!prefixes.Contains(key[..key.IndexOf('.', StringComparison.Ordinal)])
                    || catalog.ContainsKey(key)
                    || permissions.Contains(key)
                    || OtherCodes.Contains(key)
                    || IsAuditAction(text, match.Index))
                {
                    continue;
                }

                missing.Add($"{Path.GetFileName(file)}: {key}");
            }

            foreach (Match match in NotFoundLiteral().Matches(text))
            {
                var key = $"notFound.{match.Groups[1].Value}";

                if (!catalog.ContainsKey(key))
                {
                    missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        missing.Should().BeEmpty("every message key in the code has a text in the catalogs");
    }

    [Fact]
    public void No_sentence_for_people_is_written_in_the_code()
    {
        // Seed data and the default file name are data, not messages: what
        // the admin panel shows for them comes from its own translations.
        string[] allowed =
        [
            "AdminSettings.cs", "FeatureFlag.cs", "Permissions.cs", "DatabaseSeeder.cs",
            "DevelopmentSeeder.cs", "StorageProviderSeeder.cs",
        ];

        var offenders = new List<string>();

        foreach (var file in SourceFiles().Where(file => !allowed.Contains(Path.GetFileName(file))))
        {
            foreach (var (line, number) in File.ReadLines(file).Select((line, index) => (line, index + 1)))
            {
                var code = line.TrimStart();

                if (code.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match literal in StringLiteral().Matches(code))
                {
                    if (AzerbaijaniLetter().IsMatch(literal.Value) && !literal.Value.Contains("sənəd.{", StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{number}: {literal.Value}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty("text for people belongs in Localization/messages.*.json");
    }

    /// <summary>Whether the literal is an argument of the statement that writes an audit entry.</summary>
    private static bool IsAuditAction(string text, int index)
    {
        var start = text.LastIndexOfAny([';', '{', '}'], index) + 1;

        return text.AsSpan(start, index - start).Contains("udit", StringComparison.Ordinal);
    }

    private static HashSet<string> Placeholders(string text)
        => Placeholder().Matches(text).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root has to be findable from the test output directory");

        return new[] { "backend/src", "backend/shared" }
            .Select(relative => Path.Combine(directory!.FullName, relative))
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\{([A-Za-z]+)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"""([a-z]+(?:\.[a-zA-Z0-9]+)+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex(@"NotFoundException\(""([A-Za-z]+)""\)")]
    private static partial Regex NotFoundLiteral();

    [GeneratedRegex(@"\$?""(?:[^""\\]|\\.)*""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex("[əğıöüşçƏĞİÖÜŞÇ]")]
    private static partial Regex AzerbaijaniLetter();

    [GeneratedRegex(@"\b(token|tokens|flag|flags|feature|features|enrollment|dashboard|default|override|overrides|rollout|provisioning|endpoint|mirror|primary|secondary|hash|job|jobs|queue|upload|download|error|network)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishTechnicalWord();

    [GeneratedRegex("[А-Яа-яЁё]")]
    private static partial Regex CyrillicLetter();
}
