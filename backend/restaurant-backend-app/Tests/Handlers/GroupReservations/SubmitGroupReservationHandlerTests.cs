using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using APIHandler.Exceptions;
using APIHandler.Handlers.GroupReservations;
using APIHandler.Models.GroupReservation;
using APIHandler.Services.GroupReservations;
using Moq;
using System.Text.Json;

namespace APIHandler.Tests.Handlers.GroupReservations;

public class SubmitGroupReservationHandlerTests
{
    private const string UserId = "owner-user-id";
    private const string GroupReservationId = "group-reservation-id";
    private const string ReservationId = "reservation-id";

    private readonly Mock<IGroupReservationService> _groupReservationService = new();
    private readonly Mock<ILambdaContext> _context = new();
    private readonly Mock<ILambdaLogger> _logger = new();

    public SubmitGroupReservationHandlerTests()
    {
        _context.SetupGet(context => context.Logger).Returns(_logger.Object);
    }

    [Fact]
    public async Task HandleRequest_ValidRequestReturnsSubmittedDishesAndCallsServiceWithClaimAndPathIds()
    {
        var result = new List<SubmittedDishQuantity>
        {
            new("dish-1", 3),
            new("dish-2", 1)
        };
        _groupReservationService
            .Setup(service => service.SubmitGroupReservationAsync(UserId, GroupReservationId, ReservationId))
            .ReturnsAsync(result);

        var response = await CreateHandler().HandleRequest(CreateRequest(), _context.Object);

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        JsonElement dishes = body.RootElement;
        Assert.Equal(JsonValueKind.Array, dishes.ValueKind);
        Assert.Equal(2, dishes.GetArrayLength());
        Assert.Equal("dish-1", dishes[0].GetProperty("dishId").GetString());
        Assert.Equal(3, dishes[0].GetProperty("quantity").GetInt32());
        Assert.Equal("dish-2", dishes[1].GetProperty("dishId").GetString());
        Assert.Equal(1, dishes[1].GetProperty("quantity").GetInt32());
        Assert.Equal("application/json", response.Headers["Content-Type"]);
        _groupReservationService.Verify(
            service => service.SubmitGroupReservationAsync(UserId, GroupReservationId, ReservationId),
            Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandleRequest_MissingOrBlankGroupReservationPathReturnsBadRequest(string? groupReservationId)
    {
        var response = await CreateHandler().HandleRequest(
            CreateRequest(groupReservationId: groupReservationId), _context.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_MissingPathParametersReturnsBadRequest()
    {
        var response = await CreateHandler().HandleRequest(
            CreateRequest(includePath: false), _context.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    public async Task HandleRequest_MissingOrBlankSubClaimReturnsUnauthorized(
        bool includeAuthorizer,
        string? userId)
    {
        var response = await CreateHandler().HandleRequest(
            CreateRequest(includeAuthorizer: includeAuthorizer, userId: userId), _context.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_NullClaimsReturnsUnauthorized()
    {
        var response = await CreateHandler().HandleRequest(
            CreateRequest(includeClaims: false), _context.Object);

        Assert.Equal(401, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task HandleRequest_MissingOrBlankBodyReservationIdReturnsBadRequest(string? reservationId)
    {
        string body = JsonSerializer.Serialize(new { reservationId });
        var response = await CreateHandler().HandleRequest(
            CreateRequest(body: body), _context.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_BodyWithoutReservationIdReturnsBadRequest()
    {
        var response = await CreateHandler().HandleRequest(
            CreateRequest(body: "{}"), _context.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public async Task HandleRequest_MissingOrInvalidJsonBodyReturnsBadRequest(string? body)
    {
        var response = await CreateHandler().HandleRequest(
            CreateRequest(body: body), _context.Object);

        Assert.Equal(400, response.StatusCode);
        VerifyServiceNotCalled();
    }

    [Fact]
    public async Task HandleRequest_GroupReservationNotFoundReturnsNotFound()
    {
        await AssertMappedExceptionAsync(new GroupReservationNotFoundException(), 404);
    }

    [Fact]
    public async Task HandleRequest_ReservationNotFoundReturnsNotFound()
    {
        await AssertMappedExceptionAsync(new ReservationNotFoundException("Parent reservation not found."), 404);
    }

    [Fact]
    public async Task HandleRequest_ForbiddenReturnsForbidden()
    {
        await AssertMappedExceptionAsync(new ActionForbiddenException("Not the reservation owner."), 403);
    }

    [Fact]
    public async Task HandleRequest_ValidationFailureReturnsBadRequestWithUnknownErrorCode()
    {
        var response = await GetExceptionResponseAsync(new ValidationException("There are no submitted guest sessions."));

        Assert.Equal(400, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal("Unknown", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("There are no submitted guest sessions.", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task HandleRequest_AlreadySubmittedReturnsBadRequestWithSubmittedErrorCode()
    {
        var response = await GetExceptionResponseAsync(new GroupReservationSubmittedException());

        Assert.Equal(400, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal("GroupReservationSubmitted", body.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task HandleRequest_ClosedGroupReturnsBadRequestWithExpiredErrorCode()
    {
        var response = await GetExceptionResponseAsync(new GroupReservationExpiredException());

        Assert.Equal(400, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.Equal("GroupReservationExpired", body.RootElement.GetProperty("errorCode").GetString());
    }

    private async Task AssertMappedExceptionAsync(Exception exception, int expectedStatusCode)
    {
        var response = await GetExceptionResponseAsync(exception);

        Assert.Equal(expectedStatusCode, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    private async Task<APIGatewayProxyResponse> GetExceptionResponseAsync(Exception exception)
    {
        _groupReservationService
            .Setup(service => service.SubmitGroupReservationAsync(UserId, GroupReservationId, ReservationId))
            .ThrowsAsync(exception);

        return await CreateHandler().HandleRequest(CreateRequest(), _context.Object);
    }

    private SubmitGroupReservationHandler CreateHandler() =>
        new(_groupReservationService.Object);

    private void VerifyServiceNotCalled() =>
        _groupReservationService.Verify(
            service => service.SubmitGroupReservationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);

    private static APIGatewayProxyRequest CreateRequest(
        bool includePath = true,
        string? groupReservationId = GroupReservationId,
        string? userId = UserId,
        bool includeAuthorizer = true,
        bool includeClaims = true,
        string? body = """{"reservationId":"reservation-id"}""")
    {
        var claims = new Dictionary<string, string>();
        if (userId is not null)
            claims["sub"] = userId;

        return new APIGatewayProxyRequest
        {
            PathParameters = includePath
                ? new Dictionary<string, string> { ["groupReservationId"] = groupReservationId! }
                : null,
            Body = body,
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
