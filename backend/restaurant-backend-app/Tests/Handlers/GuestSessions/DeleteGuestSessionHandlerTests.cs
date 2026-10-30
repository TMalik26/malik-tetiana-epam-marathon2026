using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GuestSessions;
using APIHandler.Models.GuestSessions;
using APIHandler.Services.GuestSessions;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GuestSessions;

public class DeleteGuestSessionHandlerTests
{
    private const string UserId = "owner-user-id";
    private const string GroupReservationId = "group-id";
    private const string GuestSessionId = "guest-session-id";

    private readonly Mock<IGuestSessionService> _guestSessionService = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public DeleteGuestSessionHandlerTests()
    {
        _context.SetupGet(context => context.Logger).Returns(_logger.Object);
    }

    [Fact]
    public async Task HandleRequest_SuccessReturnsGetGroupSessionsResponseShape()
    {
        var snapshot = new GetGroupSessionsResponse(
        [
            new GroupSessionParticipant(
                "owner-session-id",
                ParticipantType.Owner,
                "Owner Name",
                [new GroupSessionItem(new GroupSessionDish("dish-1", "Soup", 4.25m), 2)],
                8.5m,
                true),
            new GroupSessionParticipant(
                "remaining-session-id",
                ParticipantType.Guest,
                "Remaining Guest",
                [],
                0m,
                false)
        ],
        8.5m);
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupReservationId, GuestSessionId))
            .ReturnsAsync(snapshot);

        var response = await new DeleteGuestSessionHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal(8.5m, body.RootElement.GetProperty("total").GetDecimal());
        var participants = body.RootElement.GetProperty("participants").EnumerateArray().ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal("owner-session-id", participants[0].GetProperty("id").GetString());
        Assert.Equal("Owner", participants[0].GetProperty("type").GetString());
        Assert.Equal("Owner Name", participants[0].GetProperty("name").GetString());
        Assert.Equal(8.5m, participants[0].GetProperty("total").GetDecimal());
        Assert.True(participants[0].GetProperty("isSubmitted").GetBoolean());
        var item = Assert.Single(participants[0].GetProperty("items").EnumerateArray());
        Assert.Equal("dish-1", item.GetProperty("dish").GetProperty("id").GetString());
        Assert.Equal("Soup", item.GetProperty("dish").GetProperty("name").GetString());
        Assert.Equal(4.25m, item.GetProperty("dish").GetProperty("price").GetDecimal());
        Assert.Equal(2, item.GetProperty("quantity").GetInt32());
        Assert.Equal("remaining-session-id", participants[1].GetProperty("id").GetString());
        Assert.Equal("Guest", participants[1].GetProperty("type").GetString());
        Assert.Equal("Remaining Guest", participants[1].GetProperty("name").GetString());
        Assert.Empty(participants[1].GetProperty("items").EnumerateArray());
        Assert.False(participants[1].GetProperty("isSubmitted").GetBoolean());
        _guestSessionService.Verify(
            service => service.DeleteGuestSessionAsync(UserId, GroupReservationId, GuestSessionId), Times.Once);
    }

    [Fact]
    public async Task HandleRequest_ActionForbiddenReturnsForbidden()
    {
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(new ActionForbiddenException("Only the reservation owner can perform this action."));

        var response = await new DeleteGuestSessionHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(403, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_GroupMismatchReturnsBadRequest()
    {
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(new ValidationException("Guest session does not belong to this group reservation."));

        var response = await new DeleteGuestSessionHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task HandleRequest_MissingGuestSessionReturnsNotFound()
    {
        _guestSessionService
            .Setup(service => service.DeleteGuestSessionAsync(UserId, GroupReservationId, GuestSessionId))
            .ThrowsAsync(new GuestSessionNotFoundException("Guest session was not found."));

        var response = await new DeleteGuestSessionHandler(_guestSessionService.Object)
            .HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(404, response.StatusCode);
    }

    private static APIGatewayProxyRequest CreateRequest()
        => new()
        {
            PathParameters = new Dictionary<string, string>
            {
                ["groupReservationId"] = GroupReservationId,
                ["guestSessionId"] = GuestSessionId
            },
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    Claims = new Dictionary<string, string> { ["sub"] = UserId }
                }
            }
        };
}
