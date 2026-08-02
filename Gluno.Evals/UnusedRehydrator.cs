using sidequest.backend.Services.Gluno;

namespace Gluno.Evals;

/// <summary>
/// A rehydrator for controller tests that never reach one.
///
/// Throws rather than returning empty: a test that unexpectedly starts fetching
/// places should fail loudly, not quietly get nothing back and assert against
/// it.
/// </summary>
internal sealed class UnusedRehydrator : IGlunoPlaceRehydrator
{
    public Task<GlunoRehydration> RehydrateAsync(
        IReadOnlyList<GlunoPlaceReference> references,
        GlunoPlaceSearchContext context,
        string? requiredOptionKey,
        CancellationToken ct)
        => throw new NotSupportedException("This test must not reach the provider.");
}
