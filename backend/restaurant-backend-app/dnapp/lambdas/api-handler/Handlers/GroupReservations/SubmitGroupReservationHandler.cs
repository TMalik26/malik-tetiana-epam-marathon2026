using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GroupReservations;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace APIHandler.Handlers.GroupReservations;

public class SubmitGroupReservationHandler(IGroupReservationService groupReservationService) : IApiRequestHandler
{
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!request.TryGetPathValue("groupReservationId", out string? groupReservationId) ||
            string.IsNullOrWhiteSpace(groupReservationId))
        {
            return ResponseBuilder.CreateErrorResponse(400, "A nonempty groupReservationId is required.", context.Logger);
        }

        if (request.RequestContext?.Authorizer?.Claims is null ||
            !request.RequestContext.Authorizer.Claims.TryGetValue("sub", out string? userId) ||
            string.IsNullOrWhiteSpace(userId))
        {
            return ResponseBuilder.CreateErrorResponse(401, "Unauthorized.", context.Logger);
        }

        SubmitGroupReservationRequest? body;
        try
        {
            body = string.IsNullOrWhiteSpace(request.Body)
                ? null
                : JsonSerializer.Deserialize<SubmitGroupReservationRequest>(request.Body, RequestJsonOptions);
        }
        catch (JsonException)
        {
            return ResponseBuilder.CreateErrorResponse(400, "Request body must be valid JSON with a reservationId.", context.Logger);
        }

        if (string.IsNullOrWhiteSpace(body?.ReservationId))
        {
            return ResponseBuilder.CreateErrorResponse(400, "A nonempty reservationId is required.", context.Logger);
        }

        try
        {
            var result = await groupReservationService.SubmitGroupReservationAsync(
                userId,
                groupReservationId,
                body.ReservationId);
            return ResponseBuilder.CreateJsonResponse(200, result);
        }
        catch (GroupReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ReservationNotFoundException exception)
        {
            return ResponseBuilder.CreateErrorResponse(404, exception.Message, context.Logger);
        }
        catch (ActionForbiddenException exception)
        {
            return ResponseBuilder.CreateErrorResponse(403, exception.Message, context.Logger);
        }
        catch (GroupReservationSubmittedException exception)
        {
            return ResponseBuilder.CreateErrorCodeResponse(exception);
        }
        catch (ValidationException exception)
        {
            return ResponseBuilder.CreateErrorCodeResponse(exception);
        }
        catch (Exception)
        {
            return ResponseBuilder.CreateErrorResponse(500, "Internal server error.", context.Logger);
        }
    }
}
