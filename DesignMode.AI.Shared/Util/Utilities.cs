using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace DesignMode.AI.Shared.Util
{
    public static class Utilities
    {
        public static string HashPassword(string password, string salt)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(password + salt);
            return Convert.ToBase64String(sha256.ComputeHash(bytes));
        }

        public static string GenerateJwt(string username, string? userId = null)
        {
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("JwtSecretKey")));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, username),
            };
            if (!string.IsNullOrWhiteSpace(userId))
                claims.Add(new Claim(ClaimTypes.NameIdentifier, userId));

            var expirationTime = DateTime.UtcNow.AddDays(30); // Token expires after 5 minutes

            var token = new JwtSecurityToken(
                issuer: "DesignModeAI",
                audience: "DesignModeFunctions",
                claims: claims,
                expires: expirationTime,
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public static string XorString(string input, string key = "dGhpcyBpcyBhIHNlY3VyZSBpbnRlbnNpdHkgc3RyaW5nIGZyb20gaGVsbG8=")
        {
            var output = new StringBuilder();

            // XOR operation on input
            for (int i = 0; i < input.Length; i++)
            {
                output.Append((char)(input[i] ^ key[i % key.Length]));
            }

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(output.ToString()));
        }

        public static string DecryptXorString(string input, string key = "dGhpcyBpcyBhIHNlY3VyZSBpbnRlbnNpdHkgc3RyaW5nIGZyb20gaGVsbG8=")
        {
            // First decode the base64 string to get the XORed result
            var decodedBytes = Convert.FromBase64String(input);
            var decodedString = Encoding.UTF8.GetString(decodedBytes);

            var output = new StringBuilder();

            // XOR operation to decrypt
            for (int i = 0; i < decodedString.Length; i++)
            {
                output.Append((char)(decodedString[i] ^ key[i % key.Length]));
            }

            return output.ToString();
        }
    }
}
