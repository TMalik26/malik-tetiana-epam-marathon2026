using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.Reservations;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.Reservations;

public class GetOwnerGuestSessionIdHandlerTests
{
    private const string UserId = "owner-user-id";
    private const string ReservationId = "reservation-id";
    private const string GuestSessionId = "owner-session-id";

    private readonly Mock<IGuestSessionService> _guestSessionService = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public GetOwnerGuestSessionIdHandlerTests()
    {
        _context.SetupGet(context => context.Logger).Returns(_logger.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidRequestReturnsGuestSessionIdAndCallsServiceWithClaimAndPathIds()
    {
        _guestSessionService
            .Setup(service => service.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ReturnsAsync(GuestSessionId);
        var handler = new GetOwnerGuestSessionIdHandler(_guestSessionService.Object);

        var response = await handler.HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(GuestSessionId, body.RootElement.GetProperty("guestSessionId").GetString());
        _guestSessionService.Verify(
            service => service.GetOwnerGuestSessionIdAsync(UserId, ReservationId), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandleRequest_MissingOrBlankSubClaimReturnsUnauthorized(string? userId)
    {
        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(userId: userId), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _guestSessionService.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_MissingAuthorizerReturnsUnauthorized()
    {
        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(includeAuthorizer: false), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _guestSessionService.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_NullClaimsReturnsUnauthorized()
    {
        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(includeClaims: false), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _guestSessionService.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_MissingReservationPathReturnsBadRequestWithoutCallingService()
    {
        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(includePath: false), _context.Object);

        Assert.Equal(400, response.StatusCode);
        _guestSessionService.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_EmptyReservationPathReturnsBadRequestWithoutCallingService()
    {
        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(reservationId: ""), _context.Object);

        Assert.Equal(400, response.StatusCode);
        _guestSessionService.Verify(
            service => service.GetOwnerGuestSessionIdAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_ActionForbiddenReturnsForbidden()
    {
        _guestSessionService
            .Setup(service => service.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new ActionForbiddenException("Not the reservation owner."));

        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ReservationNotFoundReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new ReservationNotFoundException("Reservation was not found."));

        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFoundReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_OwnerGuestSessionNotFoundReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new GuestSessionNotFoundException("Owner guest session was not found."));

        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_DuplicateOwnerGuestSessionsReturnsConflict()
    {
        _guestSessionService
            .Setup(service => service.GetOwnerGuestSessionIdAsync(UserId, ReservationId))
            .ThrowsAsync(new DuplicateOwnerGuestSessionsException("Multiple owner sessions were found."));

        var response = await new GetOwnerGuestSessionIdHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(409, response.StatusCode);
    }

    private static APIGatewayProxyRequest CreateRequest(
        bool includePath = true,
        string? userId = UserId,
        bool includeAuthorizer = true,
        bool includeClaims = true,
        string? reservationId = ReservationId)
    {
        var claims = new Dictionary<string, string>();
        if (userId is not null)
            claims["sub"] = userId;

        return new APIGatewayProxyRequest
        {
            PathParameters = includePath
                ? new Dictionary<string, string> { ["reservationId"] = reservationId! }
                : new Dictionary<string, string>(),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = includeAuthorizer
                    ? new APIGatewayCustomAuthorizerContext
                    {
                        Claims = includeClaims ? claims : null
                    }
                    : null
            }
        };
    }
}
