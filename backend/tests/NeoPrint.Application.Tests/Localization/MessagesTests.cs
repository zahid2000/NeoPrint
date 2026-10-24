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
    public void Every_key_reads_as_a_key()
    {
        // A key whose segment starts with a capital ("page.refund.status.Succeeded")
        // is taken for a finished sentence and shown to people as it is.
        Messages.Catalog(Messages.DefaultLanguage).Keys
            .Where(key => !NeoPrint.Domain.Common.UserMessage.LooksLikeKey(key))
            .Should().BeEmpty("every segment of a key starts with a small letter");
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
    public void A_code_argument_is_written_in_the_reader_s_words()
    {
        // Stored once with the enum's name; read in each reader's language (T-129).
        var stored = UserMessage.Of("incidents.fiscalRejected", ("type", "TopUp"), ("amount", 1)).Encode();

        Messages.WriteStored(stored, "az").Should().StartWith("E-kassa 1 ₼ məbləğində balans artırma fiskal çekini rədd etdi.");
        Messages.WriteStored(stored, "en").Should().StartWith("The e-kassa refused a top-up fiscal receipt of 1 ₼.");
        Messages.WriteStored(stored, "ru").Should().Contain("фискальный чек пополнения баланса").And.NotContain("TopUp");

        // A code the catalog does not know is shown as it is rather than lost.
        Messages.Write(UserMessage.Of("incidents.fiscalRejected", ("type", "Other"), ("amount", 1)), "en")
            .Should().StartWith("The e-kassa refused a Other fiscal receipt");
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

    [Fact]
    public void A_fiscal_incident_s_cause_reads_in_the_reader_s_language()
    {
        // Stored as the cause's key (T-140), not as Azerbaijani text.
        var stored = UserMessage.Of(
                "incidents.fiscalPending", ("type", "Sale"), ("amount", 2.5m), ("minutes", 15), ("causeMessage", "fiscal.provider.unavailable"))
            .Encode();

        stored.Should().NotContain("E-kassa");
        Messages.WriteStored(stored, "az").Should().Contain("Səbəb: E-kassa cavab vermədi; çek avtomatik yenidən göndəriləcək. Ödəniş");
        Messages.WriteStored(stored, "en").Should().Contain("Reason: The e-kassa did not answer; the receipt is sent again automatically. Payments");
        Messages.WriteStored(stored, "ru").Should().Contain("Причина: Э-касса не ответила; чек будет отправлен повторно автоматически. Платежи");

        var noCause = UserMessage.Of(
            "incidents.fiscalPending", ("type", "Sale"), ("amount", 1), ("minutes", 15), ("causeMessage", "incidents.fiscalPendingNoCause"));
        Messages.Write(noCause, "en").Should().Contain("for 15 min. Reason: No reason was recorded. Payments go on");
    }

    [Fact]
    public void A_fiscal_incident_stored_with_azerbaijani_cause_text_still_reads_in_the_reader_s_language()
    {
        // What step 4 stored: the cause already written out in Azerbaijani.
        static string Legacy(string cause)
            => UserMessage.Of("incidents.fiscalPending", ("type", "Sale"), ("amount", 2.5m), ("minutes", 15), ("cause", cause)).Encode();

        var legacy = Legacy(Messages.Write(new UserMessage("fiscal.provider.unavailable"), "az"));

        Messages.WriteStored(legacy, "en").Should().Contain("Reason: The e-kassa did not answer; the receipt is sent again automatically. Payments");
        Messages.WriteStored(legacy, "ru").Should().Contain("Причина: Э-касса не ответила;");
        Messages.WriteStored(legacy, "az").Should().Contain("Səbəb: E-kassa cavab vermədi;");

        // No cause at all, and a sentence no catalog has any more: shown as they were.
        Messages.WriteStored(Legacy("-"), "en").Should().Contain("Reason: - Payments");
        Messages.WriteStored(Legacy("Köhnə mətn"), "en").Should().Contain("Reason: Köhnə mətn Payments").And.NotContain("{causeMessage}");
    }

    [Theory]
    [InlineData("az", "12,5")]
    [InlineData("ru", "12,5")]
    [InlineData("en", "12.5")]
    public void A_decimal_value_is_written_in_the_reader_s_style(string language, string expected)
        => Messages.Write(UserMessage.Of("files.tooLarge", ("size", 12.5m), ("max", 50)), language)
            .Should().StartWith(language == "en" ? $"The file is {expected} MB" : language == "az" ? $"Fayl {expected} MB" : $"Размер файла {expected} МБ");

    [Theory]
    [InlineData("az")]
    [InlineData("ru")]
    [InlineData("en")]
    public void A_version_is_written_as_it_is_not_as_a_number(string language)
        => Messages.Write(UserMessage.Of("incidents.kiosk.updateFailed", ("kiosk", "KSK-0001"),
                ("componentMessage", "releases.component.app"), ("version", "0.8.12"), ("reasonMessage", "updates.reasonUnknown")), language)
            .Should().Contain("0.8.12");

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

            // A key with a prefix the catalog has never seen (a new module)
            // would pass the check above; inside a message call it is a key
            // whatever its prefix.
            foreach (Match call in MessageCall().Matches(text))
            {
                var arguments = TopLevelArguments(ArgumentsOf(text, call.Index + call.Length - 1));

                // ValidationError(field, key) and Invalid(field, key): the first
                // argument is a field name ("newKiosk.name"), not a message.
                var skip = call.Value.StartsWith("ValidationError", StringComparison.Ordinal)
                           || call.Value.StartsWith("Invalid", StringComparison.Ordinal)
                    ? 1
                    : 0;

                foreach (Match literal in arguments.Skip(skip).SelectMany(argument => StringLiteral().Matches(argument)))
                {
                    var value = literal.Value.TrimStart('$').Trim('"');

                    if (UserMessage.LooksLikeKey(value)
                        && !catalog.ContainsKey(value)
                        && !permissions.Contains(value)
                        && !OtherCodes.Contains(value))
                    {
                        missing.Add($"{Path.GetFileName(file)}: {value}");
                    }
                }
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

        missing.Should().BeEmpty($"every message key in the code has a text in the catalogs: {string.Join("; ", missing.Distinct())}");
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

    [Fact]
    public void A_message_is_passed_as_a_key_never_as_a_sentence()
    {
        // A sentence without an Azerbaijani letter ("Bu hesab deaktiv edilib.",
        // or an English one) slips past the letter check above; this one looks
        // at what the message-carrying calls are given instead. Configuration
        // errors at start-up are for the operator's log, not for a person.
        string[] allowed = ["CorsOrigins.cs", "MoneySerializer.cs"];
        var offenders = new List<string>();

        foreach (var file in SourceFiles().Where(file => !allowed.Contains(Path.GetFileName(file))))
        {
            var text = File.ReadAllText(file);

            foreach (Match call in MessageCall().Matches(text))
            {
                var arguments = ArgumentsOf(text, call.Index + call.Length - 1);

                foreach (Match literal in StringLiteral().Matches(arguments))
                {
                    var value = literal.Value.TrimStart('$').Trim('"');

                    if (value.Contains(' ', StringComparison.Ordinal)
                        && value.Any(char.IsLetter)
                        && !DateFormat().IsMatch(value))
                    {
                        var line = text[..call.Index].Count(character => character == '\n') + 1;
                        offenders.Add($"{Path.GetFileName(file)}:{line}: {literal.Value}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty("a person's message goes through the catalogs as a key");
    }

    /// <summary>The text between a call's opening parenthesis and its matching close.</summary>
    private static string ArgumentsOf(string text, int open)
    {
        var depth = 0;

        for (var index = open; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '"':
                    index++;
                    while (index < text.Length && text[index] != '"')
                    {
                        index += text[index] == '\\' ? 2 : 1;
                    }

                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return text[open..(index + 1)];
                    }

                    break;
            }
        }

        return text[open..];
    }

    /// <summary>A call's arguments, split at the commas between them: "(a, f(b, c), d)" → a | f(b, c) | d.</summary>
    private static List<string> TopLevelArguments(string call)
    {
        var arguments = new List<string>();
        var depth = 0;
        var start = 1;

        for (var index = 0; index < call.Length; index++)
        {
            switch (call[index])
            {
                case '"':
                    index++;
                    while (index < call.Length && call[index] != '"')
                    {
                        index += call[index] == '\\' ? 2 : 1;
                    }

                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    if (depth == 0)
                    {
                        arguments.Add(call[start..index]);
                    }

                    break;
                case ',' when depth == 1:
                    arguments.Add(call[start..index]);
                    start = index + 1;
                    break;
            }
        }

        return arguments;
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

    [GeneratedRegex(@"\b(?:DomainException|NotFoundException|ValidationError|AuthenticationException|FileEditException|PageRenderException|Unrecognised|Fail|Because|UserMessage|Failed|BucketTestResult|PricingException|Invalid|UserMessage.Of)\s*\(")]
    private static partial Regex MessageCall();

    [GeneratedRegex(@"^[dMyHhmsf.:/\- ]+$")]
    private static partial Regex DateFormat();

    [GeneratedRegex("[əğıöüşçƏĞİÖÜŞÇ]")]
    private static partial Regex AzerbaijaniLetter();

    [GeneratedRegex(@"\b(token|tokens|flag|flags|feature|features|enrollment|dashboard|default|override|overrides|rollout|provisioning|endpoint|mirror|primary|secondary|hash|job|jobs|queue|upload|download|error|network)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnglishTechnicalWord();

    [GeneratedRegex("[А-Яа-яЁё]")]
    private static partial Regex CyrillicLetter();
}
