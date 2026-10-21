using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Coinsnap;

/// <summary>
/// Issues the connection string the settings page would save, so a store can be configured through
/// the API. Only the store's own managers may ask, which is what keeps its binding private.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
public sealed class GreenfieldCoinsnapController : ControllerBase
{
    private readonly ICoinsnapStoreBinding _binding;
    private readonly CoinsnapBindingRotation _rotation;

    public GreenfieldCoinsnapController(ICoinsnapStoreBinding binding, CoinsnapBindingRotation rotation)
    {
        _binding = binding;
        _rotation = rotation;
    }

    [HttpGet("~/api/v1/stores/{storeId}/coinsnap/connection-string")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public IActionResult GetConnectionString(string storeId, [FromQuery(Name = "ln-address")] string? lnAddress)
    {
        if (!CoinsnapLightningAddress.TryParse(lnAddress ?? "", out var address, out var error))
            return BadRequest(new { message = error });
        return Ok(new { connectionString = Build(address!.Value, storeId, _binding.Sign(storeId)) });
    }

    /// <summary>
    /// Revokes every binding issued to this store before, for a suspected leak, and re-saves the
    /// store's own Coinsnap connection with a new one. Removing or demoting a store user does this
    /// automatically.
    /// </summary>
    [HttpPost("~/api/v1/stores/{storeId}/coinsnap/rotate-binding")]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
    public async Task<IActionResult> RotateBinding(string storeId) =>
        Ok(new { connectionString = await _rotation.RotateAsync(storeId) });

    internal static string Build(string lightningAddress, string storeId, string binding) =>
        $"type=coinsnap;server=https://{CoinsnapConstants.Host};ln-address={lightningAddress};store-id={storeId};binding={binding};";
}
