// Local helper: prints a ready-to-send request envelope with AES-256-GCM encrypted parameters,
// using the same key the proxy reads (EncryptionKeys:<reference> from the API's user secrets or
// an EncryptionKeys__<reference> environment variable).
//
//   dotnet run --project SecureProxy.EncryptTool -- customer.update
//   dotnet run --project SecureProxy.EncryptTool -- customer.update @parameters.json
//   dotnet run --project SecureProxy.EncryptTool -- customer.update @parameters.json --key-ref customer-v1

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SecureProxy.Api.Security;

const string ApiUserSecretsId = "23c0a029-fb37-4b4f-a8e0-3c9420cb26b4";
const string DefaultParameters = """{"urlId":1,"email":"dana@example.com","phone":"+972 50-123-4567"}""";

if (args.Length < 1 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: SecureProxy.EncryptTool <operationKey> [@parameters.json | <json>] [--key-ref <reference>]");
    return 1;
}

var operationKey = args[0];
var keyReference = "customer-v1";
var parameters = DefaultParameters;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--key-ref" && i + 1 < args.Length)
    {
        keyReference = args[++i];
    }
    else if (args[i].StartsWith('@'))
    {
        parameters = File.ReadAllText(args[i][1..]);
    }
    else
    {
        parameters = args[i];
    }
}

using (var check = JsonDocument.Parse(parameters))
{
    if (check.RootElement.ValueKind != JsonValueKind.Object)
    {
        Console.Error.WriteLine("Parameters must be a JSON object.");
        return 1;
    }
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(ApiUserSecretsId)
    .AddEnvironmentVariables()
    .Build();

var keySource = new ConfigurationKeySource(configuration);
var key = await keySource.GetKeyAsync(keyReference, CancellationToken.None);
if (key is not { Length: AesGcmPayload.KeySize })
{
    Console.Error.WriteLine($"No 256-bit key '{ConfigurationKeySource.SectionName}:{keyReference}' found in user secrets or environment variables.");
    return 1;
}

var (nonce, ciphertext, tag) = AesGcmPayload.Encrypt(key, Encoding.UTF8.GetBytes(parameters), AesGcmPayload.AssociatedData(operationKey));

Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        clientId = operationKey,
        encryptedJson = new
        {
            nonce = Convert.ToBase64String(nonce),
            ciphertext = Convert.ToBase64String(ciphertext),
            tag = Convert.ToBase64String(tag),
        },
    },
    new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
return 0;
