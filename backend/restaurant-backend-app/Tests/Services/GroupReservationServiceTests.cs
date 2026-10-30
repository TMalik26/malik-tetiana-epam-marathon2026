using APIHandler.Constants;
using APIHandler.Constants.Orders;
using APIHandler.Entities;
using APIHandler.Exceptions;
using APIHandler.Models.GroupReservation;
using APIHandler.Repositories.GroupReservations;
using APIHandler.Repositories.GuestSessions;
using APIHandler.Repositories.Reservations;
using APIHandler.Repositories.Tables;
using APIHandler.Services.Auth;
using APIHandler.Services.GroupReservations;
using APIHandler.Services.Locations;
using APIHandler.Services.Orders;
using AutoFixture.Xunit2;
using Moq;

namespace APIHandler.Tests.Services;

public class GroupReservationServiceTests
{
    private readonly Mock<IGroupReservationRepository> _groupReservationRepositoryMock = new();
    private readonly Mock<ITableRepository> _tableRepositoryMock = new();
    private readonly Mock<IReservationRepository> _reservationRepositoryMock = new();
    private readonly Mock<IGuestSessionRepository> _guestSessionRepositoryMock = new();
    private readonly Mock<IUserService> _userServiceMock = new();
    private readonly Mock<ILocationService> _locationServiceMock = new();
    private readonly Mock<IOrderService> _orderServiceMock = new();

    private readonly GroupReservationService _service;

    public GroupReservationServiceTests()
    {
        _service = new GroupReservationService(
            _groupReservationRepositoryMock.Object,
            _tableRepositoryMock.Object,
            _reservationRepositoryMock.Object,
            _guestSessionRepositoryMock.Object,
            _userServiceMock.Object,
            _locationServiceMock.Object,
            _orderServiceMock.Object);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_ValidInput_ReturnsGroupReservationInfo(
        GroupReservation groupReservation,
        Table table,
        Reservation reservation,
        Location location,
        string expectedUserName)
    {
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;
        reservation.TableId = table.Id;
        table.LocationId = location.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(0);

        _tableRepositoryMock
            .Setup(r => r.GetTableAsync(table.Id))
            .ReturnsAsync(table);

        _locationServiceMock
            .Setup(s => s.GetLocationByIdAsync(table.LocationId))
            .ReturnsAsync(location);

        _userServiceMock
            .Setup(s => s.GetUserName(reservation.CustomerId!))
            .ReturnsAsync(expectedUserName);

        var result = await _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken);

        Assert.Equal(groupReservation.GroupReservationId, result.GroupReservationId);
        Assert.Equal(table.LocationId, result.LocationId);
        Assert.Equal(reservation.GuestCount, result.MaxGuestCount);
        Assert.Equal(reservation.StartTime, result.StartTime);
        Assert.Equal(reservation.EndTime, result.EndTime);
        Assert.Equal(table.TableNumber, result.TableNumber);
        Assert.Equal("Green & Tasty", result.RestaurantName);
        Assert.Equal(location.Address, result.LocationAddress);
        Assert.Equal(expectedUserName, result.InviterName);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_InvalidToken_ThrowsNotFound(string token)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(token))
            .ReturnsAsync((GroupReservation?)null);

        await Assert.ThrowsAsync<GroupReservationNotFoundException>(() => _service.GetGroupReservationByTokenAsync(token));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed)]
    [InlineAutoData(GroupReservationStatus.Submitted)]
    public async Task GetGroupReservationByTokenAsync_GroupReservationIsNotOpen_ThrowsException(
        GroupReservationStatus status,
        GroupReservation groupReservation)
    {
        groupReservation.Status = status;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationByTokenAsync_WhenGuestCountIsMax_ThrowsGuestLimitExceeded(GroupReservation groupReservation, Reservation reservation)
    {
        groupReservation.Status = GroupReservationStatus.Open;
        groupReservation.ReservationId = reservation.Id;

        _groupReservationRepositoryMock
            .Setup(r => r.GetGroupReservationByTokenAsync(groupReservation.ReservationToken))
            .ReturnsAsync(groupReservation);

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _guestSessionRepositoryMock
            .Setup(r => r.CountByGroupReservationId(groupReservation.GroupReservationId))
            .ReturnsAsync(reservation.GuestCount);

        await Assert.ThrowsAsync<GuestLimitExceededException>(() => _service.GetGroupReservationByTokenAsync(groupReservation.ReservationToken));
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_ValidInput_UpdatesGroupReservationStatus(GroupReservation groupReservation)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(groupReservation.ReservationId))
            .ReturnsAsync(groupReservation);

        GroupReservation? savedGroupReservation = null;
        _groupReservationRepositoryMock
            .Setup(r => r.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(r => savedGroupReservation = r);

        await _service.CloseGroupReservationAsync(groupReservation.ReservationId);

        Assert.NotNull(savedGroupReservation);
        Assert.Equal(GroupReservationStatus.Closed, savedGroupReservation.Status);
    }

    [Theory]
    [AutoData]
    public async Task CloseGroupReservationAsync_GroupReservationNotFound_DoesNotThrowException(string reservationId)
    {
        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservationId))
            .ReturnsAsync((GroupReservation?)null);

        await _service.CloseGroupReservationAsync(reservationId);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_ValidInput_ReturnsToken(Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = GroupReservationStatus.Open;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        GetGroupInviteTokenResponse result = await _service.GetGroupReservationInviteTokenAsync(reservation.Id);

        Assert.Equal(groupReservation.ReservationToken, result.Token);
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_InvalidReservationId_ThrowsNotFound(string reservationId)
    {
        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() => _service.GetGroupReservationInviteTokenAsync(reservationId));
    }

    [Theory]
    [AutoData]
    public async Task GetGroupReservationInviteTokenAsync_OneGuest_ThrowsValidationException(Reservation reservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(ReservationStatus.Completed)]
    [InlineAutoData(ReservationStatus.Cancelled)]
    [InlineAutoData(ReservationStatus.InProgress)]
    public async Task GetGroupReservationInviteTokenAsync_StatusIsNotReserved_ThrowsValidationException(ReservationStatus reservationStatus, Reservation reservation)
    {
        reservation.Status = reservationStatus;
        reservation.GuestCount = 1;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        await Assert.ThrowsAsync<ValidationException>(() => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Theory]
    [InlineAutoData(GroupReservationStatus.Closed, typeof(GroupReservationExpiredException))]
    [InlineAutoData(GroupReservationStatus.Submitted, typeof(GroupReservationSubmittedException))]
    public async Task GetGroupReservationInviteTokenAsync_GroupStatusIsNotOpen_ThrowsException(GroupReservationStatus status, Type type, Reservation reservation, GroupReservation groupReservation)
    {
        reservation.Status = ReservationStatus.Reserved;
        reservation.GuestCount = 3;
        groupReservation.ReservationId = reservation.Id;
        groupReservation.Status = status;

        _reservationRepositoryMock
            .Setup(r => r.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);

        _groupReservationRepositoryMock
            .Setup(r => r.GetByReservationIdAsync(reservation.Id))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync(type, () => _service.GetGroupReservationInviteTokenAsync(reservation.Id));
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ReservationIdDoesNotMatchGroup_ThrowsValidationWithoutPersistence()
    {
        var groupReservation = CreateGroupReservation();
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync("owner", groupReservation.GroupReservationId, "different-reservation"));

        _reservationRepositoryMock.Verify(repository => repository.GetReservationByIdAsync(It.IsAny<string>()), Times.Never);
        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyCollection<APIHandler.Models.GroupReservation.SubmittedDishQuantity>>()),
            Times.Never);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_MissingParentReservationThrowsNotFoundWithoutFurtherWork()
    {
        var groupReservation = CreateGroupReservation();
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(groupReservation.ReservationId))
            .ReturnsAsync((Reservation?)null);

        await Assert.ThrowsAsync<ReservationNotFoundException>(() =>
            _service.SubmitGroupReservationAsync(
                "owner", groupReservation.GroupReservationId, groupReservation.ReservationId));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyCollection<SubmittedDishQuantity>>()),
            Times.Never);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_NonOwnerIsForbiddenWithoutPersistence()
    {
        var groupReservation = CreateGroupReservation();
        var reservation = CreateReservation(groupReservation.ReservationId, "actual-owner");
        ConfigureSubmission(groupReservation, reservation);

        await Assert.ThrowsAsync<ActionForbiddenException>(() =>
            _service.SubmitGroupReservationAsync("different-user", groupReservation.GroupReservationId, reservation.Id));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyCollection<APIHandler.Models.GroupReservation.SubmittedDishQuantity>>()),
            Times.Never);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_AlreadySubmittedDoesNotLoadSessionsOrSaveAnything()
    {
        var groupReservation = CreateGroupReservation(GroupReservationStatus.Submitted);
        var reservation = CreateReservation(groupReservation.ReservationId, "owner");
        ConfigureSubmission(groupReservation, reservation);

        await Assert.ThrowsAsync<GroupReservationSubmittedException>(() =>
            _service.SubmitGroupReservationAsync("owner", groupReservation.GroupReservationId, reservation.Id));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyCollection<APIHandler.Models.GroupReservation.SubmittedDishQuantity>>()),
            Times.Never);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_ClosedGroupDoesNotMergeOrSaveAnything()
    {
        var groupReservation = CreateGroupReservation(GroupReservationStatus.Closed);
        var reservation = CreateReservation(groupReservation.ReservationId, "owner");
        ConfigureSubmission(groupReservation, reservation);

        await Assert.ThrowsAsync<GroupReservationExpiredException>(() =>
            _service.SubmitGroupReservationAsync("owner", groupReservation.GroupReservationId, reservation.Id));

        _guestSessionRepositoryMock.Verify(
            repository => repository.GetAllByGroupReservationIdAsync(It.IsAny<string>()), Times.Never);
        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyCollection<APIHandler.Models.GroupReservation.SubmittedDishQuantity>>()),
            Times.Never);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubmitGroupReservationAsync_NoSubmittedDishesThrowsValidationWithoutPersistence(bool includeEmptySession)
    {
        var groupReservation = CreateGroupReservation();
        var reservation = CreateReservation(groupReservation.ReservationId, "owner");
        ConfigureSubmission(groupReservation, reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(includeEmptySession
                ? new List<GuestSession>
                {
                    new()
                    {
                        Id = "session-1",
                        GroupReservationId = groupReservation.GroupReservationId,
                        GuestName = "Guest",
                        IsSubmitted = true,
                        Dishes = new List<OrderItem>()
                    }
                }
                : new List<GuestSession>());

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync("owner", groupReservation.GroupReservationId, reservation.Id));

        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyCollection<APIHandler.Models.GroupReservation.SubmittedDishQuantity>>()),
            Times.Never);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_OrderMergeFailureDoesNotSaveGroupAsSubmitted()
    {
        var groupReservation = CreateGroupReservation();
        var reservation = CreateReservation(groupReservation.ReservationId, "owner");
        ConfigureSubmission(groupReservation, reservation);
        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(new List<GuestSession>
            {
                CreateSession("submitted-session", groupReservation.GroupReservationId, true,
                    new OrderItem { DishId = "dish-1", Quantity = 1, State = OrderItemState.Pending })
            });
        _orderServiceMock
            .Setup(service => service.MergeGroupReservationDishesIntoOrderAsync(
                groupReservation.ReservationId,
                It.IsAny<IReadOnlyCollection<SubmittedDishQuantity>>()))
            .ThrowsAsync(new ValidationException("No order is associated with this reservation."));

        await Assert.ThrowsAsync<ValidationException>(() =>
            _service.SubmitGroupReservationAsync("owner", groupReservation.GroupReservationId, reservation.Id));

        Assert.Equal(GroupReservationStatus.Open, groupReservation.Status);
        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                groupReservation.ReservationId, It.IsAny<IReadOnlyCollection<SubmittedDishQuantity>>()),
            Times.Once);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()), Times.Never);
    }

    [Fact]
    public async Task SubmitGroupReservationAsync_AggregatesOnlySubmittedSessionsFromThisGroupAndSavesSubmittedStatus()
    {
        var groupReservation = CreateGroupReservation();
        var reservation = CreateReservation(groupReservation.ReservationId, "owner");
        ConfigureSubmission(groupReservation, reservation);

        _guestSessionRepositoryMock
            .Setup(repository => repository.GetAllByGroupReservationIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(new List<GuestSession>
            {
                CreateSession("submitted-1", groupReservation.GroupReservationId, true,
                    new OrderItem { DishId = "dish-1", Quantity = 2, State = OrderItemState.Pending },
                    new OrderItem { DishId = "dish-2", Quantity = 1, State = OrderItemState.Pending }),
                CreateSession("submitted-2", groupReservation.GroupReservationId, true,
                    new OrderItem { DishId = "dish-1", Quantity = 3, State = OrderItemState.Confirmed }),
                CreateSession("not-submitted", groupReservation.GroupReservationId, false,
                    new OrderItem { DishId = "dish-ignored", Quantity = 10, State = OrderItemState.Pending }),
                CreateSession("other-group", "another-group", true,
                    new OrderItem { DishId = "dish-ignored", Quantity = 20, State = OrderItemState.Pending })
            });

        IReadOnlyCollection<SubmittedDishQuantity>? mergedDishes = null;
        _orderServiceMock
            .Setup(service => service.MergeGroupReservationDishesIntoOrderAsync(
                groupReservation.ReservationId,
                It.IsAny<IReadOnlyCollection<SubmittedDishQuantity>>()))
            .Callback<string, IReadOnlyCollection<SubmittedDishQuantity>>(
                (_, dishes) => mergedDishes = dishes)
            .Returns(Task.CompletedTask);

        GroupReservation? savedGroup = null;
        _groupReservationRepositoryMock
            .Setup(repository => repository.SaveGroupReservationAsync(It.IsAny<GroupReservation>()))
            .Callback<GroupReservation>(saved => savedGroup = saved)
            .Returns(Task.CompletedTask);

        var result = await _service.SubmitGroupReservationAsync(
            "owner", groupReservation.GroupReservationId, reservation.Id);

        Assert.Equal(2, result.Count);
        Assert.Equal(5, result.Single(dish => dish.DishId == "dish-1").Quantity);
        Assert.Equal(1, result.Single(dish => dish.DishId == "dish-2").Quantity);
        Assert.NotNull(mergedDishes);
        Assert.Equal(
            result.OrderBy(dish => dish.DishId),
            mergedDishes.OrderBy(dish => dish.DishId));
        Assert.Equal(GroupReservationStatus.Submitted, savedGroup!.Status);
        _orderServiceMock.Verify(
            service => service.MergeGroupReservationDishesIntoOrderAsync(
                groupReservation.ReservationId,
                It.IsAny<IReadOnlyCollection<SubmittedDishQuantity>>()),
            Times.Once);
        _groupReservationRepositoryMock.Verify(
            repository => repository.SaveGroupReservationAsync(groupReservation), Times.Once);
    }

    private void ConfigureSubmission(GroupReservation groupReservation, Reservation reservation)
    {
        _groupReservationRepositoryMock
            .Setup(repository => repository.GetGroupReservationByIdAsync(groupReservation.GroupReservationId))
            .ReturnsAsync(groupReservation);
        _reservationRepositoryMock
            .Setup(repository => repository.GetReservationByIdAsync(reservation.Id))
            .ReturnsAsync(reservation);
    }

    private static GroupReservation CreateGroupReservation(
        GroupReservationStatus status = GroupReservationStatus.Open) =>
        new()
        {
            GroupReservationId = "group-id",
            ReservationId = "reservation-id",
            ReservationToken = "token",
            Status = status
        };

    private static Reservation CreateReservation(string reservationId, string ownerId) =>
        new()
        {
            Id = reservationId,
            TableId = "table-id",
            WaiterId = "waiter-id",
            CustomerId = ownerId
        };

    private static GuestSession CreateSession(
        string id,
        string groupReservationId,
        bool submitted,
        params OrderItem[] dishes) =>
        new()
        {
            Id = id,
            GroupReservationId = groupReservationId,
            GuestName = "Guest",
            IsSubmitted = submitted,
            Dishes = dishes.ToList()
        };
}
