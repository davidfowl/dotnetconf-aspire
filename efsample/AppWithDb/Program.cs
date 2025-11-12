using Microsoft.EntityFrameworkCore;
using AppWithDb.Data;
using AppWithDb.Models;
using AppWithDb.DTOs;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services
builder.Services.AddOpenApi();

// Add PostgreSQL with Aspire
builder.AddNpgsqlDbContext<AppDbContext>("appdb");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapDefaultEndpoints();

// CRUD API endpoints
app.MapGet("/", () => "People API - use /swagger to explore endpoints");

// GET all people
app.MapGet("/people", async (AppDbContext context) =>
{
    var people = await context.People
        .Select(p => new PersonResponse(p.Id, p.FirstName, p.LastName, p.Email, p.DateOfBirth, p.CreatedAt))
        .ToListAsync();
    return Results.Ok(people);
})
.WithName("GetPeople")
.WithTags("People")
.WithSummary("Get all people");

// GET person by ID
app.MapGet("/people/{id:int}", async (int id, AppDbContext context) =>
{
    var person = await context.People.FindAsync(id);
    if (person == null)
        return Results.NotFound($"Person with ID {id} not found");

    var response = new PersonResponse(person.Id, person.FirstName, person.LastName, person.Email, person.DateOfBirth, person.CreatedAt);
    return Results.Ok(response);
})
.WithName("GetPersonById")
.WithTags("People")
.WithSummary("Get a person by ID");

// POST create person
app.MapPost("/people", async (CreatePersonRequest request, AppDbContext context) =>
{
    // Check if email already exists
    var existingPerson = await context.People.FirstOrDefaultAsync(p => p.Email == request.Email);
    if (existingPerson != null)
        return Results.Conflict("A person with this email already exists");

    var person = new Person
    {
        FirstName = request.FirstName,
        LastName = request.LastName,
        Email = request.Email,
        DateOfBirth = request.DateOfBirth
    };

    context.People.Add(person);
    await context.SaveChangesAsync();

    var response = new PersonResponse(person.Id, person.FirstName, person.LastName, person.Email, person.DateOfBirth, person.CreatedAt);
    return Results.Created($"/people/{person.Id}", response);
})
.WithName("CreatePerson")
.WithTags("People")
.WithSummary("Create a new person");

// PUT update person
app.MapPut("/people/{id:int}", async (int id, UpdatePersonRequest request, AppDbContext context) =>
{
    var person = await context.People.FindAsync(id);
    if (person == null)
        return Results.NotFound($"Person with ID {id} not found");

    // Check if email already exists for a different person
    var existingPerson = await context.People.FirstOrDefaultAsync(p => p.Email == request.Email && p.Id != id);
    if (existingPerson != null)
        return Results.Conflict("A person with this email already exists");

    person.FirstName = request.FirstName;
    person.LastName = request.LastName;
    person.Email = request.Email;
    person.DateOfBirth = request.DateOfBirth;

    await context.SaveChangesAsync();

    var response = new PersonResponse(person.Id, person.FirstName, person.LastName, person.Email, person.DateOfBirth, person.CreatedAt);
    return Results.Ok(response);
})
.WithName("UpdatePerson")
.WithTags("People")
.WithSummary("Update an existing person");

// DELETE person
app.MapDelete("/people/{id:int}", async (int id, AppDbContext context) =>
{
    var person = await context.People.FindAsync(id);
    if (person == null)
        return Results.NotFound($"Person with ID {id} not found");

    context.People.Remove(person);
    await context.SaveChangesAsync();

    return Results.NoContent();
})
.WithName("DeletePerson")
.WithTags("People")
.WithSummary("Delete a person");

app.Run();
