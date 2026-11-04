using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace TodoSaaS.Api.Services;

public interface ITokenService
{
    string GenerateToken(Guid tenantId, string email, string role = "Admin");
}

public class JwtTokenService : ITokenService
{
    private readonly IConfiguration _config;

    public JwtTokenService(IConfiguration config)
    {
        _config = config;
    }

    public string GenerateToken(Guid tenantId, string email, string role = "Admin")
    {
        var secret = _config["Auth:JwtSecret"] ?? "SuperSecretTodoSaaSKeyForLocalDevelopmentAndTestingPurposesOnly1234567890";
        var issuer = _config["Auth:Issuer"] ?? "TodoSaaS";
        var audience = _config["Auth:Audience"] ?? "TodoSaaS.Clients";

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, email),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("org_id", tenantId.ToString()),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
