using System.Collections.Concurrent;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NeoPrint.Application.Abstractions;
using NeoPrint.Infrastructure.Conversion;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// A sandbox whose runs do not end until the test lets them: stands in for
/// converter processes that are busy.
/// </summary>
internal sealed class HeldSandbox : IProcessSandbox
{
    private readonly ConcurrentQueue<TaskCompletionSource> _started = new();
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _running;
    private int _most;
    private int _runs;

    /// <summary>How many runs were going at once, at most.</summary>
    public int MostAtOnce => Volatile.Read(ref _most);

    /// <summary>How many runs were started at all.</summary>
    public int Runs => Volatile.Read(ref _runs);

    public int Running => Volatile.Read(ref _running);

    /// <summary>Lets every run - those going and those to come - end.</summary>
    public void Release() => _release.TrySetResult();

    public async Task<SandboxResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, SandboxLimits limits, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _runs);
        var now = Interlocked.Increment(ref _running);

        int seen;
        while (now > (seen = Volatile.Read(ref _most)))
        {
            Interlocked.CompareExchange(ref _most, now, seen);
        }

        try
        {
            await _release.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }

        // No answer file was written: the caller reads that as a failed tool.
        return new SandboxResult(SandboxOutcome.Failed, 1, string.Empty, TimeSpan.Zero);
    }

    public async Task UntilRunningAsync(int count)
    {
        for (var i = 0; i < 200 && Running < count; i++)
        {
            await Task.Delay(25);
        }

        Running.Should().Be(count);
    }
}

/// <summary>
/// How many converter processes run at once for requests somebody is waiting
/// on, and what the rest are told (T-222).
/// </summary>
public sealed class ConverterQueueTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("neoprint-queue-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    private ConverterTools Tools(IProcessSandbox sandbox, int parallel, int queue, int seconds) => new(
        sandbox,
        Options.Create(new ConversionOptions
        {
            WorkRoot = _root,
            InteractiveParallel = parallel,
            InteractiveQueueLength = queue,
            InteractiveQueueSeconds = seconds,
        }),
        NullLogger<ConverterTools>.Instance);

    private static Task<byte[]> RenderAsync(ConverterTools tools)
        => new ToolPageRenderer(tools).RenderPngAsync(new MemoryStream(ConversionTests.OnePagePdf()), 0, 320);

    [Fact]
    public async Task No_more_processes_run_at_once_than_the_limit_and_the_rest_wait_their_turn()
    {
        var sandbox = new HeldSandbox();
        using var tools = Tools(sandbox, parallel: 2, queue: 10, seconds: 30);

        var requests = Enumerable.Range(0, 6).Select(_ => RenderAsync(tools)).ToList();

        await sandbox.UntilRunningAsync(2);
        tools.Waiting.Should().Be(4);

        // A waiting request has written nothing: only the two that hold a
        // slot have a document in the spool.
        Directory.GetDirectories(_root).Should().HaveCount(2);

        sandbox.Release();

        foreach (var request in requests)
        {
            // The held sandbox writes no answer, which is a failed drawing -
            // what matters here is that every request got its turn.
            await ((Func<Task>)(() => request)).Should().ThrowAsync<PageRenderException>();
        }

        sandbox.Runs.Should().Be(6);
        sandbox.MostAtOnce.Should().Be(2);
        tools.Waiting.Should().Be(0);
        Directory.GetDirectories(_root).Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_that_does_not_fit_in_the_queue_is_refused_at_once_as_busy()
    {
        var sandbox = new HeldSandbox();
        using var tools = Tools(sandbox, parallel: 1, queue: 1, seconds: 30);

        var running = RenderAsync(tools);
        await sandbox.UntilRunningAsync(1);
        var waiting = RenderAsync(tools);

        for (var i = 0; i < 200 && tools.Waiting < 1; i++)
        {
            await Task.Delay(10);
        }

        var started = DateTime.UtcNow;
        var refused = await ((Func<Task>)(() => RenderAsync(tools))).Should().ThrowAsync<FileProcessingBusyException>();

        refused.Which.RetryAfterSeconds.Should().Be(ConverterTools.RetryAfterSeconds);
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5), "a full queue is not waited in");
        sandbox.Runs.Should().Be(1, "the refused request started no process");

        sandbox.Release();
        await ((Func<Task>)(() => running)).Should().ThrowAsync<PageRenderException>();
        await ((Func<Task>)(() => waiting)).Should().ThrowAsync<PageRenderException>();

        // With the load gone the next request is taken again.
        await ((Func<Task>)(() => RenderAsync(tools))).Should().ThrowAsync<PageRenderException>();
        sandbox.Runs.Should().Be(3);
    }

    [Fact]
    public async Task A_request_that_waited_its_time_out_is_refused_as_busy_and_leaves_the_queue()
    {
        var sandbox = new HeldSandbox();
        using var tools = Tools(sandbox, parallel: 1, queue: 5, seconds: 1);

        var running = RenderAsync(tools);
        await sandbox.UntilRunningAsync(1);

        await ((Func<Task>)(() => RenderAsync(tools))).Should().ThrowAsync<FileProcessingBusyException>();

        tools.Waiting.Should().Be(0);
        sandbox.Runs.Should().Be(1);

        sandbox.Release();
        await ((Func<Task>)(() => running)).Should().ThrowAsync<PageRenderException>();
    }

    [Fact]
    public async Task A_caller_who_gives_up_while_waiting_frees_its_place()
    {
        var sandbox = new HeldSandbox();
        using var tools = Tools(sandbox, parallel: 1, queue: 1, seconds: 30);

        var running = RenderAsync(tools);
        await sandbox.UntilRunningAsync(1);

        using var gaveUp = new CancellationTokenSource();
        var waiting = new ToolPageRenderer(tools).RenderPngAsync(new MemoryStream(ConversionTests.OnePagePdf()), 0, 320, gaveUp.Token);

        for (var i = 0; i < 200 && tools.Waiting < 1; i++)
        {
            await Task.Delay(10);
        }

        gaveUp.Cancel();
        await ((Func<Task>)(() => waiting)).Should().ThrowAsync<OperationCanceledException>();
        tools.Waiting.Should().Be(0);

        sandbox.Release();
        await ((Func<Task>)(() => running)).Should().ThrowAsync<PageRenderException>();
    }
}

/// <summary>The page images kept in memory for a few minutes (T-222).</summary>
public sealed class PagePreviewCacheTests
{
    /// <summary>A clock the test moves by hand.</summary>
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static PagePreviewCache Cache(TestTime? time = null, int megabytes = 1, int minutes = 10)
        => new(Options.Create(new ConversionOptions { PreviewCacheMegabytes = megabytes, PreviewCacheMinutes = minutes }), time);

    private static Func<CancellationToken, Task<byte[]>> Draw(Action count, int size = 1024)
        => _ =>
        {
            count();
            return Task.FromResult(new byte[size]);
        };

    [Fact]
    public async Task A_page_is_drawn_once_and_then_answered_from_memory()
    {
        var cache = Cache();
        var drawn = 0;

        var first = await cache.GetOrAddAsync("hash:1:320", Draw(() => drawn++));
        var second = await cache.GetOrAddAsync("hash:1:320", Draw(() => drawn++));

        drawn.Should().Be(1);
        second.Should().BeSameAs(first);

        // Another page, another width and another document are other pictures.
        await cache.GetOrAddAsync("hash:2:320", Draw(() => drawn++));
        await cache.GetOrAddAsync("hash:1:640", Draw(() => drawn++));
        await cache.GetOrAddAsync("other:1:320", Draw(() => drawn++));

        drawn.Should().Be(4);
        cache.Count.Should().Be(4);
    }

    [Fact]
    public async Task Requests_for_the_same_page_at_the_same_moment_share_one_drawing()
    {
        var cache = Cache();
        var drawn = 0;
        var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<byte[]> Ask() => cache.GetOrAddAsync("hash:1:320", _ =>
        {
            Interlocked.Increment(ref drawn);
            return gate.Task;
        });

        var requests = Enumerable.Range(0, 8).Select(_ => Ask()).ToList();
        await Task.Delay(100);
        gate.SetResult(new byte[10]);

        var images = await Task.WhenAll(requests);

        drawn.Should().Be(1);
        images.Should().AllSatisfy(image => image.Should().BeSameAs(images[0]));
    }

    [Fact]
    public async Task A_page_nobody_asked_for_in_a_while_is_forgotten_and_one_still_in_use_is_kept()
    {
        var time = new TestTime();
        var cache = Cache(time, minutes: 10);
        var drawn = 0;

        await cache.GetOrAddAsync("used", Draw(() => drawn++));
        await cache.GetOrAddAsync("idle", Draw(() => drawn++));

        time.Advance(TimeSpan.FromMinutes(6));
        await cache.GetOrAddAsync("used", Draw(() => drawn++));

        time.Advance(TimeSpan.FromMinutes(6));
        await cache.GetOrAddAsync("used", Draw(() => drawn++));
        drawn.Should().Be(2, "asked for again within ten minutes each time");

        await cache.GetOrAddAsync("idle", Draw(() => drawn++));
        drawn.Should().Be(3, "twelve minutes without anyone asking");
    }

    [Fact]
    public async Task The_cache_stays_within_its_size_by_dropping_what_was_used_longest_ago()
    {
        var cache = Cache(megabytes: 1);
        var drawn = 0;
        const int Size = 400 * 1024;

        await cache.GetOrAddAsync("a", Draw(() => drawn++, Size));
        await cache.GetOrAddAsync("b", Draw(() => drawn++, Size));
        await cache.GetOrAddAsync("a", Draw(() => drawn++, Size));
        await cache.GetOrAddAsync("c", Draw(() => drawn++, Size));

        cache.Bytes.Should().BeLessOrEqualTo(1024 * 1024);
        cache.Count.Should().Be(2);

        await cache.GetOrAddAsync("a", Draw(() => drawn++, Size));
        drawn.Should().Be(3, "a was used after b, so b went");

        await cache.GetOrAddAsync("b", Draw(() => drawn++, Size));
        drawn.Should().Be(4);

        // An image larger than the whole cache is served but not kept.
        await cache.GetOrAddAsync("huge", Draw(() => drawn++, 2 * 1024 * 1024));
        await cache.GetOrAddAsync("huge", Draw(() => drawn++, 2 * 1024 * 1024));
        drawn.Should().Be(6);
        cache.Bytes.Should().BeLessOrEqualTo(1024 * 1024);
    }

    [Fact]
    public async Task A_drawing_that_failed_is_not_remembered()
    {
        var cache = Cache();
        var failing = () => cache.GetOrAddAsync("hash:1:320", _ => throw new FileProcessingBusyException(3));

        await failing.Should().ThrowAsync<FileProcessingBusyException>();

        var drawn = 0;
        await cache.GetOrAddAsync("hash:1:320", Draw(() => drawn++));
        drawn.Should().Be(1);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_fail_the_others_waiting_for_the_same_page()
    {
        var cache = Cache();
        using var gaveUp = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = cache.GetOrAddAsync(
            "hash:1:320",
            async token =>
            {
                firstStarted.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return [];
            },
            gaveUp.Token);

        await firstStarted.Task;

        var drawn = 0;
        var second = cache.GetOrAddAsync("hash:1:320", Draw(() => drawn++));

        gaveUp.Cancel();

        await ((Func<Task>)(() => first)).Should().ThrowAsync<OperationCanceledException>();
        (await second).Should().HaveCount(1024);
        drawn.Should().Be(1);
    }

    [Fact]
    public async Task A_cache_of_no_size_keeps_nothing()
    {
        var cache = Cache(megabytes: 0);
        var drawn = 0;

        await cache.GetOrAddAsync("hash:1:320", Draw(() => drawn++));
        await cache.GetOrAddAsync("hash:1:320", Draw(() => drawn++));

        drawn.Should().Be(2);
        cache.Count.Should().Be(0);
    }
}
