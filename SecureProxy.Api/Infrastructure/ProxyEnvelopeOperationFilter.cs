using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using SecureProxy.Api.Proxy;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace SecureProxy.Api.Infrastructure;

/// <summary>
/// Documents the request body of the proxy endpoint in Swagger. The endpoint does not declare
/// <c>Accepts</c> metadata, because routing would then reject a wrong content type with a bare 415
/// before the proxy could return its own problem response and audit record.
/// </summary>
public sealed class ProxyEnvelopeOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.ActionDescriptor.EndpointMetadata.OfType<IEndpointNameMetadata>()
                .All(m => m.EndpointName != "ExecuteProxyOperation"))
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(ProxyEnvelopeDocument), context.SchemaRepository),
                    Example = new OpenApiObject
                    {
                        ["clientId"] = new OpenApiString("customer.get"),
                        ["json"] = new OpenApiObject
                        {
                            ["urlId"] = new OpenApiInteger(42),
                            ["includeOrders"] = new OpenApiBoolean(true),
                        },
                    },
                },
            },
        };
    }
}
