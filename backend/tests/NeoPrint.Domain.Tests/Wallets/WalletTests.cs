using NeoPrint.Domain.Common;
using NeoPrint.Domain.Wallets;

namespace NeoPrint.Domain.Tests.Wallets;

/// <summary>The wallet rules (plan section 2.14; docs/DOMAIN_RULES.md "Wallet").</summary>
public sealed class WalletTests
{
    private static readonly DateTime Now = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

    private static WalletTransaction Post(Wallet wallet, WalletTransactionType type, decimal amount, int pages = 0)
        => wallet.Post(type, Money.FromAzn(amount), pages, "ref", $"key-{Guid.NewGuid():N}", null, Now);

    [Fact]
    public void Every_movement_comes_with_the_ledger_line_that_explains_it()
    {
        var wallet = Wallet.For(Guid.NewGuid());

        var topUp = Post(wallet, WalletTransactionType.TopUp, 5m);
        var payment = Post(wallet, WalletTransactionType.Payment, -1.20m);

        wallet.Balance.Should().Be(Money.FromAzn(3.80m));
        topUp.BalanceAfter.Should().Be(Money.FromAzn(5m));
        payment.BalanceAfter.Should().Be(wallet.Balance);
        payment.UserId.Should().Be(wallet.UserId);
        (topUp.Amount + payment.Amount).Should().Be(wallet.Balance, "the balance is the ledger's sum");
    }

    [Fact]
    public void Nothing_takes_the_balance_or_the_bonus_pages_below_zero_not_even_an_admin()
    {
        var wallet = Wallet.For(Guid.NewGuid());
        Post(wallet, WalletTransactionType.TopUp, 1m);

        FluentActions.Invoking(() => Post(wallet, WalletTransactionType.Payment, -1.01m))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("wallet.insufficient");
        FluentActions.Invoking(() => Post(wallet, WalletTransactionType.AdminAdjust, -2m))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("wallet.insufficient");
        FluentActions.Invoking(() => Post(wallet, WalletTransactionType.Payment, 0m, -1))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("wallet.insufficientBonus");

        wallet.Balance.Should().Be(Money.FromAzn(1m), "a refused movement changes nothing");
    }

    [Theory]
    [InlineData(WalletTransactionType.TopUp, -1)]
    [InlineData(WalletTransactionType.Refund, -1)]
    [InlineData(WalletTransactionType.VoucherRedeem, -1)]
    [InlineData(WalletTransactionType.Bonus, -1)]
    [InlineData(WalletTransactionType.Payment, 1)]
    public void Each_kind_of_movement_goes_one_way_only(WalletTransactionType type, int sign)
    {
        var wallet = Wallet.For(Guid.NewGuid());
        Post(wallet, WalletTransactionType.TopUp, 10m);

        FluentActions.Invoking(() => Post(wallet, type, sign * 1m))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("wallet.wrongDirection");
    }

    [Fact]
    public void An_empty_movement_is_refused()
    {
        FluentActions.Invoking(() => Post(Wallet.For(Guid.NewGuid()), WalletTransactionType.AdminAdjust, 0m))
            .Should().Throw<DomainException>().Which.Text.Key.Should().Be("wallet.nothingToPost");
    }
}
