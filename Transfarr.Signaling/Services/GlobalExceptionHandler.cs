using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using System.Net;

namespace Transfarr.Signaling.Services;

/// <summary>
/// Global handler for mapping exceptions to standardized Problem Details (RFC 7807) responses.
/// </summary>
public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
	public async ValueTask<bool> TryHandleAsync(
		HttpContext httpContext,
		Exception exception,
		CancellationToken cancellationToken)
	{
		logger.LogError(exception, "An unhandled exception occurred: {Message}", exception.Message);

		var (statusCode, title) = exception switch
		{
			UnauthorizedAccessException => (HttpStatusCode.Unauthorized, "Unauthorized Access"),
			ArgumentException => (HttpStatusCode.BadRequest, "Bad Request"),
			InvalidOperationException => (HttpStatusCode.Conflict, "Invalid Operation"),
			KeyNotFoundException => (HttpStatusCode.NotFound, "Resource Not Found"),
			_ => (HttpStatusCode.InternalServerError, "Internal Server Error")
		};

		var problemDetails = new ProblemDetails
		{
			Status = (int)statusCode,
			Title = title,
			Detail = exception.Message,
			Instance = httpContext.Request.Path
		};

		httpContext.Response.StatusCode = problemDetails.Status.Value;

		await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

		return true;
	}
}
