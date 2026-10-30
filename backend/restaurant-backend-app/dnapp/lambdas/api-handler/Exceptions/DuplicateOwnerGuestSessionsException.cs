using System;

namespace APIHandler.Exceptions;

public class DuplicateOwnerGuestSessionsException(string message) : Exception(message);
