using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for WHICH travel provider implementation serves the tripadvisor
/// family, and for the observability that makes the choice a fact.
///
/// THE PROVEN PRODUCTION FAILURE. A direct place search logged
/// providerStatus=Unknown — a value Terra can never produce, and the only
/// clue that the legacy Content API had answered instead. The old selection
/// rule was "first CONFIGURED provider in registration order", so an
/// unconfigured Terra silently handed the whole family to legacy.
///
/// THE RULES PINNED HERE. Within a family the ENABLED implementation with the
/// lowest fixed SelectionPriority owns it (Terra=0, legacy=100 — DI order can
/// never decide). An owner that is enabled but not configured FAILS CLOSED:
/// nobody serves the family, and the caller gets the structured
/// not-configured path instead of a silent downgrade. The selection is
/// observable at startup, on every provider call, and in the authenticated
/// status endpoint — as booleans and fixed ids, never a key.
///
/// Behavioural tests drive the real registry and the real Terra provider with
/// in-memory configuration. Source assertions cover wiring and log shapes,
/// and are labelled as such.
/// </summary>
public class ProviderSelectionEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static TravelDataRegistry Registry(params ITravelDataProvider[] providers)
        => new(providers, NullLogger<TravelDataRegistry>.Instance);

    private static TerraTravelProvider Terra(params (string Key, string? Value)[] settings)
        => new(
            new UnusedHttpClientFactory(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(pair => pair.Key, pair => pair.Value))
                .Build(),
            NullLogger<TerraTravelProvider>.Instance);

    private static TravelPlaceQuery Query() => new() { Near = "Linz", Limit = 3 };

    // ── 1. The documented section binds ───────────────────────────────────

    [Fact]
    public void The_terra_section_and_keys_are_exactly_as_documented()
    {
        // Behavioural: the real provider against in-memory configuration
        // using exactly the documented section and key names.
        var configured = Terra(("TripadvisorTerra:Enabled", "true"), ("TripadvisorTerra:ApiKey", "k"));
        Assert.True(configured.IsEnabled);
        Assert.True(configured.HasApiKey);
        Assert.True(configured.HasValidBaseUrl);   // default BaseUrl is https
        Assert.True(configured.IsConfigured);

        var keyless = Terra(("TripadvisorTerra:Enabled", "true"));
        Assert.True(keyless.IsEnabled);
        Assert.False(keyless.HasApiKey);
        Assert.False(keyless.IsConfigured);

        var disabled = Terra(("TripadvisorTerra:ApiKey", "k"));
        Assert.False(disabled.IsEnabled);
        Assert.False(disabled.IsConfigured);

        // SOURCE ASSERTIONS: the exact configuration keys, so a renamed
        // section cannot silently orphan the Railway variables.
        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");
        Assert.Contains("\"TripadvisorTerra:Enabled\"", terra);
        Assert.Contains("_config[\"TripadvisorTerra:ApiKey\"]", terra);
        Assert.Contains("_config[\"TripadvisorTerra:BaseUrl\"]", terra);
        Assert.Contains("\"TripadvisorTerra:TimeoutSeconds\"", terra);
    }

    // ── 2–6. The selection rule, behaviourally ────────────────────────────

    [Fact]
    public async Task Terra_enabled_and_configured_wins_the_family()
    {
        var terra = new FakeProvider { Implementation = "terra", SelectionPriority = 0, IsEnabled = true, IsConfigured = true };
        var legacy = new FakeProvider { Implementation = "legacy", SelectionPriority = 100, IsEnabled = true, IsConfigured = true };

        var registry = Registry(terra, legacy);
        var result = await registry.SearchAllAsync(Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Ok, result.Status);
        Assert.Equal(1, terra.StatusCalls);
        Assert.Equal(0, legacy.StatusCalls);
        Assert.Equal("terra", registry.SelectedImplementationFor("tripadvisor"));
    }

    [Fact]
    public async Task Terra_enabled_but_unconfigured_fails_closed_and_never_picks_legacy()
    {
        var terra = new FakeProvider { Implementation = "terra", SelectionPriority = 0, IsEnabled = true, IsConfigured = false };
        var legacy = new FakeProvider { Implementation = "legacy", SelectionPriority = 100, IsEnabled = true, IsConfigured = true };

        var registry = Registry(terra, legacy);
        var result = await registry.SearchAllAsync(Query(), CancellationToken.None);

        // FAIL CLOSED: nobody runs — a misconfiguration is a visible failure,
        // never a silent downgrade to the sibling.
        Assert.Equal(0, terra.StatusCalls);
        Assert.Equal(0, legacy.StatusCalls);
        Assert.Equal(TravelSearchStatus.Failed, result.Status);
        Assert.Empty(result.Places);
        Assert.Null(registry.SelectedImplementationFor("tripadvisor"));
        Assert.False(registry.HasConfiguredProvider);
    }

    [Fact]
    public async Task Terra_disabled_lets_a_configured_legacy_serve()
    {
        var terra = new FakeProvider { Implementation = "terra", SelectionPriority = 0, IsEnabled = false, IsConfigured = false };
        var legacy = new FakeProvider { Implementation = "legacy", SelectionPriority = 100, IsEnabled = true, IsConfigured = true };

        var registry = Registry(terra, legacy);
        var result = await registry.SearchAllAsync(Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Ok, result.Status);
        Assert.Equal(1, legacy.StatusCalls);
        Assert.Equal("legacy", registry.SelectedImplementationFor("tripadvisor"));
    }

    [Fact]
    public async Task Nothing_enabled_is_the_structured_not_configured_path()
    {
        var registry = Registry(
            new FakeProvider { Implementation = "terra", SelectionPriority = 0, IsEnabled = false },
            new FakeProvider { Implementation = "legacy", SelectionPriority = 100, IsEnabled = false });

        var result = await registry.SearchAllAsync(Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Failed, result.Status);
        Assert.False(registry.HasConfiguredProvider);
    }

    // ── 7. Registration order can never decide ────────────────────────────

    [Fact]
    public void Registration_order_cannot_hand_the_family_to_legacy()
    {
        // The legacy provider REGISTERED FIRST, both enabled and configured —
        // the fixed priority still selects terra. This is what makes the old
        // failure mode unrepresentable: order is not part of the rule.
        var legacy = new FakeProvider { Implementation = "legacy", SelectionPriority = 100, IsEnabled = true, IsConfigured = true };
        var terra = new FakeProvider { Implementation = "terra", SelectionPriority = 0, IsEnabled = true, IsConfigured = true };

        Assert.Equal("terra", Registry(legacy, terra).SelectedImplementationFor("tripadvisor"));

        // And the real implementations carry exactly those fixed priorities.
        var terraSource = Source("Services", "Gluno", "TerraTravelProvider.cs");
        Assert.Contains("public int SelectionPriority => 0;", terraSource);

        var legacySource = Source("Services", "Gluno", "TripadvisorTravelProvider.cs");
        Assert.Contains("public int SelectionPriority => 100;", legacySource);
    }

    // ── 8. Family and implementation are separate facts ───────────────────

    [Fact]
    public void Family_and_implementation_are_distinct()
    {
        Assert.Equal("tripadvisor", TerraTravelProvider.ProviderId);
        Assert.Equal("terra", TerraTravelProvider.ImplementationId);

        Assert.Equal("tripadvisor", TripadvisorTravelProvider.ProviderId);
        Assert.Equal("legacy", TripadvisorTravelProvider.ImplementationId);
    }

    // ── 9–10. Startup diagnostics: booleans and enums, never a secret ─────

    [Fact]
    public void The_startup_line_is_booleans_and_fixed_ids_only()
    {
        var program = Source("Program.cs");
        var start = program.IndexOf("[GLUNO] travel provider configuration", StringComparison.Ordinal);
        Assert.True(start >= 0, "the startup configuration line is missing");

        var block = program[start..(start + 700)];

        foreach (var field in new[]
        {
            "terraRegistered={TerraRegistered}", "terraEnabled={TerraEnabled}",
            "terraApiKeyPresent={TerraApiKeyPresent}", "terraBaseUrlPresent={TerraBaseUrlPresent}",
            "terraConfigured={TerraConfigured}", "legacyRegistered={LegacyRegistered}",
            "legacyEnabled={LegacyEnabled}", "legacyConfigured={LegacyConfigured}",
            "selectedProvider={SelectedProvider}",
        })
        {
            Assert.Contains(field, block);
        }

        // Presence flags only — never the key, never a length, never a URL.
        // (HasApiKey/HasValidBaseUrl are booleans; the raw properties are
        // private to the provider and cannot even be referenced here.)
        Assert.DoesNotContain(".ApiKey", block);
        Assert.DoesNotContain(".BaseUrl", block);
        Assert.DoesNotContain("Length", block);
    }

    [Fact]
    public void The_api_key_presence_flag_is_a_boolean_and_the_key_never_leaves_the_provider()
    {
        var terra = Source("Services", "Gluno", "TerraTravelProvider.cs");

        Assert.Contains("public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);", terra);
        // The key itself stays private.
        Assert.Contains("private string? ApiKey", terra);
        Assert.DoesNotContain("public string? ApiKey", terra);
    }

    // ── 11–13. The implementation is a logged fact, Unknown is legacy-only ─

    [Fact]
    public void Every_provider_call_logs_the_implementation()
    {
        var registry = Source("Services", "Gluno", "TravelDataRegistry.cs");

        Assert.Contains(
            "[GLUNO] travel provider result family={Family} implementation={Implementation} ",
            registry);
        Assert.Contains("status={Status} requestId={RequestId}", registry);
        Assert.Contains("implementation={Implementation} lookup failed: {Category}", registry);
    }

    [Fact]
    public async Task Terra_never_reports_unknown()
    {
        // Behavioural: even fully unconfigured, Terra answers Failed — never
        // Unknown, which is reserved for a provider that cannot report.
        var result = await ((ITravelDataProvider)Terra()).SearchPlacesWithStatusAsync(
            Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Failed, result.Status);

        // And no path in the provider can produce it.
        Assert.DoesNotContain(
            "TravelSearchStatus.Unknown",
            Source("Services", "Gluno", "TerraTravelProvider.cs"));
    }

    [Fact]
    public async Task Unknown_can_only_come_from_the_interface_default()
    {
        // The legacy provider deliberately has no status override, so the
        // interface default answers Unknown — the exact signature that proved
        // legacy had run in production.
        Assert.DoesNotContain(
            "SearchPlacesWithStatusAsync",
            Source("Services", "Gluno", "TripadvisorTravelProvider.cs"));

        var defaulted = new SearchOnlyFake();
        var result = await ((ITravelDataProvider)defaulted).SearchPlacesWithStatusAsync(
            Query(), CancellationToken.None);

        Assert.Equal(TravelSearchStatus.Unknown, result.Status);
    }

    // ── 14–15. The status endpoint tells the same story, behind auth ──────

    [Fact]
    public void The_status_endpoint_reports_the_registrys_own_selection()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        // The registry's selection, never a parallel guess.
        Assert.Contains("_travelRegistry.SelectedImplementationFor(TerraTravelProvider.ProviderId)", controller);

        var dto = Source("Dtos", "GlunoDtos.cs");
        foreach (var field in new[]
        {
            "TravelProviderFamily", "TravelProviderImplementation",
            "TerraEnabled", "TerraConfigured", "LegacyConfigured",
        })
        {
            Assert.Contains($"public {(field.StartsWith("Travel") ? "string?" : "bool")} {field}", dto);
        }

        // Nothing secret in the DTO: no key, no BaseUrl, no host.
        var dtoStart = dto.IndexOf("public class GlunoTravelDataDto", StringComparison.Ordinal);
        var dtoBlock = dto[dtoStart..(dtoStart + 2500)];
        Assert.DoesNotContain("ApiKey", dtoBlock);
        Assert.DoesNotContain("BaseUrl", dtoBlock);
    }

    [Fact]
    public void The_status_endpoint_requires_auth()
    {
        var controller = Source("Controllers", "GlunoController.cs");

        // Controller-wide [Authorize], and no anonymous carve-out anywhere.
        var classDeclaration = controller.IndexOf("public class GlunoController", StringComparison.Ordinal);
        Assert.Contains("[Authorize]", controller[..classDeclaration]);
        Assert.DoesNotContain("[AllowAnonymous]", controller);
    }

    // ── Test doubles ──────────────────────────────────────────────────────

    /// A provider with every selection fact settable. Implements the status
    /// search EXPLICITLY so calls are countable; the search-only fake below
    /// exists to exercise the interface default instead.
    private sealed class FakeProvider : ITravelDataProvider
    {
        public string Provider { get; init; } = "tripadvisor";
        public string Implementation { get; init; } = "fake";
        public int SelectionPriority { get; init; } = 100;
        public bool IsEnabled { get; init; }
        public bool IsConfigured { get; init; }
        public bool AllowsContentPersistence => true;
        public bool AllowsLocationIdPersistence => true;

        public int StatusCalls;

        public Task<IReadOnlyList<TravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TravelPlace>>(Array.Empty<TravelPlace>());

        public Task<TravelSearchResult> SearchPlacesWithStatusAsync(TravelPlaceQuery query, CancellationToken ct)
        {
            StatusCalls++;
            return Task.FromResult(new TravelSearchResult
            {
                Places = Array.Empty<TravelPlace>(),
                Status = TravelSearchStatus.Ok,
            });
        }

        public Task<TravelPlace?> GetPlaceDetailsAsync(string providerPlaceId, string language, CancellationToken ct)
            => Task.FromResult<TravelPlace?>(null);
    }

    /// No status override — exactly the legacy provider's shape, so the
    /// interface default (Unknown) is what answers.
    private sealed class SearchOnlyFake : ITravelDataProvider
    {
        public string Provider => "tripadvisor";
        public bool IsConfigured => true;
        public bool AllowsContentPersistence => true;
        public bool AllowsLocationIdPersistence => true;

        public Task<IReadOnlyList<TravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TravelPlace>>(Array.Empty<TravelPlace>());

        public Task<TravelPlace?> GetPlaceDetailsAsync(string providerPlaceId, string language, CancellationToken ct)
            => Task.FromResult<TravelPlace?>(null);
    }

    /// Never invoked: the unconfigured provider answers before any HTTP.
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException("no HTTP call belongs in these tests");
    }
}
