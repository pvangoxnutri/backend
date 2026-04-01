using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace sidequest.backend.Services;

public sealed class GoogleTokenVerifier
{
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> ConfigurationManager = new(
        "https://accounts.google.com/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever());

    public async Task<GoogleTokenPayload?> VerifyAsync(string idToken, string clientId, CancellationToken cancellationToken)
    {
        var configuration = await ConfigurationManager.GetConfigurationAsync(cancellationToken);
        var handler = new JwtSecurityTokenHandler();

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidateAudience = true,
            ValidAudience = clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        try
        {
            var principal = handler.ValidateToken(idToken, validationParameters, out _);
            var emailVerified = string.Equals(
                principal.FindFirst("email_verified")?.Value,
                "true",
                StringComparison.OrdinalIgnoreCase);

            if (!emailVerified)
            {
                return null;
            }

            var subject = principal.FindFirst("sub")?.Value;
            var email = principal.FindFirst("email")?.Value;

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            return new GoogleTokenPayload
            {
                Subject = subject,
                Email = email,
                Name = principal.FindFirst("name")?.Value ?? email,
                Picture = principal.FindFirst("picture")?.Value
            };
        }
        catch
        {
            return null;
        }
    }
}

public sealed class GoogleTokenPayload
{
    public string Subject { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Picture { get; init; }
}
