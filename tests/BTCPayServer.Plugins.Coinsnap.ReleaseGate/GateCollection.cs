namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>
/// One BTCPay for the whole gate. A collection fixture rather than a class fixture because the
/// restart scenarios stop the server, and two classes running in parallel would fight over the
/// port and over each other's restarts.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GateCollection : ICollectionFixture<GateFixture>
{
    public const string Name = "release-gate";
}
