#:package Aspire.Hosting.PostgreSQL@13.0.0
#:package Bogus@35.6.1
#:sdk Aspire.AppHost.Sdk@13.0.0

using Bogus;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using System.Text.Json;

var builder = DistributedApplication.CreateBuilder(args);

// Add PostgreSQL database
var postgres = builder.AddPostgres("postgres")
                    .WithPgAdmin(c => c.WithLifetime(ContainerLifetime.Persistent));

var postgresdb = postgres.AddDatabase("appdb")
    .WithResetDbCommand();

// PG MCP
builder.AddContainer("pgmcp", "crystaldba/postgres-mcp")
       .WithHttpEndpoint(port: 8000, targetPort: 8000)
       .WithEnvironment("DATABASE_URI", postgresdb.Resource.UriExpression)
       .WithArgs("--access-mode=unrestricted")
       .WithArgs("--transport=sse")
       .WaitFor(postgresdb)
       .WithParentRelationship(postgres)
       .ExcludeFromManifest();

// Add the web app with a reference to the database
var app = builder.AddProject("app", "./AppWithDb")
    .WithHttpHealthCheck("/health")
    .WithReference(postgresdb)
    .WaitFor(postgresdb)
    .WithBuild() // what is this?
    .WithUrls(context =>
    {
        foreach (var u in context.Urls)
        {
            u.DisplayLocation = UrlDisplayLocation.DetailsOnly;
        }

        // Only show the /scalar URL in the UI
        context.Urls.Add(new ResourceUrlAnnotation()
        {
            Url = "/scalar",
            DisplayText = "OpenAPI Docs",
            Endpoint = context.GetEndpoint("https")
        });
    });

var projectDirectory = Path.GetDirectoryName(app.Resource.GetProjectMetadata().ProjectPath)!;

// dotnet ef 
var efmigrate = builder.AddEfMigrate("ef-migrate", projectDirectory, postgresdb);

// Ensure the app is built before running
app.WaitForCompletion(efmigrate);
app.WithChildRelationship(efmigrate);

// Add a seed-data command
app.WithCommand("seed-data", "Seed the database with fake data using Bogus", async context =>
{
    await SeedDatabaseAsync(app, context.CancellationToken);
    return new ExecuteCommandResult { Success = true };
});

builder.Build().Run();

#region SEED API

static async Task SeedDatabaseAsync(IResourceBuilder<ProjectResource> app, CancellationToken cancellationToken)
{
    Console.WriteLine("🌱 Starting database seeding with Bogus...");

    // Wait a bit for the app to be fully ready
    using var httpClient = new HttpClient();

    // Get the actual endpoint URL dynamically
    var httpEndpoint = app.GetEndpoint("http");
    var baseUrl = await httpEndpoint.GetValueAsync(cancellationToken);

    // Create a Faker for Person data - using object initializer approach
    var personFaker = new Faker();

    Console.WriteLine($"📊 Generating 50 fake people. Starting to seed...");
    Console.WriteLine($"🔗 Using endpoint: {baseUrl}");

    int successCount = 0;
    int errorCount = 0;

    for (int i = 0; i < 50; i++)
    {
        try
        {
            // Generate fake person data
            var firstName = personFaker.Name.FirstName();
            var lastName = personFaker.Name.LastName();
            var email = personFaker.Internet.Email(firstName, lastName);
            var dateOfBirth = personFaker.Date.Between(DateTime.Now.AddYears(-80), DateTime.Now.AddYears(-18));

            var person = new
            {
                firstName,
                lastName,
                email,
                dateOfBirth = dateOfBirth.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };

            var json = JsonSerializer.Serialize(person);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{baseUrl}/people", content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                successCount++;
                Console.WriteLine($"✅ Created: {firstName} {lastName} ({email})");
            }
            else
            {
                errorCount++;
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"❌ Failed to create {firstName} {lastName}: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Exception ex)
        {
            errorCount++;
            Console.WriteLine($"💥 Exception creating person {i}: {ex.Message}");
        }

        // Small delay between requests
        await Task.Delay(50, cancellationToken);
    }

    Console.WriteLine($"🎉 Seeding complete! Created {successCount} people, {errorCount} errors.");
}

#endregion

#region Extension Methods

public static class ExtMethods
{
    extension(IDistributedApplicationBuilder builder)
    {
        public IResourceBuilder<ExecutableResource> AddEfMigrate(string name, string projectDirectory, IResourceBuilder<IResourceWithConnectionString> database)
        {
            return builder.AddExecutable(name, "dotnet", projectDirectory)
                .WithArgs("ef")
                .WithArgs("database")
                .WithArgs("update")
                .WithArgs("--no-build")
                .WithArgs("--connection")
                .WithArgs(database.Resource.ConnectionStringExpression)
                .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                .WaitFor(database)
                .WithReference(database);
        }
    }

    extension(IResourceBuilder<ProjectResource> resourceBuilder)
    {
        // Adds a dotnet build step for the project resource
        public IResourceBuilder<ProjectResource> WithBuild()
        {
            var projectDirectory = Path.GetDirectoryName(resourceBuilder.Resource.GetProjectMetadata().ProjectPath)!;

            var projectBuild = resourceBuilder.ApplicationBuilder.AddExecutable($"build-{resourceBuilder.Resource.Name}", "dotnet", projectDirectory)
                           .WithArgs("build");

            resourceBuilder.WithChildRelationship(projectBuild);
            resourceBuilder.WaitForCompletion(projectBuild);

            return resourceBuilder;
        }
    }

    extension(IResourceBuilder<PostgresDatabaseResource> resourceBuilder)
    {
        public IResourceBuilder<PostgresDatabaseResource> WithResetDbCommand()
        {
            return resourceBuilder.WithCommand("reset", "Reset Database", async context =>
            {
                var interactionService = context.ServiceProvider.GetRequiredService<IInteractionService>();

                var result = await interactionService.PromptConfirmationAsync("Are you sure you want to reset the database? This action cannot be undone.",
                    "Confirm Reset");

                if (!result.Data || result.Canceled)
                {
                    return new ExecuteCommandResult { Success = false, ErrorMessage = "Database reset cancelled by user." };
                }

                // Custom reset logic if needed
                return new ExecuteCommandResult { Success = true };
            });
        }
    }
}

#endregion