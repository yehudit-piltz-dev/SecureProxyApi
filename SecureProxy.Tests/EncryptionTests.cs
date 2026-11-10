using System.Net;
using System.Security.Cryptography;
using System.Text;
using SecureProxy.Api.Security;
using SecureProxy.Tests.Infrastructure;

namespace SecureProxy.Tests;

public sealed class EncryptionTests(ProxyTestFixture fixture) : ProxyTestBase(fixture)
{
    private static readonly object ValidParameters = new { urlId = 9, email = "dana@example.com" };

    [Fact]
    public async Task Valid_encrypted_payload_is_decrypted_validated_and_forwarded()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, ValidParameters));

        Assert.Equal(HttpStatusCode.OK, result.Response.StatusCode);
        var forwarded = AssertForwardedOnce();
        Assert.Equal(HttpMethod.Put, forwarded.Method);
        Assert.Equal("https://upstream.test/api/customers/9", forwarded.Uri.AbsoluteUri);
        Assert.Equal("""{"email":"dana@example.com"}""", forwarded.Body);
    }

    // Brief 12.11: invalid AES-GCM authentication tags are rejected.
    [Fact]
    public async Task Invalid_authentication_tag_is_rejected()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();
        var (nonce, ciphertext, tag) = Encrypt(operation.Key, ValidParameters);
        tag[0] ^= 0x01;

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, nonce, ciphertext, tag));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
        AssertNotForwarded();
    }

    // Brief 12.12: invalid encrypted payloads are never forwarded.
    [Fact]
    public async Task Tampered_ciphertext_is_rejected()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();
        var (nonce, ciphertext, tag) = Encrypt(operation.Key, ValidParameters);
        ciphertext[^1] ^= 0x01;

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, nonce, ciphertext, tag));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Payload_encrypted_with_another_key_is_rejected()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, ValidParameters, key: RandomNumberGenerator.GetBytes(32)));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Payload_encrypted_for_another_operation_cannot_be_replayed()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();

        // Same key, but the associated data binds the ciphertext to a different operation key.
        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, ValidParameters, associatedKey: "customer.other"));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Authentic_payload_that_is_not_a_json_object_is_rejected()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();
        var plaintext = Encoding.UTF8.GetBytes("[1,2,3]");
        var (nonce, ciphertext, tag) = AesGcmPayload.Encrypt(ProxyTestFixture.EncryptionKey, plaintext, AesGcmPayload.AssociatedData(operation.Key));

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, nonce, ciphertext, tag));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Authentic_payload_with_invalid_parameters_is_rejected()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, new { urlId = 9, email = "not-an-email" }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_parameter");
        AssertNotForwarded();
    }

    [Theory]
    [InlineData("""{"nonce":"AAAA","ciphertext":"AAAA","tag":"AAAAAAAAAAAAAAAAAAAAAA=="}""")]               // nonce too short
    [InlineData("""{"nonce":"AAAAAAAAAAAAAAAA","ciphertext":"AAAA","tag":"AAAA"}""")]                       // tag too short
    [InlineData("""{"nonce":"not base64!","ciphertext":"AAAA","tag":"AAAAAAAAAAAAAAAAAAAAAA=="}""")]
    [InlineData("""{"nonce":"AAAAAAAAAAAAAAAA","tag":"AAAAAAAAAAAAAAAAAAAAAA=="}""")]                       // no ciphertext
    [InlineData("""{"nonce":"AAAAAAAAAAAAAAAA","ciphertext":"AAAA","tag":"AAAAAAAAAAAAAAAAAAAAAA==","key":"x"}""")]
    [InlineData("""{"nonce":"AAAAAAAAAAAAAAAA","nonce":"AAAAAAAAAAAAAAAA","ciphertext":"AAAA","tag":"AAAAAAAAAAAAAAAAAAAAAA=="}""")]
    [InlineData("\"just a string\"")]
    public async Task Malformed_encrypted_envelope_is_rejected(string encryptedJson)
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();

        var result = await ExecuteRawAsync($$"""{"clientId":"{{operation.Key}}","encryptedJson":{{encryptedJson}}}""");

        AssertProxyError(result, HttpStatusCode.BadRequest, "invalid_encrypted_payload");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Plain_parameters_are_rejected_when_encryption_is_required()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync();

        var result = await ExecuteAsync(Envelope(operation.Key, ValidParameters));

        AssertProxyError(result, HttpStatusCode.BadRequest, "encryption_required");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Encrypted_parameters_are_rejected_when_the_operation_does_not_use_encryption()
    {
        var operation = await Fixture.CreateOperationAsync();

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, new { urlId = 1 }));

        AssertProxyError(result, HttpStatusCode.BadRequest, "encryption_not_allowed");
        AssertNotForwarded();
    }

    [Fact]
    public async Task Missing_key_fails_closed_without_revealing_details()
    {
        var operation = await Fixture.CreateEncryptedOperationAsync(keyReference: "key-that-does-not-exist");

        var result = await ExecuteAsync(EncryptedEnvelope(operation.Key, ValidParameters));

        AssertProxyError(result, HttpStatusCode.InternalServerError, "encryption_unavailable");
        Assert.DoesNotContain("key-that-does-not-exist", result.Body);
        AssertNotForwarded();
    }

    private static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) Encrypt(string operationKey, object parameters) =>
        AesGcmPayload.Encrypt(
            ProxyTestFixture.EncryptionKey,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(parameters),
            AesGcmPayload.AssociatedData(operationKey));
}
