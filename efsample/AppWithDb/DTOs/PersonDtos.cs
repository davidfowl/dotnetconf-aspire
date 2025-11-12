namespace AppWithDb.DTOs;

public record CreatePersonRequest(
    string FirstName,
    string LastName,
    string Email,
    DateTime DateOfBirth
);

public record UpdatePersonRequest(
    string FirstName,
    string LastName,
    string Email,
    DateTime DateOfBirth
);

public record PersonResponse(
    int Id,
    string FirstName,
    string LastName,
    string Email,
    DateTime DateOfBirth,
    DateTime CreatedAt
);