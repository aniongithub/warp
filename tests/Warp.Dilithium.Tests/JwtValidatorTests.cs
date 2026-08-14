using Warp.Core.Middleware;
using Warp.Dilithium.Middleware;

namespace Warp.Dilithium.Tests;

/// <summary>
/// The JWT signature/lifetime decision matrix, including the post-#29 fail-closed behaviour: with
/// <c>AllowUnsignedTokensInsecure = false</c> (the secure default) a missing/invalid signature or a
/// missing signing key is rejected rather than trusted.
/// </summary>
public class JwtValidatorTests
{
    [Fact]
    public async Task ValidSignedToken_Continues()
    {
        var key = JwtTestHelpers.SymmetricKey();
        var options = new JwtValidatorOptions { SigningKey = key };
        var validator = JwtTestHelpers.NewValidator(options, out _);

        var result = await JwtTestHelpers.RunAsync(validator, JwtTestHelpers.ContextWithBearer(JwtTestHelpers.SignHs256(key)));

        result.Action.Should().Be(PipelineAction.Continue);
    }

    [Fact]
    public async Task UnsignedToken_IsRejected_WhenInsecureOptOutDisabled()
    {
        var key = JwtTestHelpers.SymmetricKey();
        var options = new JwtValidatorOptions { SigningKey = key, AllowUnsignedTokensInsecure = false };
        var validator = JwtTestHelpers.NewValidator(options, out _);
        var context = JwtTestHelpers.ContextWithBearer(JwtTestHelpers.Unsigned());

        var result = await JwtTestHelpers.RunAsync(validator, context);

        JwtTestHelpers.StatusCodeOf(result).Should().Be(401);
    }

    [Fact]
    public async Task ExpiredSignedToken_IsRejected()
    {
        var key = JwtTestHelpers.SymmetricKey();
        var options = new JwtValidatorOptions { SigningKey = key };
        var validator = JwtTestHelpers.NewValidator(options, out _);
        // Expired well beyond the validator's 2-minute clock skew.
        var expired = JwtTestHelpers.SignHs256(key, expires: DateTime.UtcNow.AddMinutes(-10));
        var context = JwtTestHelpers.ContextWithBearer(expired);

        var result = await JwtTestHelpers.RunAsync(validator, context);

        JwtTestHelpers.StatusCodeOf(result).Should().Be(401);
    }

    [Fact]
    public async Task WrongKey_IsRejected()
    {
        var signingKey = JwtTestHelpers.SymmetricKey("key-A-key-A-key-A-key-A-key-A-key-A-01");
        var validatorKey = JwtTestHelpers.SymmetricKey("key-B-key-B-key-B-key-B-key-B-key-B-02");
        var options = new JwtValidatorOptions { SigningKey = validatorKey };
        var validator = JwtTestHelpers.NewValidator(options, out _);
        var context = JwtTestHelpers.ContextWithBearer(JwtTestHelpers.SignHs256(signingKey));

        var result = await JwtTestHelpers.RunAsync(validator, context);

        JwtTestHelpers.StatusCodeOf(result).Should().Be(401);
    }

    [Fact]
    public async Task NoKeyMaterial_FailsClosed_With500()
    {
        // No symmetric key, no JWKS URI, and the insecure opt-out is off => the middleware cannot
        // verify signatures and must fail closed (post-#29) rather than trust the token.
        var options = new JwtValidatorOptions { SigningKey = null, JwksUri = null, AllowUnsignedTokensInsecure = false };
        var validator = JwtTestHelpers.NewValidator(options, out _);
        var context = JwtTestHelpers.ContextWithBearer(JwtTestHelpers.SignHs256(JwtTestHelpers.SymmetricKey()));

        var result = await JwtTestHelpers.RunAsync(validator, context);

        JwtTestHelpers.StatusCodeOf(result).Should().Be(500);
    }

    [Fact]
    public async Task InsecureOptOut_AcceptsUnsignedToken()
    {
        // Documents the explicit, dangerous dev-only escape hatch: with AllowUnsignedTokensInsecure
        // = true an unsigned token is accepted. This proves the gate ONLY opens on the explicit flag.
        var options = new JwtValidatorOptions { AllowUnsignedTokensInsecure = true };
        var validator = JwtTestHelpers.NewValidator(options, out _);

        var result = await JwtTestHelpers.RunAsync(validator, JwtTestHelpers.ContextWithBearer(JwtTestHelpers.Unsigned()));

        result.Action.Should().Be(PipelineAction.Continue);
    }
}
