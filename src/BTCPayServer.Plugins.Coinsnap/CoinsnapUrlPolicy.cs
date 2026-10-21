using System.Net;

namespace BTCPayServer.Plugins.Coinsnap;

public static class CoinsnapUrlPolicy
{
    public static Uri ParseAndValidate(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            throw new FormatException($"Coinsnap {fieldName} must be an absolute HTTPS URL.");
        Validate(uri, fieldName);
        return uri;
    }

    public static void Validate(Uri uri, string fieldName)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Coinsnap {fieldName} must use HTTPS.");
        if (!uri.Host.Equals(CoinsnapConstants.Host, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Coinsnap {fieldName} must use the allowlisted host {CoinsnapConstants.Host}.");
        if (!uri.IsDefaultPort && uri.Port != 443)
            throw new FormatException($"Coinsnap {fieldName} must use the default HTTPS port.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new FormatException($"Coinsnap {fieldName} must not contain user information.");
        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new FormatException($"Coinsnap {fieldName} must not contain a fragment.");
        if (IPAddress.TryParse(uri.Host, out _))
            throw new FormatException($"Coinsnap {fieldName} must not use an IP address.");
    }

    public static Uri ValidateRedirect(Uri source, Uri? location, string fieldName)
    {
        if (location is null)
            throw new FormatException($"Coinsnap {fieldName} returned a redirect without a Location header.");
        var target = location.IsAbsoluteUri ? location : new Uri(source, location);
        Validate(target, fieldName);
        if (!source.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Coinsnap {fieldName} attempted a cross-domain redirect.");
        return target;
    }
}
