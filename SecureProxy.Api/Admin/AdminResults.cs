namespace SecureProxy.Api.Admin;

/// <summary>Problem responses of the admin API. Admins are trusted, so validation errors are detailed.</summary>
public static class AdminResults
{
    public static IResult Unauthorized() =>
        Problem(StatusCodes.Status401Unauthorized, "unauthorized", "A valid admin key is required.");

    public static IResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, "not_found", "The configuration item does not exist.");

    public static IResult ValidationFailed(IEnumerable<string> errors) =>
        Problem(StatusCodes.Status400BadRequest, "validation_failed", "The configuration is invalid.", errors.ToArray());

    public static IResult PreconditionRequired() =>
        Problem(StatusCodes.Status428PreconditionRequired, "precondition_required",
            "Send the version you read, as returned in the ETag header, in If-Match.");

    public static IResult InvalidPrecondition() =>
        Problem(StatusCodes.Status400BadRequest, "invalid_precondition", "If-Match is not a valid version.");

    public static IResult Conflict() =>
        Problem(StatusCodes.Status409Conflict, "configuration_conflict",
            "The configuration was changed after you read it. Read it again and reapply your change.");

    public static IResult Duplicate(string what) =>
        Problem(StatusCodes.Status409Conflict, "duplicate", $"A {what} with the same unique value already exists.");

    private static IResult Problem(int status, string code, string title, string[]? errors = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (errors is not null)
        {
            extensions["errors"] = errors;
        }

        return Results.Problem(statusCode: status, title: title, type: "https://secureproxy.local/errors/" + code, extensions: extensions);
    }
}
