using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using sidequest.backend.Dtos;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for whether Gluno will answer at all.
///
/// One failure mode, and it is the expensive one: telling somebody the
/// assistant does not exist here when it does. That reads as a removed
/// feature, not as a transient problem, and nobody comes back to check.
///
/// It happens in two ways. The backend folds an OPTIONAL capability into the
/// core boolean, so switching off one paid API takes the whole assistant away.
/// Or the app treats a failed check as an answer, so a dropped connection
/// becomes a permanent-sounding notice. Both are covered below.
///
/// Nothing here calls a model, a network, or a database.
/// </summary>
public class AvailabilityEvals
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings)
    {
        // Last one wins, so a case can override a CoreReady default by simply
        // restating the key.
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings) values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// Core configured: switched on, a key, and a primary model.
    private static IConfiguration CoreReady(params (string Key, string? Value)[] extra)
        => Config(
        [
            ("Gluno:Enabled", "true"),
            ("Gluno:ApiKey", "test-key-not-a-real-credential"),
            ("Gluno:Models:Primary", "configured-primary"),
            .. extra,
        ]);

    private static GlunoAvailability Availability(IConfiguration config)
    {
        var models = new GlunoModelPolicy(config);
        var provider = new AnthropicGlunoAiProvider(config, models, NullLogger<AnthropicGlunoAiProvider>.Instance);

        return new GlunoAvailability(
            config,
            new StubEnvironment(),
            provider,
            new TravelDataRegistry(
                [
                    new TripadvisorTravelProvider(
                        new StubHttpClientFactory(), config, new TravelDataCache(),
                        NullLogger<TripadvisorTravelProvider>.Instance),
                ],
                NullLogger<TravelDataRegistry>.Instance));
    }

    // ── The core, and only the core ──────────────────────────────────────

    [Fact]
    public void Every_optional_provider_off_still_leaves_Gluno_available()
    {
        // Tripadvisor, routing, live information and document analysis are all
        // extras. Gluno plans perfectly well without any of them — it just
        // says its travel times are estimates. Taking the assistant away
        // because one paid API is off would be the wrong trade every time.
        var availability = Availability(CoreReady(
            ("Tripadvisor:Enabled", "false"),
            ("Routing:Enabled", "false"),
            ("Gluno:LiveInfo:Enabled", "false"),
            ("Gluno:Documents:Enabled", "false")));

        Assert.True(availability.IsAvailable);
        Assert.Null(availability.UnavailableReason);
    }

    [Fact]
    public void Availability_is_exactly_enabled_and_configured()
    {
        var availability = Availability(CoreReady());

        Assert.True(availability.IsEnabled);
        Assert.True(availability.IsConfigured);
        Assert.True(availability.IsAvailable);

        // Travel data is reported separately and is false here. If it ever
        // starts pulling IsAvailable down, this is the assertion that catches
        // it.
        Assert.False(availability.HasTravelData);
        Assert.True(availability.IsAvailable);
    }

    [Fact]
    public void A_missing_primary_model_makes_Gluno_unavailable()
    {
        var availability = Availability(Config(
            ("Gluno:Enabled", "true"),
            ("Gluno:ApiKey", "test-key-not-a-real-credential")));

        Assert.False(availability.IsAvailable);
        Assert.Equal("not_configured", availability.UnavailableReason);
    }

    [Fact]
    public void A_missing_key_makes_Gluno_unavailable()
    {
        var availability = Availability(Config(
            ("Gluno:Enabled", "true"),
            ("Gluno:Models:Primary", "configured-primary")));

        Assert.False(availability.IsAvailable);
        Assert.Equal("not_configured", availability.UnavailableReason);
    }

    [Fact]
    public void Disabled_is_reported_as_disabled_rather_than_unconfigured()
    {
        var availability = Availability(CoreReady(("Gluno:Enabled", "false")));

        Assert.False(availability.IsAvailable);
        // Distinct reasons: one is "not for this environment", the other is
        // "somebody needs to set a key". The app says different things.
        Assert.Equal("disabled", availability.UnavailableReason);
    }

    [Fact]
    public void The_legacy_model_key_still_configures_Gluno()
    {
        // A deployment written before Gluno:Models:Primary existed must not
        // silently report aiConfigured=false after an upgrade.
        var availability = Availability(Config(
            ("Gluno:Enabled", "true"),
            ("Gluno:ApiKey", "test-key-not-a-real-credential"),
            ("Gluno:Model", "legacy-configured-model")));

        Assert.True(availability.IsConfigured);
        Assert.True(availability.IsAvailable);
    }

    // ── What the status endpoint may say ─────────────────────────────────

    [Fact]
    public void The_status_contract_separates_the_core_from_the_extras()
    {
        foreach (var name in new[] { "Available", "Enabled", "AiConfigured" })
        {
            var property = typeof(GlunoStatusDto).GetProperty(name);
            Assert.NotNull(property);
            Assert.Equal(typeof(bool), property!.PropertyType);
        }
    }

    [Fact]
    public void The_status_contract_never_names_a_model_a_provider_or_a_key()
    {
        foreach (var property in typeof(GlunoStatusDto).GetProperties())
        {
            var name = property.Name;

            Assert.DoesNotContain("Model", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Key", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Provider", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Url", name, StringComparison.OrdinalIgnoreCase);

            // Booleans, ints and the coarse reason string only — nothing that
            // could carry a configuration value out.
            Assert.True(
                property.PropertyType == typeof(bool)
                || property.PropertyType == typeof(int)
                || property.PropertyType == typeof(string),
                $"{name} is {property.PropertyType.Name}; status must stay flat and non-revealing");
        }
    }

    [Fact]
    public void The_reason_is_coarse_enough_not_to_be_a_configuration_readout()
    {
        // "disabled" and "not_configured" tell the app what to render. Which
        // specific setting is missing is the operator's business, and appears
        // only in the development-only hint the app holds itself.
        foreach (var config in new[]
        {
            Config(("Gluno:Enabled", "false")),
            Config(("Gluno:Enabled", "true")),
        })
        {
            var reason = Availability(config).UnavailableReason;
            Assert.Contains(reason, new[] { "disabled", "not_configured" });
        }
    }

    // ── Scope does not gate availability ─────────────────────────────────

    [Fact]
    public void Availability_knows_nothing_about_a_trip_or_a_user()
    {
        // No parameters anywhere on this type. Global Gluno cannot be blocked
        // by an Adventure, and an Adventure cannot be blocked by the global
        // scope — losing membership is a per-request authorisation failure on
        // that Adventure, not the assistant disappearing.
        foreach (var name in new[] { "IsAvailable", "IsEnabled", "IsConfigured" })
        {
            var property = typeof(GlunoAvailability).GetProperty(name);
            Assert.NotNull(property);
            Assert.Empty(property!.GetIndexParameters());
        }
    }

    [Fact]
    public void Losing_membership_is_its_own_error_and_not_unavailability()
    {
        // Two distinct outcomes, deliberately. Collapsing them would turn "you
        // are not in this Adventure any more" into "Gluno does not exist".
        Assert.NotEqual(GlunoTurnError.Unavailable, GlunoTurnError.NotTripMember);
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        // Production, so nothing below passes by accident on a Development
        // default. Every positive case above sets Gluno:Enabled explicitly.
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string WebRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
