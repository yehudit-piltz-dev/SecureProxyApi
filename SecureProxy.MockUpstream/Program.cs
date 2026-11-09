// Local stand-in for the "customers-api" destination, for manual runs only.
// It echoes what it received so the effect of the proxy's policies is visible,
// and has a few routes that simulate upstream failures.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapMethods("/customers/{**rest}", ["GET", "PUT"], async (HttpRequest request, string? rest) =>
{
    switch (rest)
    {
        case "500":
            return Results.Problem("Simulated upstream failure with internal details: db01.internal", statusCode: 500);
        case "404":
            return Results.NotFound(new { error = "customer not found" });
        case "999":
            // Longer than the destination timeout configured by the seed data (5 seconds).
            await Task.Delay(TimeSpan.FromSeconds(10), request.HttpContext.RequestAborted);
            return Results.Ok();
    }

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    return Results.Ok(new
    {
        receivedBy = "mock-upstream",
        method = request.Method,
        path = request.Path.Value,
        query = request.QueryString.Value,
        headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
        body,
    });
});

app.MapGet("/customers", (HttpRequest request) => Results.Ok(new
{
    receivedBy = "mock-upstream",
    path = request.Path.Value,
    query = request.QueryString.Value,
    results = new[] { new { id = 1, name = "Dana" }, new { id = 2, name = "Dan" } },
}));

app.Run();
