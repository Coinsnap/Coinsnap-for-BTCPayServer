using BTCPayServer.Abstractions.Contracts;

namespace BTCPayServer.Plugins.Coinsnap.Tests;

public class StoreBindingKeyTests
{
    [Fact]
    public void ABindingVerifiesForItsOwnStoreOnly()
    {
        var binding = new CoinsnapStoreBinding(new MemorySettings());

        var signed = binding.Sign("victim");

        Assert.True(binding.Verify("victim", signed));
        Assert.False(binding.Verify("attacker", signed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("b-victim")]
    public void AMissingOrGuessedBindingIsRejected(string? guess)
    {
        var binding = new CoinsnapStoreBinding(new MemorySettings());

        Assert.False(binding.Verify("victim", guess));
    }

    [Fact]
    public void ATamperedBindingIsRejected()
    {
        var binding = new CoinsnapStoreBinding(new MemorySettings());
        var signed = binding.Sign("victim");
        var tampered = (signed[0] == 'A' ? 'B' : 'A') + signed[1..];

        Assert.False(binding.Verify("victim", tampered));
    }

    [Fact]
    public void TheBindingIsSafeInsideAConnectionString()
    {
        var signed = new CoinsnapStoreBinding(new MemorySettings()).Sign("victim");

        Assert.DoesNotContain(signed, c => c is ';' or '=' || char.IsWhiteSpace(c));
    }

    /// <summary>Saved connections must keep working after a restart, so the key is persisted, not regenerated.</summary>
    [Fact]
    public void TheKeySurvivesARestart()
    {
        var settings = new MemorySettings();
        var signed = new CoinsnapStoreBinding(settings).Sign("victim");

        var afterRestart = new CoinsnapStoreBinding(settings);

        Assert.True(afterRestart.Verify("victim", signed));
    }

    [Fact]
    public void AnotherServerCannotProduceThisServersBinding()
    {
        var signed = new CoinsnapStoreBinding(new MemorySettings()).Sign("victim");

        Assert.False(new CoinsnapStoreBinding(new MemorySettings()).Verify("victim", signed));
    }

    [Fact]
    public async Task RotatingRevokesEveryEarlierBindingOfThatStore()
    {
        var binding = new CoinsnapStoreBinding(new MemorySettings());
        var leaked = binding.Sign("victim");

        await binding.RotateAsync("victim");

        Assert.False(binding.Verify("victim", leaked));
        Assert.True(binding.Verify("victim", binding.Sign("victim")));
    }

    [Fact]
    public async Task RotatingOneStoreLeavesOthersValid()
    {
        var binding = new CoinsnapStoreBinding(new MemorySettings());
        var other = binding.Sign("other");
        binding.Sign("victim");

        await binding.RotateAsync("victim");

        Assert.True(binding.Verify("other", other));
    }

    /// <summary>A revoked binding must stay revoked across a restart.</summary>
    [Fact]
    public async Task ARotationSurvivesARestart()
    {
        var settings = new MemorySettings();
        var first = new CoinsnapStoreBinding(settings);
        var leaked = first.Sign("victim");
        await first.RotateAsync("victim");
        var current = first.Sign("victim");

        var afterRestart = new CoinsnapStoreBinding(settings);

        Assert.False(afterRestart.Verify("victim", leaked));
        Assert.True(afterRestart.Verify("victim", current));
    }

    [Fact]
    public void AStoreThatWasNeverIssuedABindingHasNone()
    {
        var binding = new CoinsnapStoreBinding(new MemorySettings());

        Assert.False(binding.Verify("never-issued", "anything"));
        Assert.Empty(binding.BoundStores);
    }

    [Fact]
    public void StoresThatWereIssuedABindingAreListed()
    {
        var binding = new CoinsnapStoreBinding(new MemorySettings());
        binding.Sign("a");
        binding.Sign("b");

        Assert.Equal(["a", "b"], binding.BoundStores.Order());
    }

    /// <summary>Saves that finish out of order must not resurrect a revoked nonce after a restart.</summary>
    [Fact]
    public async Task ConcurrentRotationsPersistTheLatestNonce()
    {
        var settings = new MemorySettings(slowSaves: true);
        var binding = new CoinsnapStoreBinding(settings);
        binding.Sign("victim");

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => binding.RotateAsync("victim"))));
        var current = binding.Sign("victim");

        Assert.True(new CoinsnapStoreBinding(settings).Verify("victim", current));
    }

    private sealed class MemorySettings(bool slowSaves = false) : ISettingsRepository
    {
        private readonly Dictionary<string, object> _values = new();

        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class =>
            Task.FromResult(_values.TryGetValue(name ?? typeof(T).FullName!, out var value) ? (T?)value : null);

        public async Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            if (slowSaves)
                await Task.Delay(Random.Shared.Next(0, 15));
            lock (_values)
                _values[name ?? typeof(T).FullName!] = obj;
        }

        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class =>
            throw new NotSupportedException();
    }
}
