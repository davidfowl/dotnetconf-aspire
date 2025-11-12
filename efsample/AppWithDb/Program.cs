using AppWithDb.Data;
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

app.MapPersonApi();

app.Run();
