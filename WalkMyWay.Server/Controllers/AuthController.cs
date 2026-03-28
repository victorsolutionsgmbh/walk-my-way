using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace WalkMyWay.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;

    public AuthController(IConfiguration config)
    {
        _config = config;
    }

    [HttpPost]
    public IActionResult GetToken([FromBody] AuthRequest request)
    {
        var expectedKey = _config["Auth:ApiKey"]
            ?? throw new InvalidOperationException("Auth:ApiKey is not configured.");

        if (request.ApiKey != expectedKey)
            return Unauthorized();

        var secret = _config["Auth:JwtSecret"]
            ?? throw new InvalidOperationException("Auth:JwtSecret is not configured.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "walkmyway.victor.solutions",
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: creds
        );

        return Ok(new { token = new JwtSecurityTokenHandler().WriteToken(token) });
    }
}

public record AuthRequest(string ApiKey);
