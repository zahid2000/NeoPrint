using System.Text.RegularExpressions;

using MongoDB.Driver;

using NeoPrint.Infrastructure.Persistence;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Rules about how persistence is allowed to be written, checked against the
/// source.
/// </summary>
/// <remarks>
/// A transaction only covers the operations the session was passed to, so a
/// repository that reaches the driver collection directly writes outside it and
/// its write survives an abort. <see cref="MongoSet{TDocument}"/> makes that
/// impossible to do by accident; these tests are what keeps it impossible on
/// purpose, for the next person who needs a collection and finds a way to one
/// (<c>docs/adr/0004-ambient-mongo-session.md</c>).
///
/// No Docker: they read files.
/// </remarks>
public sealed class PersistenceRulesTests
{
    [Fact]
    public void Only_the_context_and_the_set_reach_a_driver_collection()
    {
        var offenders = new List<string>();

        foreach (var file in InfrastructureSources())
        {
            var name = Path.GetFileName(file);

            // MongoSet is the wrapper; MongoContext is where collections come
            // from in the first place.
            if (name is "MongoSet.cs" or "MongoContext.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);

            foreach (var pattern in new[] { "GetCollection<", "IMongoCollection<", "WithoutSession" })
            {
                if (text.Contains(pattern, StringComparison.Ordinal))
                {
                    offenders.Add($"{name} uses {pattern}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a collection reached outside MongoSet<T> does not join the request's transaction, " +
            "so its write survives an abort");
    }

    [Fact]
    public void Every_write_the_repositories_make_goes_through_the_wrapper()
    {
        // The wrapper's own write methods. Anything calling one of these is
        // calling it on a MongoSet, because that is the only thing the context
        // hands out - which the test above is what guarantees.
        var writes = new[]
        {
            "InsertOneAsync", "InsertManyAsync", "ReplaceOneAsync", "UpdateOneAsync",
            "UpdateManyAsync", "DeleteOneAsync", "DeleteManyAsync", "BulkWriteAsync",
        };

        var wrapper = File.ReadAllText(Path.Combine(InfrastructureRoot(), "Persistence", "MongoSet.cs"));

        foreach (var write in writes)
        {
            wrapper.Should().Contain(
                $"public Task",
                "the wrapper has to offer every write the repositories use");

            wrapper.Should().Contain(
                write,
                $"{write} is used by a repository, so the wrapper must cover it");

            // Each one has to actually pass the session, not just exist.
            var method = Regex.Match(
                wrapper,
                $@"{write}\([^)]*\)\s*=>\s*Session is \{{ \}} session",
                RegexOptions.Singleline);

            method.Success.Should().BeTrue($"{write} must use the ambient session when there is one");
        }
    }

    [Fact]
    public void The_transaction_runner_enlists_the_session()
    {
        var runner = File.ReadAllText(
            Path.Combine(InfrastructureRoot(), "Persistence", "MongoTransactionRunner.cs"));

        runner.Should().Contain(
            "_context.Enlist(session)",
            "starting a transaction whose session nothing uses is the bug this replaced");
    }

    private static IEnumerable<string> InfrastructureSources()
        => Directory.EnumerateFiles(InfrastructureRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string InfrastructureRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        // Directory.Build.props, not a documentation file: it sits at the
        // repository root by definition, and its name is the same on a
        // case-sensitive file system, which CI runs on.
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root has to be findable from the test output directory");

        return Path.Combine(directory!.FullName, "backend", "src", "NeoPrint.Infrastructure");
    }
}
