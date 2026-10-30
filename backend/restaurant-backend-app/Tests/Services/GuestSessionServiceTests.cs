using Amazon.CognitoIdentityProvider.Model;
using APIHandler.Constants.Dishes;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GuestSessions;
using APIHandler.Repositories.Auth;
using APIHandler.Repositories.Dishes;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.GuestSessions;
using AutoFixture.Xunit2;
using Moq;
using NPOI.POIFS.Storage;
using System.Security.Cryptography;
using System.Text;

namespace APIHandler.Tests.Services;

public class GuestSessionServiceTests
{
    private readonly Mock<IGuestSessionRepository> _mockGuestSessionRepository = new();
    private readonly Mock<IGroupReservationRepository> _mockGroupReservationRepository = new();
    private readonly Mock<IDishRepository> _mockDishRepository = new();
    private readonly Mock<IReservationRepository> _mockReservationRepository = new();
    private readonly Mock<IUserRepository> _mockUserRepository = new();
    private readonly Mock<ITableRepository> _mockTableRepository = new();
    private readonly GuestSessionService _guestSessionService;

    public GuestSessionServiceTests()
    {
        _guestSessionService = new GuestSessionService(
            _mockGroupReservationRepository.Object,
            _mockGuestSessionRepository.Object,
            _mockDishRepository.Object,
            _mockReservationRepository.Object,
            _mockUserRepository.Object,
            _mockTableRepository.Object);
    }
    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(3)]
    public async Task CreateGuestSessionAsync_CorrectData_ReturnsSessionToken(
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = 5;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync([]);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var response = await _guestSessionService.CreateGuestSessionAsync(request);

        Assert.NotEmpty(response.SessionId);
        Assert.NotEmpty(response.SessionToken);
        Assert.NotNull(session);
        Assert.Equal(groupReservation.GroupReservationId, session.GroupReservationId);
        Assert.Equal(request.GuestName, session.GuestName);
        Assert.NotNull(session.SessionTokenHash);
        Assert.NotEmpty(session.SessionTokenHash);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_InvalidGroupReservationId_ThrowsGroupReservationNotFoundException(CreateGuestSessionRequest request)
    {
        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_GroupReservationExpired_ThrowsGroupReservationExpiredException(CreateGuestSessionRequest request, GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(request.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldAddNewDish_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;
        int quantityToAdd = 2;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(dish.Id, guestSession.Dishes[0].DishId);
        Assert.Equal(quantityToAdd, guestSession.Dishes[0].Quantity);
        Assert.Equal(OrderItemState.Pending, guestSession.Dishes[0].State);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldIncreaseQuantity_WhenDishAlreadyExists(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        int initialQuantity = 3;
        int quantityToAdd = 2;
        guestSession.Dishes = [new OrderItem { DishId = dish.Id, Quantity = initialQuantity, State = OrderItemState.Pending }];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;
        dish.State = DishState.Available;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        await _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantityToAdd);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity + quantityToAdd, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [InlineAutoData(0)]
    [InlineAutoData(-1)]
    public async Task AddDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(int quantity, string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, quantity));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, Dish dish)
    {
        dish.State = DishState.Available;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.AddDishAsync(guestSessionId, dish.Id, 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotFound_WhenDishNotFound(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, string dishId)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);

        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([]);

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dishId, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task AddDishAsync_ShouldThrowDishNotAvailable_WhenDishIsNotAvailable(GuestSession guestSession, GroupReservation groupReservation, Reservation parentReservation, Table table, Dish dish)
    {
        dish.State = DishState.OutOfStock;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = parentReservation.Id;
        parentReservation.TableId = table.Id;

        _mockGuestSessionRepository.Setup(r => r.GetByGuestSessionId(guestSession.Id)).ReturnsAsync(guestSession);
        _mockGroupReservationRepository.Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId)).ReturnsAsync(groupReservation);
        _mockReservationRepository.Setup(r => r.GetReservationByIdAsync(parentReservation.Id)).ReturnsAsync(parentReservation);
        _mockTableRepository.Setup(r => r.GetTableAsync(table.Id)).ReturnsAsync(table);
        _mockDishRepository.Setup(r => r.GetDishesByLocationIdAsync(table.LocationId)).ReturnsAsync([dish]);

        var exception = await Assert.ThrowsAsync<DishIsNotAvailableException>(() =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task AddDishAsync_ShouldThrowGroupReservationExpired_WhenGroupStatusIsNotOpen(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation, Dish dish)
    {
        dish.State = DishState.Available;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockDishRepository
            .Setup(r => r.GetDishByIdAsync(dish.Id))
            .ReturnsAsync(dish);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync(exceptionType, () =>
            _guestSessionService.AddDishAsync(guestSession.Id, dish.Id, quantity: 2));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldDecreaseQuantity_WhenQuantityIsGreaterThanAmountToRemove(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 5;
        int quantityToRemove = 2;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);


        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Single(guestSession.Dishes);
        Assert.Equal(initialQuantity - quantityToRemove, guestSession.Dishes[0].Quantity);

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldRemoveDishFromList_WhenQuantityToRemoveIsEqualOrGreater(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        int initialQuantity = 2;
        int quantityToRemove = 3;
        guestSession.Dishes =
        [
            new OrderItem { DishId = dishId, Quantity = initialQuantity, State = OrderItemState.Pending }
        ];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, quantityToRemove);

        Assert.Empty(guestSession.Dishes);
        _mockGuestSessionRepository.Verify(r => r.SaveAsync(guestSession), Times.Once);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowDishNotFoundException_WhenDishDoesNotExistInSession(GuestSession guestSession, GroupReservation groupReservation, string dishId)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
           .ReturnsAsync(groupReservation);

        var exception = await Assert.ThrowsAsync<DishNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSession.Id, dishId, 2));

        Assert.Contains("not found in guest session", exception.Message);
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowValidationException_WhenQuantityIsZeroOrLess(string guestSessionId, string dishId)
    {
        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 0));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, -5));
    }

    [Theory]
    [AutoData]
    public async Task RemoveDishAsync_ShouldThrowGuestSessionNotFoundException_WhenSessionNotFound(string guestSessionId, string dishId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        var exception = await Assert.ThrowsAsync<GuestSessionNotFoundException>(() =>
            _guestSessionService.RemoveDishAsync(guestSessionId, dishId, 2));
    }
    [Theory]
    [InlineAutoData(1, 0)]
    [InlineAutoData(1, 1)]
    [InlineAutoData(2, 1)]
    [InlineAutoData(2, 2)]
    public async Task CreateGuestSessionAsync_WhenCapacityReached_ThrowsGuestLimitExceededException(
        int reservationCapacity,
        int guestCount,
        CreateGuestSessionRequest request,
        GroupReservation groupReservation,
        Reservation parentReservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.ReservationId = parentReservation.Id;

        parentReservation.GuestCount = reservationCapacity;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(parentReservation.Id))
            .ReturnsAsync(parentReservation);

        _mockGuestSessionRepository
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(guestCount);

        var exception = await Assert.ThrowsAsync<GuestLimitExceededException>(() => _guestSessionService.CreateGuestSessionAsync(request));

        _mockGuestSessionRepository.Verify(r => r.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Theory]
    [AutoData]
    public async Task CreateGuestSessionAsync_MissingParentReservation_ThrowsGroupReservationExpiredException(
        CreateGuestSessionRequest request,
        GroupReservation groupReservation)
    {
        groupReservation.GroupReservationId = request.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        var exception = await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _guestSessionService.CreateGuestSessionAsync(request));
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_ValidTokenAndId_ReturnsTrue(GuestSession guestSession, byte[] tokenBytes)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSession.Id);

        Assert.True(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidToken_ReturnsFalse(byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync((GuestSession?)null);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }

    [Theory]
    [AutoData]
    public async Task IsAuthorizedAsync_InvalidGuestSessionId_ReturnsFalse(GuestSession guestSession, byte[] tokenBytes, string guestSessionId)
    {
        string tokenString = Convert.ToBase64String(tokenBytes);
        var hashString = Convert.ToBase64String(SHA256.HashData(tokenBytes));
        guestSession.SessionTokenHash = hashString;

        _mockGuestSessionRepository
            .Setup(r => r.GetByTokenHash(hashString))
            .ReturnsAsync(guestSession);

        bool result = await _guestSessionService.IsAuthorizedAsync(tokenString, guestSessionId);

        Assert.False(result);
    }
    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_Valid_SubmitsSuccessfuilly(GuestSession guestSession, GroupReservation groupReservation, Dish[] dishes)
    {
        foreach (var dish in dishes)
            dish.State = DishState.Available;

        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(dishes);

        GuestSession? session = null;
        _mockGuestSessionRepository
            .Setup(r => r.SaveAsync(It.IsAny<GuestSession>()))
            .Callback<GuestSession>(s => session = s);

        await _guestSessionService.SubmitCartAsync(guestSession.Id);

        Assert.NotNull(session);
        Assert.True(session.IsSubmitted);
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_SessionNotFound_ThrowsGuestSessionNotFound(string guestSessionId)
    {
        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(() => _guestSessionService.SubmitCartAsync(guestSessionId));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    public async Task SubmitCartAsync_GroupReservationNotOpen_ThrowsException(GroupReservationStatus status, Type exceptionType, GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = status;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(exceptionType, () => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionHasNoDishes_ThrowsCartIsEmptyException(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.Dishes = [];
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<CartIsEmptyException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [AutoData]
    public async Task SubmitCartAsync_GuestSessionSubmitted_ThrowsAlreadySubmited(GuestSession guestSession, GroupReservation groupReservation)
    {
        guestSession.IsSubmitted = true;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.Status = GroupReservationStatus.Open;

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GuestSessionSubmittedException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }

    [Theory]
    [InlineAutoData(DishState.OutOfStock)]
    [InlineAutoData(DishState.OnStop)]
    public async Task SubmitCartAsync_DishNotAvailable_ThrowsDishNotAvailable(DishState state, Dish dish, GuestSession guestSession, GroupReservation groupReservation)
    {
        dish.State = state;
        guestSession.IsSubmitted = false;
        guestSession.GroupReservationId = groupReservation.GroupReservationId;
        guestSession.Dishes = 
        [
            new OrderItem()
            {
                DishId = dish.Id,
                Quantity = 1,
            }
        ];

        _mockDishRepository
            .Setup(d => d.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([dish]);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(guestSession.Id))
            .ReturnsAsync(guestSession);

        _mockGroupReservationRepository
           .Setup(r => r.GetGroupReservationByIdAsync(guestSession.GroupReservationId))
           .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<DishIsNotAvailableException>(() => _guestSessionService.SubmitCartAsync(guestSession.Id));
    }
    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_Guest_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = false;

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(session.GuestName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Theory]
    [MemberAutoData(nameof(PepareDishesForGuestSession))]
    public async Task GetGuestSessionInfoAsync_GroupOwner_ReturnsAsync(
        IEnumerable<string> dishIds,
        IEnumerable<Dish> dishes,
        List<OrderItem> orderItems,
        decimal expectedTotal,
        GuestSession session,
        GroupReservation groupReservation,
        Reservation reservation,
        string customerId,
        string ownerName,
        UserType userType)
    {
        session.Dishes = orderItems;
        session.IsOwnerSession = true;
        session.GroupReservationId = groupReservation.GroupReservationId;
        groupReservation.ReservationId = reservation.Id;
        reservation.CustomerId = customerId;
        userType.Attributes.Add(new AttributeType()
        {
            Name = "given_name",
            Value = ownerName,
        });

        _mockDishRepository
            .Setup(r => r.GetDishesByIdsAsync(dishIds))
            .ReturnsAsync(dishes);

        _mockGuestSessionRepository
            .Setup(r => r.GetByGuestSessionId(session.Id))
            .ReturnsAsync(session);

        _mockGroupReservationRepository
            .Setup(r => r.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        _mockReservationRepository
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _mockUserRepository
            .Setup(r => r.GetUserByIdAsync(customerId))
            .ReturnsAsync(userType);

        GetGuestSessionResponse result = await _guestSessionService.GetGuestSessionInfoAsync(session.Id);

        Assert.Equal(ownerName, result.Name);
        Assert.Equal(dishes.Count(), result.Items.Count());
        Assert.Equal(expectedTotal, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_MixesOwnerAndGuestParticipantsWithDishDetailsAndTotals()
    {
        const string userId = "owner-user-id";
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        var group = new GroupReservation
        {
            GroupReservationId = groupReservationId,
            ReservationId = reservationId,
            ReservationToken = "reservation-token"
        };
        var reservation = new Reservation
        {
            Id = reservationId,
            TableId = "table-id",
            CustomerId = userId,
            WaiterId = "waiter-id"
        };
        var ownerSession = new GuestSession
        {
            Id = "owner-session-id",
            GroupReservationId = groupReservationId,
            GuestName = "unused owner session name",
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "dish-1", Quantity = 2 }]
        };
        var guestSession = new GuestSession
        {
            Id = "guest-session-id",
            GroupReservationId = groupReservationId,
            GuestName = "Guest Name",
            IsOwnerSession = false,
            IsSubmitted = false,
            Dishes = [new OrderItem { DishId = "dish-1", Quantity = 3 }]
        };
        var dish = new Dish { Id = "dish-1", Name = "Soup", Price = 4.25m };
        var owner = new UserType
        {
            Attributes = [new AttributeType { Name = "given_name", Value = "Owner Name" }]
        };

        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(group);
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(reservation);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([ownerSession, guestSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(
                ids => ids.SequenceEqual(new[] { "dish-1" }))))
            .ReturnsAsync([dish]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(userId))
            .ReturnsAsync(owner);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupReservationId);

        var participants = result.Participants.ToArray();
        Assert.Equal(2, participants.Length);

        Assert.Equal("owner-session-id", participants[0].Id);
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal("Owner Name", participants[0].Name);
        Assert.True(participants[0].IsSubmitted);
        Assert.Equal(8.5m, participants[0].Total);
        var ownerItem = Assert.Single(participants[0].Items);
        Assert.Equal("dish-1", ownerItem.Dish.Id);
        Assert.Equal("Soup", ownerItem.Dish.Name);
        Assert.Equal(4.25m, ownerItem.Dish.Price);
        Assert.Equal(2, ownerItem.Quantity);

        Assert.Equal("guest-session-id", participants[1].Id);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.Equal("Guest Name", participants[1].Name);
        Assert.False(participants[1].IsSubmitted);
        Assert.Equal(12.75m, participants[1].Total);
        var guestItem = Assert.Single(participants[1].Items);
        Assert.Equal("dish-1", guestItem.Dish.Id);
        Assert.Equal("Soup", guestItem.Dish.Name);
        Assert.Equal(4.25m, guestItem.Dish.Price);
        Assert.Equal(3, guestItem.Quantity);
        Assert.Equal(21.25m, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_EmptyGroupReturnsNoParticipantsAndZeroTotal()
    {
        const string userId = "owner-user-id";
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = groupReservationId,
                ReservationId = reservationId,
                ReservationToken = "reservation-token"
            });
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation
            {
                Id = reservationId,
                TableId = "table-id",
                CustomerId = userId,
                WaiterId = "waiter-id"
            });
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync([]);

        var result = await _guestSessionService.GetGroupSessionsAsync(userId, groupReservationId);

        Assert.Empty(result.Participants);
        Assert.Equal(0m, result.Total);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_NonOwnerIsForbiddenWithoutReadingSessionsOrDishes()
    {
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = groupReservationId,
                ReservationId = reservationId,
                ReservationToken = "reservation-token"
            });
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation
            {
                Id = reservationId,
                TableId = "table-id",
                CustomerId = "actual-owner",
                WaiterId = "waiter-id"
            });

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetGroupSessionsAsync("different-user", groupReservationId));

        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        _mockDishRepository.Verify(
            repository => repository.GetDishesByIdsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_MissingGroupThrowsNotFound()
    {
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync("missing-group"))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync("owner-user-id", "missing-group"));

        _mockReservationRepository.Verify(
            repository => repository.GetReservationByIdAsync(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_MissingParentReservationThrowsNotFound()
    {
        const string groupReservationId = "group-id";
        const string reservationId = "missing-reservation";
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = groupReservationId,
                ReservationId = reservationId,
                ReservationToken = "reservation-token"
            });
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync("owner-user-id", groupReservationId));

        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetGroupSessionsAsync_DanglingDishIdThrowsDishNotFound()
    {
        const string userId = "owner-user-id";
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = groupReservationId,
                ReservationId = reservationId,
                ReservationToken = "reservation-token"
            });
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation
            {
                Id = reservationId,
                TableId = "table-id",
                CustomerId = userId,
                WaiterId = "waiter-id"
            });
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync(
            [
                new GuestSession
                {
                    Id = "guest-session-id",
                    GroupReservationId = groupReservationId,
                    GuestName = "Guest",
                    Dishes = [new OrderItem { DishId = "missing-dish", Quantity = 1 }]
                }
            ]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(
                ids => ids.SequenceEqual(new[] { "missing-dish" }))))
            .ReturnsAsync([]);

        await Assert.ThrowsAsync<DishNotFoundException>(
            () => _guestSessionService.GetGroupSessionsAsync(userId, groupReservationId));
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_OwnerDeletesGuest_ReturnsRemainingSnapshotAndRecalculatesTotal()
    {
        const string userId = "owner-user-id";
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        const string deletedGuestSessionId = "deleted-guest-session-id";
        SetupDeleteGuestSessionOwnerAccess(userId, groupReservationId, reservationId);

        var ownerSession = new GuestSession
        {
            Id = "owner-session-id",
            GroupReservationId = groupReservationId,
            GuestName = "Unused owner name",
            IsOwnerSession = true,
            IsSubmitted = true,
            Dishes = [new OrderItem { DishId = "owner-dish", Quantity = 2 }]
        };
        var deletedGuestSession = new GuestSession
        {
            Id = deletedGuestSessionId,
            GroupReservationId = groupReservationId,
            GuestName = "Deleted Guest",
            Dishes = [new OrderItem { DishId = "deleted-dish", Quantity = 1 }]
        };
        var remainingGuestSession = new GuestSession
        {
            Id = "remaining-guest-session-id",
            GroupReservationId = groupReservationId,
            GuestName = "Remaining Guest",
            IsSubmitted = false,
            Dishes = [new OrderItem { DishId = "remaining-dish", Quantity = 2 }]
        };
        var owner = new UserType
        {
            Attributes = [new AttributeType { Name = "given_name", Value = "Owner Name" }]
        };
        var ownerDish = new Dish { Id = "owner-dish", Name = "Owner meal", Price = 5m };
        var remainingDish = new Dish { Id = "remaining-dish", Name = "Guest meal", Price = 3.25m };

        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId(deletedGuestSessionId))
            .ReturnsAsync(deletedGuestSession);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync([ownerSession, deletedGuestSession, remainingGuestSession]);
        _mockDishRepository
            .Setup(repository => repository.GetDishesByIdsAsync(It.Is<IEnumerable<string>>(
                ids => ids.OrderBy(id => id).SequenceEqual(new[] { "owner-dish", "remaining-dish" }))))
            .ReturnsAsync([ownerDish, remainingDish]);
        _mockUserRepository
            .Setup(repository => repository.GetUserByIdAsync(userId))
            .ReturnsAsync(owner);

        var result = await _guestSessionService.DeleteGuestSessionAsync(
            userId, groupReservationId, deletedGuestSessionId);

        var participants = result.Participants.ToArray();
        Assert.Equal(2, participants.Length);
        Assert.Equal("owner-session-id", participants[0].Id);
        Assert.Equal(ParticipantType.Owner, participants[0].Type);
        Assert.Equal("Owner Name", participants[0].Name);
        Assert.Equal(10m, participants[0].Total);
        Assert.Equal("remaining-guest-session-id", participants[1].Id);
        Assert.Equal(ParticipantType.Guest, participants[1].Type);
        Assert.Equal("Remaining Guest", participants[1].Name);
        Assert.Equal(6.5m, participants[1].Total);
        Assert.DoesNotContain(participants, participant => participant.Id == deletedGuestSessionId);
        Assert.Equal(16.5m, result.Total);

        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(deletedGuestSession), Times.Once);
        _mockDishRepository.Verify(repository => repository.GetDishesByIdsAsync(
            It.Is<IEnumerable<string>>(ids => !ids.Contains("deleted-dish"))), Times.Once);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_NonOwnerIsForbiddenWithoutDeletingSession()
    {
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        const string guestSessionId = "guest-session-id";
        SetupDeleteGuestSessionOwnerAccess("actual-owner", groupReservationId, reservationId);

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.DeleteGuestSessionAsync("different-user", groupReservationId, guestSessionId));

        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetByGuestSessionId(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_SessionFromDifferentGroupThrowsValidationWithoutDeleting()
    {
        const string userId = "owner-user-id";
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        const string guestSessionId = "guest-session-id";
        SetupDeleteGuestSessionOwnerAccess(userId, groupReservationId, reservationId);
        var session = new GuestSession
        {
            Id = guestSessionId,
            GroupReservationId = "different-group-id",
            GuestName = "Guest",
            Dishes = []
        };
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync(session);

        await Assert.ThrowsAsync<ValidationException>(
            () => _guestSessionService.DeleteGuestSessionAsync(userId, groupReservationId, guestSessionId));

        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGuestSessionAsync_MissingSessionThrowsNotFoundWithoutDeleting()
    {
        const string userId = "owner-user-id";
        const string groupReservationId = "group-id";
        const string reservationId = "reservation-id";
        const string guestSessionId = "missing-session-id";
        SetupDeleteGuestSessionOwnerAccess(userId, groupReservationId, reservationId);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetByGuestSessionId(guestSessionId))
            .ReturnsAsync((GuestSession?)null);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _guestSessionService.DeleteGuestSessionAsync(userId, groupReservationId, guestSessionId));

        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_ReturnsExactPersistedOwnerSessionId()
    {
        const string userId = "owner-user-id";
        const string reservationId = "reservation-id";
        const string groupReservationId = "group-id";
        const string persistedSessionId = "persisted-owner-session-id";
        SetupOwnerReservationAndGroup(userId, reservationId, groupReservationId);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync(
            [
                new GuestSession
                {
                    Id = persistedSessionId,
                    GroupReservationId = groupReservationId,
                    GuestName = "Owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = userId
                }
            ]);

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId);

        Assert.Equal(persistedSessionId, result);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(groupReservationId), Times.Once);
        _mockGuestSessionRepository.Verify(
            repository => repository.SaveAsync(It.IsAny<GuestSession>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.DeleteAsync(It.IsAny<GuestSession>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_FiltersOutGuestOtherOwnersAndMismatchedGroupSessions()
    {
        const string userId = "owner-user-id";
        const string reservationId = "reservation-id";
        const string groupReservationId = "group-id";
        SetupOwnerReservationAndGroup(userId, reservationId, groupReservationId);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync(
            [
                new GuestSession
                {
                    Id = "guest-session",
                    GroupReservationId = groupReservationId,
                    GuestName = "Guest",
                    Dishes = [],
                    IsOwnerSession = false,
                    OwnerUserId = userId
                },
                new GuestSession
                {
                    Id = "other-owner-session",
                    GroupReservationId = groupReservationId,
                    GuestName = "Other owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = "another-user-id"
                },
                new GuestSession
                {
                    Id = "mismatched-group-session",
                    GroupReservationId = "another-group-id",
                    GuestName = "Owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = userId
                },
                new GuestSession
                {
                    Id = "matching-owner-session",
                    GroupReservationId = groupReservationId,
                    GuestName = "Owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = userId
                }
            ]);

        var result = await _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId);

        Assert.Equal("matching-owner-session", result);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NonOwnerThrowsForbiddenBeforeLookingUpGroup()
    {
        const string reservationId = "reservation-id";
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation
            {
                Id = reservationId,
                TableId = "table-id",
                CustomerId = "actual-owner-id",
                WaiterId = "waiter-id"
            });

        await Assert.ThrowsAsync<ActionForbiddenException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync("different-user-id", reservationId));

        _mockGroupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()), Times.Never);
        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_MissingReservationThrowsNotFound()
    {
        const string reservationId = "missing-reservation";
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync("owner-user-id", reservationId));

        _mockGroupReservationRepository.Verify(
            repository => repository.GetByReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_MissingGroupReservationThrowsNotFound()
    {
        const string userId = "owner-user-id";
        const string reservationId = "reservation-id";
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation
            {
                Id = reservationId,
                TableId = "table-id",
                CustomerId = userId,
                WaiterId = "waiter-id"
            });
        _mockGroupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(reservationId))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId));

        _mockGuestSessionRepository.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_NoResolvableOwnerSessionThrowsNotFound()
    {
        const string userId = "owner-user-id";
        const string reservationId = "reservation-id";
        const string groupReservationId = "group-id";
        SetupOwnerReservationAndGroup(userId, reservationId, groupReservationId);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync(
            [
                new GuestSession
                {
                    Id = "guest-session",
                    GroupReservationId = groupReservationId,
                    GuestName = "Guest",
                    Dishes = [],
                    IsOwnerSession = false,
                    OwnerUserId = userId
                },
                new GuestSession
                {
                    Id = "other-owner-session",
                    GroupReservationId = groupReservationId,
                    GuestName = "Other owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = "another-user-id"
                },
                new GuestSession
                {
                    Id = "mismatched-group-session",
                    GroupReservationId = "another-group-id",
                    GuestName = "Owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = userId
                }
            ]);

        await Assert.ThrowsAsync<GuestSessionNotFoundException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId));
    }

    [Fact]
    public async Task GetOwnerGuestSessionIdAsync_MultipleMatchingOwnerSessionsThrowsDuplicateException()
    {
        const string userId = "owner-user-id";
        const string reservationId = "reservation-id";
        const string groupReservationId = "group-id";
        SetupOwnerReservationAndGroup(userId, reservationId, groupReservationId);
        _mockGuestSessionRepository
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservationId))
            .ReturnsAsync(
            [
                new GuestSession
                {
                    Id = "matching-owner-session-one",
                    GroupReservationId = groupReservationId,
                    GuestName = "Owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = userId
                },
                new GuestSession
                {
                    Id = "matching-owner-session-two",
                    GroupReservationId = groupReservationId,
                    GuestName = "Owner",
                    Dishes = [],
                    IsOwnerSession = true,
                    OwnerUserId = userId
                }
            ]);

        await Assert.ThrowsAsync<DuplicateOwnerGuestSessionsException>(
            () => _guestSessionService.GetOwnerGuestSessionIdAsync(userId, reservationId));
    }

    private void SetupOwnerReservationAndGroup(string userId, string reservationId, string groupReservationId)
    {
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation
            {
                Id = reservationId,
                TableId = "table-id",
                CustomerId = userId,
                WaiterId = "waiter-id"
            });
        _mockGroupReservationRepository
            .Setup(repository => repository.GetByReservationIdAsync(reservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = groupReservationId,
                ReservationId = reservationId,
                ReservationToken = "reservation-token"
            });
    }

    private void SetupDeleteGuestSessionOwnerAccess(string ownerUserId, string groupReservationId, string reservationId)
    {
        _mockGroupReservationRepository
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservationId))
            .ReturnsAsync(new GroupReservation
            {
                GroupReservationId = groupReservationId,
                ReservationId = reservationId,
                ReservationToken = "reservation-token"
            });
        _mockReservationRepository
            .Setup(repository => repository.GetReservationByIdAsync(reservationId))
            .ReturnsAsync(new Reservation
            {
                Id = reservationId,
                TableId = "table-id",
                CustomerId = ownerUserId,
                WaiterId = "waiter-id"
            });
    }

    public static TheoryData<IEnumerable<string>, IEnumerable<Dish>, List<OrderItem>, decimal> PepareDishesForGuestSession()
    {
        var dishMap = new Dictionary<string, Dish>()
        {
            ["1"] = new()
            {
                Id = "1",
                Price = 1
            },
            ["2"] = new()
            {
                Id = "2",
                Price = 2
            },
            ["3"] = new()
            {
                Id = "3",
                Price = 3
            }
        };

        var orderItems = new List<OrderItem>
        {
            new OrderItem()
                    {
                        DishId = "1",
                        Quantity = 2
                    },
                    new OrderItem()
                    {
                        DishId = "2",
                        Quantity = 3
                    },
                    new OrderItem()
                    {
                        DishId = "3",
                        Quantity = 1
                    }
        };

        var expectedTotal = orderItems.Sum(o => dishMap[o.DishId].Price * o.Quantity);

        return new()
        {
            { dishMap.Keys, dishMap.Values, orderItems, expectedTotal }
        };
    }
}
