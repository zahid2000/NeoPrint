using System.Reflection;

using NeoPrint.Application.Abstractions;
using NeoPrint.Application.Refunds;

namespace NeoPrint.Api.IntegrationTests;

/// <summary>
/// Every notification the panels can be sent is sent by something (T-261).
/// </summary>
/// <remarks>
/// <para>
/// A notifier method nobody calls fails nothing: it compiles, the hub is ready
/// to deliver it, the panel subscribes to it - and the page never follows.
/// Five of them were like that until T-261: a payment, and a device being
/// registered, activated and revoked had a method each and no caller. The
/// device handlers did call <c>DeviceActivatedAsync</c> - of
/// <see cref="IDeviceNotifier"/>, which tells the device, not the panels; the
/// two interfaces share the name, which is why reading the code did not show it.
/// </para>
/// <para>
/// So this looks at what was compiled rather than at names: the IL of every
/// method in the backend's assemblies is searched for a call to - or a method
/// group of - each method of the two panel notifier interfaces. The panel's
/// own half of the contract is <c>admin/scripts/hub-check.mjs</c>.
/// </para>
/// </remarks>
public sealed class HubContractTests
{
    private static readonly Assembly[] Backend =
    [
        typeof(IAdminNotifier).Assembly,                                  // Application
        typeof(NeoPrint.Infrastructure.Persistence.MongoContext).Assembly, // Infrastructure
        typeof(NeoPrint.Api.Hubs.AdminHub).Assembly,                       // Api
    ];

    [Theory]
    [InlineData(typeof(IAdminNotifier))]
    [InlineData(typeof(IPaymentsAdminNotifier))]
    public void Every_notification_to_the_panels_has_a_caller(Type notifier)
    {
        ArgumentNullException.ThrowIfNull(notifier);

        var called = CalledMethods();

        var orphans = notifier.GetMethods()
            .Where(method => !called.Contains(method))
            .Select(method => method.Name)
            .ToList();

        orphans.Should().BeEmpty(
            "a notification nothing sends is a page that never follows; call it where the thing happens, or take it out of {0}", notifier.Name);
    }

    [Fact]
    public void The_search_tells_a_called_method_from_one_that_is_not()
    {
        // The check above is only worth something if it can fail.
        var called = CalledMethods();

        called.Should().Contain(typeof(IAdminNotifier).GetMethod(nameof(IAdminNotifier.KioskStatusChangedAsync))!);
        called.Should().NotContain(
            typeof(Uncalled).GetMethod(nameof(Uncalled.NobodyCallsThisAsync))!, "the search does not find a call that is not there");
    }

    private interface Uncalled
    {
        Task NobodyCallsThisAsync();
    }

    /// <summary>
    /// Every method that some method of the backend calls or takes as a
    /// method group, found by reading the IL for the opcodes that carry a
    /// method token.
    /// </summary>
    private static HashSet<MethodBase> CalledMethods()
    {
        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var called = new HashSet<MethodBase>();

        foreach (var assembly in Backend)
        {
            // Compiler-made types as well: an async method's body lives in its state machine.
            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                {
                    if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
                    {
                        continue;
                    }

                    Type[]? typeArguments = type.IsGenericType ? type.GetGenericArguments() : null;
                    Type[]? methodArguments = method is MethodInfo { IsGenericMethod: true } ? method.GetGenericArguments() : null;

                    for (var at = 0; at + 4 < il.Length; at++)
                    {
                        // call, callvirt, newobj, and (after 0xFE) ldftn / ldvirtftn: each is followed by a method token.
                        var carriesToken = il[at] is 0x28 or 0x6F or 0x73
                                           || (il[at] == 0xFE && at + 5 < il.Length && il[at + 1] is 0x06 or 0x07);

                        if (!carriesToken)
                        {
                            continue;
                        }

                        var tokenAt = il[at] == 0xFE ? at + 2 : at + 1;

                        try
                        {
                            if (method.Module.ResolveMethod(BitConverter.ToInt32(il, tokenAt), typeArguments, methodArguments) is { } target)
                            {
                                called.Add(target is MethodInfo { IsGenericMethod: true } generic ? generic.GetGenericMethodDefinition() : target);
                            }
                        }
                        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or TypeLoadException or FileNotFoundException)
                        {
                            // A byte that looked like an opcode and was an operand: not a method token.
                        }
                    }
                }
            }
        }

        return called;
    }
}
