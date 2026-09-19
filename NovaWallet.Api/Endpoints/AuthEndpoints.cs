using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NovaWallet.Domain;

namespace NovaWallet.Api;

// DEV-ONLY mock JWT issuer. Not a real identity provider: this exists so protected endpoints
// can be exercised without standing up a full OAuth2/OIDC stack.
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/token", (
                [FromBody] TokenRequest request,
                IOptions<JwtOptions> jwtOptions) =>
            {
                if (string.IsNullOrWhiteSpace(request.CustomerId))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["customerId"] = ["CustomerId is required."]
                    });
                }

                // This mock issuer accepts any customerId with no real identity check —
                // explicitly excluding the one reserved value closes the specific path
                // where that let a caller mint a token that owns the settlement account.
                if (request.CustomerId == WellKnownWalletIds.SystemSettlementCustomerId)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["customerId"] = ["This customerId is reserved and cannot be used."]
                    });
                }

                var options = jwtOptions.Value;
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
                var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

                var claims = new[] { new Claim("customer_id", request.CustomerId) };

                var token = new JwtSecurityToken(
                    issuer: options.Issuer,
                    audience: options.Audience,
                    claims: claims,
                    expires: DateTime.UtcNow.AddMinutes(options.ExpiryMinutes),
                    signingCredentials: credentials);

                var jwt = new JwtSecurityTokenHandler().WriteToken(token);
                return Results.Ok(new TokenResponse(jwt, options.ExpiryMinutes * 60));
            })
            .WithName("IssueDevToken")
            .WithSummary("DEV-ONLY mock token issuer — mints a JWT for a given customerId. Not a production identity provider.")
            .WithTags("Auth")
            .WithOpenApi();
    }
}

public record TokenRequest(string CustomerId);
public record TokenResponse(string AccessToken, int ExpiresInSeconds);
