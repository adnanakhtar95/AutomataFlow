using System.Security.Cryptography;
using System.Text;
using DemoCrm.Configuration;
using Microsoft.Extensions.Options;

namespace DemoCrm.Security;

/// <summary>Validates the fictional demo credentials from configuration using constant-time comparison.</summary>
public sealed class DemoUserAuthenticator(IOptions<DemoCrmOptions> options)
{
    public DemoUser? Authenticate(string? email, string? password)
    {
        var suppliedEmail = Hash(email?.Trim().ToLowerInvariant() ?? string.Empty);
        var suppliedPassword = Hash(password ?? string.Empty);

        DemoUser? match = null;
        foreach (var user in options.Value.Users)
        {
            // Evaluate every user and both fields so timing does not reveal which part was wrong.
            var emailOk = CryptographicOperations.FixedTimeEquals(suppliedEmail, Hash(user.Email.Trim().ToLowerInvariant()));
            var passwordOk = CryptographicOperations.FixedTimeEquals(suppliedPassword, Hash(user.Password));
            if (emailOk && passwordOk)
            {
                match = user;
            }
        }

        return match;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
