using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Helpers;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using System.Threading.Tasks;

namespace APIHandler.Handlers.Reservations;

public class GetOwnerGuestSessionIdHandler(IGuestSessionService guestSessionService) : IApiRequestHandler
{
    public async Task<APIGatewayProxyResponse> HandleRequest(APIGatewayProxyRequest request, ILambdaContext context)
    {
        var claims = request.RequestContext?.Authorizer?.Claims;
        if (claims is null ||
            !claims.TryGetValue("sub", out string? userId) ||
            string.IsNullOrWhiteSpace(userId))
        {
            return ResponseBuilder.CreateErrorResponse(401, "Unauthorized: User ID is missing.", context.Logger);
        }

        if (!request.TryGetPathValue("reservationId", out string? reservationId))
            return ResponseBuilder.CreateErrorResponse(400, "Missing reservationId in path parameters.", context.Logger);

        try
        {
            string guestSessionId = await guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId);
            return ResponseBuilder.CreateJsonResponse(200, new GetOwnerSessionIdResponse(guestSessionId));
        }
        catch (ReservationNotFoundException)
        {
            return ResponseBuilder.CreateErrorResponse(404, "Reservation was not found.", context.Logger);
        }
        catch (GroupReservationNotFoundException)
        {
            return ResponseBuilder.CreateErrorResponse(404, "Related group reservation was not found.", context.Logger);
        }
        catch (GuestSessionNotFoundException)
        {
            return ResponseBuilder.CreateErrorResponse(404, "Owner guest session was not found.", context.Logger);
        }
        catch (ActionForbiddenException)
        {
            return ResponseBuilder.CreateErrorResponse(403, "Forbidden: You do not own this reservation.", context.Logger);
        }
        catch (DuplicateOwnerGuestSessionsException)
        {
            return ResponseBuilder.CreateErrorResponse(409, "Multiple owner guest sessions were found for this reservation.", context.Logger);
        }
        catch (System.Exception)
        {
            return ResponseBuilder.CreateErrorResponse(500, "Internal server error.", context.Logger);
        }
    }
}
