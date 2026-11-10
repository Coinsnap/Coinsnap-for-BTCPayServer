using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.Coinsnap.ReleaseGate;

/// <summary>BTCPay rejected the configuration. Distinct from transport failures so tests can assert on it.</summary>
public sealed class GreenfieldValidationException(string message) : Exception(message);

/// <summary>Just enough Greenfield to drive the gate: a user, stores, lightning config, invoices.</summary>
public sealed class GreenfieldClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _email;
    private readonly string _password;
    private HttpClient? _browser;

    private GreenfieldClient(HttpClient http, string email, string password)
    {
        _http = http;
        _email = email;
        _password = password;
    }

    public static async Task<GreenfieldClient> BootstrapAsync(Uri baseAddress)
    {
        var email = $"release-gate-{Guid.NewGuid():N}@example.invalid";
        const string password = "Release-Gate-Passw0rd!";

        // Admin registration is open on a dev instance, so the gate can mint its own admin even when
        // the database already has users from earlier runs.
        using (var anonymous = new HttpClient { BaseAddress = baseAddress })
        {
            using var created = await anonymous.PostAsJsonAsync(
                "api/v1/users", new { email, password, isAdministrator = true });
            if (!created.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Could not create a gate user ({(int)created.StatusCode}): {await created.Content.ReadAsStringAsync()}");
        }

        return await LoginAsync(baseAddress, email, password);
    }

    private static async Task<GreenfieldClient> LoginAsync(Uri baseAddress, string email, string password)
    {
        var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(2) };

        // Basic auth only works while the account is under five minutes old
        // (BasicAuthenticationHandler: isNewAccount). Trade it for an API key immediately, or the
        // gate starts returning 401 partway through any run that outlives the window.
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{password}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        using var key = await http.PostAsJsonAsync("api/v1/api-keys",
            new { label = "release-gate", permissions = new[] { "unrestricted" } });
        if (!key.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not mint a gate API key ({(int)key.StatusCode}): {await key.Content.ReadAsStringAsync()}");

        var token = JsonDocument.Parse(await key.Content.ReadAsStringAsync())
            .RootElement.GetProperty("apiKey").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);

        return new GreenfieldClient(http, email, password);
    }

    public async Task<string> CreateStoreAsync(string name, TimeSpan? invoiceExpiration = null)
    {
        // Greenfield serialises these as whole seconds.
        object body = invoiceExpiration is { } expiry
            ? new { name, invoiceExpiration = (int)expiry.TotalSeconds }
            : new { name };

        using var response = await _http.PostAsJsonAsync("api/v1/stores", body);
        var store = await ReadAsync(response);

        if (invoiceExpiration is { } expected &&
            store.TryGetProperty("invoiceExpiration", out var actual) &&
            actual.GetInt32() != (int)expected.TotalSeconds)
            throw new InvalidOperationException(
                $"Store kept an invoice expiry of {actual.GetInt32()}s instead of " +
                $"{(int)expected.TotalSeconds}s; a timing test would be meaningless.");

        return store.GetProperty("id").GetString()!;
    }

    /// <summary>The connection string the settings page would save, built from what the page renders for this user.</summary>
    public async Task<string> GetCoinsnapConnectionAsync(string storeId, string lightningAddress)
    {
        var browser = await BrowserAsync();
        using var response = await browser.GetAsync($"stores/{storeId}/lightning/BTC/setup");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"The setup page of store {storeId} returned {(int)response.StatusCode} {response.Headers.Location}");

        var page = await response.Content.ReadAsStringAsync();
        var panel = Regex.Match(page, "<div id=\"CoinsnapSetup\"[^>]*>", RegexOptions.Singleline).Value;
        string Attribute(string name) => WebUtility.HtmlDecode(
            Regex.Match(panel, $"{name}=\"([^\"]*)\"").Groups[1].Value);

        var connection = $"type=coinsnap;server={Attribute("data-server")};ln-address={lightningAddress};";
        if (Attribute("data-store-id") is { Length: > 0 } id)
            connection += $"store-id={id};";
        if (Attribute("data-binding") is { Length: > 0 } binding)
            connection += $"binding={binding};";
        return connection;
    }

    // Signed in like a browser: the binding is only rendered on the settings page.
    private async Task<HttpClient> BrowserAsync()
    {
        if (_browser is not null)
            return _browser;

        var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
        {
            BaseAddress = _http.BaseAddress,
            Timeout = TimeSpan.FromMinutes(2)
        };
        var loginPage = await browser.GetStringAsync("login");
        var token = Regex.Match(loginPage, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
        using var login = await browser.PostAsync("login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = _email,
            ["Password"] = _password,
            ["Method"] = "Password",
            ["__RequestVerificationToken"] = token
        }));
        if (login.StatusCode != HttpStatusCode.Redirect && login.StatusCode != HttpStatusCode.Found)
            throw new InvalidOperationException($"Could not sign in to the BTCPay UI ({(int)login.StatusCode}).");

        _browser = browser;
        return browser;
    }

    public async Task RemoveStoreUserAsync(string storeId, string idOrEmail)
    {
        using var response = await _http.DeleteAsync($"api/v1/stores/{storeId}/users/{Uri.EscapeDataString(idOrEmail)}");
        await ReadAsync(response);
    }

    public async Task SetCoinsnapLightningAsync(string storeId, string lightningAddress) =>
        await SetLightningConnectionAsync(storeId, await GetCoinsnapConnectionAsync(storeId, lightningAddress));

    /// <summary>Saves a raw connection string, so a test can claim a store-id other than its own.</summary>
    public async Task SetLightningConnectionAsync(string storeId, string connectionString)
    {
        var body = new { enabled = true, config = new { connectionString } };

        using var response = await _http.PutAsJsonAsync(
            $"api/v1/stores/{storeId}/payment-methods/BTC-LN", body);
        await ReadAsync(response);
    }

    public async Task<string> GetLightningConnectionAsync(string storeId)
    {
        using var response = await _http.GetAsync(
            $"api/v1/stores/{storeId}/payment-methods/BTC-LN?includeConfig=true");
        var method = await ReadAsync(response);
        return method.GetProperty("config").GetProperty("connectionString").GetString()!;
    }

    public Task<string> CreateLightningInvoiceAsync(string storeId, long satoshis) =>
        CreateInvoiceAsync(storeId, (satoshis / 100_000_000m).ToString("0.00000000"), "BTC");

    public async Task<string> CreateInvoiceAsync(
        string storeId, string amount, string currency, params string[] paymentMethods)
    {
        var body = new
        {
            amount,
            currency,
            checkout = new { paymentMethods = paymentMethods.Length > 0 ? paymentMethods : new[] { "BTC-LN" } }
        };

        using var response = await _http.PostAsJsonAsync($"api/v1/stores/{storeId}/invoices", body);
        var invoice = await ReadAsync(response);
        return invoice.GetProperty("id").GetString()!;
    }

    /// <summary>Null when BTCPay could not activate Lightning for the invoice.</summary>
    public async Task<string?> TryGetLightningDestinationAsync(string storeId, string invoiceId)
    {
        try
        {
            return await GetLightningDestinationAsync(storeId, invoiceId);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>BTCPay's own LNURL method; the settings page enables it, the BTC-LN API call does not.</summary>
    public async Task EnableLnurlAsync(string storeId)
    {
        using var response = await _http.PutAsJsonAsync(
            $"api/v1/stores/{storeId}/payment-methods/BTC-LNURL", new { enabled = true, config = new { } });
        await ReadAsync(response);
    }

    /// <summary>Creates the store's own Lightning Address, name@btcpay-host, served by BTCPay.</summary>
    public async Task AddLightningAddressAsync(string storeId, string username)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/stores/{storeId}/lightning-addresses/{username}", new { username });
        await ReadAsync(response);
    }

    public async Task<IReadOnlyList<(string Id, string Status)>> ListInvoicesAsync(string storeId)
    {
        using var response = await _http.GetAsync($"api/v1/stores/{storeId}/invoices");
        var invoices = await ReadAsync(response);
        return invoices.EnumerateArray()
            .Select(i => (i.GetProperty("id").GetString()!, i.GetProperty("status").GetString()!))
            .ToList();
    }

    public async Task MarkInvalidAsync(string storeId, string invoiceId)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/stores/{storeId}/invoices/{invoiceId}/status", new { status = "Invalid" });
        await ReadAsync(response);
    }

    public async Task AddStoreUserAsync(string storeId, string email, string role)
    {
        using var response = await _http.PostAsJsonAsync(
            $"api/v1/stores/{storeId}/users", new { userId = email, storeRole = role, requireInvitation = false });
        await ReadAsync(response);
    }

    public async Task DeleteStoreAsync(string storeId)
    {
        using var response = await _http.DeleteAsync($"api/v1/stores/{storeId}");
        await ReadAsync(response);
    }

    /// <summary>Creates a user and returns a client acting as them, plus their credentials for the UI.</summary>
    public async Task<(GreenfieldClient Client, string Email, string Password)> CreateUserAsync(bool isAdministrator)
    {
        var email = $"release-gate-{Guid.NewGuid():N}@example.invalid";
        const string password = "Release-Gate-Passw0rd!";
        using var created = await _http.PostAsJsonAsync("api/v1/users", new { email, password, isAdministrator });
        await ReadAsync(created);
        return (await LoginAsync(_http.BaseAddress!, email, password), email, password);
    }

    public async Task<bool> IsServerAdminAsync()
    {
        using var response = await _http.GetAsync("api/v1/users/me");
        var me = await ReadAsync(response);
        return me.TryGetProperty("roles", out var roles) &&
               roles.EnumerateArray().Any(r => r.GetString() == "ServerAdmin");
    }

    public async Task<string> GetLightningDestinationAsync(string storeId, string invoiceId)
    {
        using var response = await _http.GetAsync(
            $"api/v1/stores/{storeId}/invoices/{invoiceId}/payment-methods");
        var methods = await ReadAsync(response);

        foreach (var method in methods.EnumerateArray())
        {
            if (method.GetProperty("paymentMethodId").GetString() == "BTC-LN" &&
                method.TryGetProperty("destination", out var destination) &&
                destination.GetString() is { Length: > 0 } bolt11)
                return bolt11;
        }

        throw new InvalidOperationException($"Invoice {invoiceId} exposed no BTC-LN destination.");
    }

    /// <summary>Payments recorded against the lightning method. Guards against double crediting.</summary>
    /// <summary>Payments across every method, for invoices BTCPay may record under BTC-LNURL.</summary>
    public async Task<int> CountAllPaymentsAsync(string storeId, string invoiceId)
    {
        using var response = await _http.GetAsync(
            $"api/v1/stores/{storeId}/invoices/{invoiceId}/payment-methods");
        var methods = await ReadAsync(response);
        return methods.EnumerateArray().Sum(m =>
            m.TryGetProperty("payments", out var payments) && payments.ValueKind == JsonValueKind.Array
                ? payments.GetArrayLength()
                : 0);
    }

    public async Task<int> CountPaymentsAsync(string storeId, string invoiceId)
    {
        using var response = await _http.GetAsync(
            $"api/v1/stores/{storeId}/invoices/{invoiceId}/payment-methods");
        var methods = await ReadAsync(response);

        foreach (var method in methods.EnumerateArray())
        {
            if (method.GetProperty("paymentMethodId").GetString() != "BTC-LN")
                continue;
            return method.TryGetProperty("payments", out var payments) &&
                   payments.ValueKind == JsonValueKind.Array
                ? payments.GetArrayLength()
                : 0;
        }

        return 0;
    }

    public async Task<string> GetStatusAsync(string storeId, string invoiceId)
    {
        using var response = await _http.GetAsync($"api/v1/stores/{storeId}/invoices/{invoiceId}");
        var invoice = await ReadAsync(response);
        return invoice.GetProperty("status").GetString()!;
    }

    /// <summary>How the invoice deviated from the happy path, e.g. PaidLate.</summary>
    public async Task<string> GetAdditionalStatusAsync(string storeId, string invoiceId)
    {
        using var response = await _http.GetAsync($"api/v1/stores/{storeId}/invoices/{invoiceId}");
        var invoice = await ReadAsync(response);
        return invoice.GetProperty("additionalStatus").GetString()!;
    }

    public async Task<string> WaitForStatusAsync(
        string storeId, string invoiceId, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var status = await GetStatusAsync(storeId, invoiceId);

        while (status != expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            status = await GetStatusAsync(storeId, invoiceId);
        }

        return status;
    }

    public async Task<int> WaitForPaymentCountAsync(
        string storeId, string invoiceId, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var count = await CountPaymentsAsync(storeId, invoiceId);

        while (count < expected && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            count = await CountPaymentsAsync(storeId, invoiceId);
        }

        return count;
    }

    /// <summary>
    /// Polls for a window and returns the first status that is not <paramref name="expected"/>,
    /// or <paramref name="expected"/> if it never changed. Lets a test assert that something
    /// stayed put, which a single sample could only ever assert by luck.
    /// </summary>
    public async Task<string> WatchForDeviationAsync(
        string storeId, string invoiceId, string expected, TimeSpan window)
    {
        var deadline = DateTimeOffset.UtcNow + window;
        do
        {
            var status = await GetStatusAsync(storeId, invoiceId);
            if (status != expected)
                return status;
            await Task.Delay(TimeSpan.FromSeconds(2));
        } while (DateTimeOffset.UtcNow < deadline);

        return expected;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();

        if (response.StatusCode is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest)
            throw new GreenfieldValidationException(payload);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Greenfield returned {(int)response.StatusCode}: {payload}");

        return payload.Length == 0
            ? default
            : JsonDocument.Parse(payload).RootElement.Clone();
    }

    public void Dispose()
    {
        _browser?.Dispose();
        _http.Dispose();
    }
}
