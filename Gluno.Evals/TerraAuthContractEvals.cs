using System.Net;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for Terra's authentication contract, added after the proven
/// production 401:
///
///   terra transport status=401 contentType=application/problem+json
///   terra search result=Unauthorized
///
/// with provider selection CORRECT (selectedProvider=terra,
/// terraApiKeyPresent=True). The failure is the credential itself, not the
/// wiring — so what these evals pin is the wiring, in both directions:
/// the request matches Terra's documented contract exactly (X-API-KEY
/// header, no Bearer, no query parameter, the documented base URL and
/// path), the key is read from exactly one configuration source and sent
/// exactly once, and no diagnostic path can ever carry the key, a header
/// or a problem+json body into a log line.
///
/// Terra's own documentation (docs.terra.tripadvisor.com/docs/api-security):
/// "Every request to the Terra API must include a valid API key in the
/// X-API-KEY header", and a 401 means the key was not provided, not found,
/// or not enabled. HTTP header names are case-insensitive, so the code's
/// X-API-Key satisfies X-API-KEY.
/// </summary>
public class TerraAuthContractEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Terra() => Source("Services", "Gluno", "TerraTravelProvider.cs");

    // ── 1. The auth header matches the documented contract ────────────────

    [Fact]
    public void The_key_travels_in_the_documented_header_with_no_prefix()
    {
        var terra = Terra();

        // X-API-Key, set per request, the raw key value — no Bearer, no
        // scheme, no query parameter. Header names are case-insensitive, so
        // this satisfies the documented X-API-KEY.
        Assert.Contains("request.Headers.Add(\"X-API-Key\", ApiKey);", terra);
        Assert.DoesNotContain("Bearer", terra);
        Assert.DoesNotContain("Authorization", terra);
        // Never in the URL, where request logging and proxies would see it.
        Assert.DoesNotContain("key=", terra);
        Assert.DoesNotContain("apiKey=", terra);
    }

    // ── 2. The documented base URL and path ───────────────────────────────

    [Fact]
    public void The_base_url_and_search_path_are_the_documented_ones()
    {
        var terra = Terra();

        Assert.Contains("\"https://terra.tripadvisor.com/api\"", terra);
        Assert.Contains("SendAsync(\"/recommendations/search\"", terra);
        Assert.Contains("request.Headers.Accept.ParseAdd(\"application/json\");", terra);
    }

    // ── 3. The key is sent exactly once ───────────────────────────────────

    [Fact]
    public void The_key_is_attached_exactly_once_and_never_as_a_client_default()
    {
        var terra = Terra();

        // One attachment site, on the per-request message.
        Assert.Equal(1, terra.Split("Headers.Add(\"X-API-Key\"").Length - 1);

        // And never on the shared client, where it would become ambient state
        // for every request the factory hands out.
        Assert.DoesNotContain("DefaultRequestHeaders.Add(\"X-API-Key\"", terra);

        var program = Source("Program.cs");
        var registration = program.IndexOf(
            ".AddHttpClient(TerraTravelProvider.HttpClientName)", StringComparison.Ordinal);
        Assert.True(registration >= 0);

        // The client registration sets a User-Agent and NOTHING that could
        // collide with or carry auth.
        var block = program[registration..(registration + 400)];
        Assert.Contains("UserAgent.ParseAdd(\"SideQuest/1.0\")", block);
        Assert.DoesNotContain("X-API-Key", block);
        Assert.DoesNotContain("ApiKey", block);
    }

    // ── 4. The key can never reach a log line ─────────────────────────────

    [Fact]
    public void No_terra_log_line_can_carry_the_key()
    {
        var terra = Terra();

        // Every [GLUNO] log FORMAT STRING in the provider, checked against
        // the placeholders that would leak. ($"{BaseUrl}{path}" in the
        // REQUEST construction is C# interpolation, not a log template — the
        // check is scoped to the log lines.)
        var logLines = terra.Split('"').Where(part => part.StartsWith("[GLUNO]", StringComparison.Ordinal));

        Assert.NotEmpty(logLines);

        foreach (var line in logLines)
        {
            foreach (var forbidden in new[] { "{ApiKey}", "{Key}", "{BaseUrl}", "{Url}", "{Header}", "{Body}" })
            {
                Assert.DoesNotContain(forbidden, line);
            }
        }

        // The key property is private, and the only public trace of it is the
        // presence boolean the startup line reads.
        Assert.Contains("private string? ApiKey", terra);
        Assert.Contains("public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);", terra);
    }

    // ── 5. 401 maps to the correct internal status ────────────────────────

    [Fact]
    public void A_401_is_classified_as_unauthorized_and_flagged_for_attention()
    {
        // The real classifier, behaviourally.
        Assert.Equal(TerraFailure.Unauthorized, TerraTravelProvider.Classify(HttpStatusCode.Unauthorized));
        Assert.Equal(TerraFailure.Forbidden, TerraTravelProvider.Classify(HttpStatusCode.Forbidden));

        var terra = Terra();

        // Unauthorized is one of the failures that pages a person rather than
        // suggesting a retry — a credential does not fix itself.
        Assert.Contains("failure is TerraFailure.Unauthorized or TerraFailure.Forbidden", terra);
        Assert.Contains("[GLUNO] terra needs attention result={Result}", terra);

        // TravelSearchStatus deliberately has no Unauthorized value — the
        // wire status stays Failed (neutral retryable provider error in the
        // app) while TerraFailure carries the precise category in the logs.
        Assert.DoesNotContain("TravelSearchStatus.Unauthorized", terra);
    }

    // ── 6–7. problem+json is mined for safe fields only ───────────────────

    [Fact]
    public void The_problem_body_is_never_exported_raw()
    {
        var terra = Terra();
        var start = terra.IndexOf("private async Task LogProblemAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0);

        var body = terra[start..(start + 1800)];

        // Only `type` and field NAMES are read. `title`, `detail` and
        // `message` can quote the request or the account back, and the body
        // itself is never stored, returned or logged.
        Assert.Contains("Text(problem.RootElement, \"type\")", body);
        Assert.DoesNotContain("\"detail\"", body);
        Assert.DoesNotContain("\"title\"", body);
        Assert.DoesNotContain("\"message\"", body);
        Assert.DoesNotContain("ReadAsStringAsync", body);
    }

    [Fact]
    public void The_rejection_line_is_status_type_and_field_names_only()
    {
        // The safe failure class for a 401 is the status plus the problem
        // type — enough to tell "key not found" from "key not enabled" from
        // Terra's own type value, with no free text at all.
        Assert.Contains(
            "[GLUNO] terra rejected request status={Status} type={Type} fields={Fields}",
            Terra());
    }

    // ── 8–9. One credential source, and it is Terra's own ─────────────────

    [Fact]
    public void The_legacy_key_can_never_be_read_by_the_terra_provider()
    {
        var terra = Terra();

        // The legacy section is a different credential for a different
        // product. Terra must never fall back to it — a wrong key that
        // "works" by coincidence would be worse than a clean 401.
        Assert.DoesNotContain("\"Tripadvisor:ApiKey\"", terra);
        Assert.DoesNotContain("\"Tripadvisor:Enabled\"", terra);
    }

    [Fact]
    public void The_terra_key_has_exactly_one_configuration_source()
    {
        var terra = Terra();

        Assert.Contains("_config[\"TripadvisorTerra:ApiKey\"]", terra);
        Assert.Equal(1, terra.Split("ApiKey\"]").Length - 1);

        // And nothing reads an environment variable or file directly — the
        // one configuration path is what the Railway variable feeds.
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", terra);
    }
}
