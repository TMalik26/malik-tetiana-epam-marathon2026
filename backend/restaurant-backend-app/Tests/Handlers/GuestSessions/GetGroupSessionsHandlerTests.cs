using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GuestSessions;

public class GetGroupSessionsHandlerTests
{
    private const string UserId = "owner-user-id";
    private const string GroupReservationId = "group-id";

    private readonly Mock<IGuestSessionService> _guestSessionService = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public GetGroupSessionsHandlerTests()
    {
        _context.SetupGet(context => context.Logger).Returns(_logger.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidRequestReturnsSuccessAndCallsServiceWithPathAndUserIds()
    {
        var result = new GetGroupSessionsResponse([], 0m);
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(UserId, GroupReservationId))
            .ReturnsAsync(result);
        var handler = new GetGroupSessionsHandler(_guestSessionService.Object);

        var response = await handler.HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(0m, body.RootElement.GetProperty("total").GetDecimal());
        Assert.Empty(body.RootElement.GetProperty("participants").EnumerateArray());
        _guestSessionService.Verify(
            service => service.GetGroupSessionsAsync(UserId, GroupReservationId), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_MissingGroupReservationPathReturnsBadRequest()
    {
        var handler = new GetGroupSessionsHandler(_guestSessionService.Object);

        var response = await handler.HandleRequest(CreateRequest(includePath: false), _context.Object);

        Assert.Equal(400, response.StatusCode);
        _guestSessionService.Verify(
            service => service.GetGroupSessionsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandleRequest_MissingOrBlankSubClaimReturnsUnauthorized(string? userId)
    {
        var handler = new GetGroupSessionsHandler(_guestSessionService.Object);

        var response = await handler.HandleRequest(CreateRequest(userId: userId), _context.Object);

        Assert.Equal(401, response.StatusCode);
        _guestSessionService.Verify(
            service => service.GetGroupSessionsAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task HandleRequest_DishNotFoundReturnsConflict()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(UserId, GroupReservationId))
            .ThrowsAsync(new DishNotFoundException("Dish is missing."));

        var response = await new GetGroupSessionsHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(409, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ActionForbiddenReturnsForbidden()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(UserId, GroupReservationId))
            .ThrowsAsync(new ActionForbiddenException("Not the reservation owner."));

        var response = await new GetGroupSessionsHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFoundReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(UserId, GroupReservationId))
            .ThrowsAsync(new GroupReservationNotFoundException());

        var response = await new GetGroupSessionsHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_ParentReservationNotFoundReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.GetGroupSessionsAsync(UserId, GroupReservationId))
            .ThrowsAsync(new ReservationNotFoundException("Parent reservation not found."));

        var response = await new GetGroupSessionsHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(404, response.StatusCode);
    }

    private static APIGatewayProxyRequest CreateRequest(bool includePath = true, string? userId = UserId)
    {
        var claims = new Dictionary<string, string>();
        if (userId is not null)
            claims["sub"] = userId;

        return new APIGatewayProxyRequest
        {
            PathParameters = includePath
                ? new Dictionary<string, string> { ["groupReservationId"] = GroupReservationId }
                : new Dictionary<string, string>(),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { Claims = claims }
            }
        };
    }
}
