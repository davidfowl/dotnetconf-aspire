#:package Aspire.Hosting.Docker@13.0.0-preview.1.25560.3
#:package Aspire.Hosting.PostgreSQL@13.0.0
#:package Bogus@35.6.1
#:sdk Aspire.AppHost.Sdk@13.0.0

using Aspire.Hosting.Pipelines;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Projects;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("dc");

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
       .WithIconName("WindowDevTools")
       .ExcludeFromManifest();

// Add the web app with a reference to the database
var app = builder.AddProject("app", "./AppWithDb")
    .WithHttpHealthCheck("/health")
    .WithReference(postgresdb)
    .WaitFor(postgresdb)
    .WithExternalHttpEndpoints()
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

// dotnet ef
var efmigrate = builder.AddEfMigrate(app, postgresdb);

// Ensure the app is built before running
app.WaitForCompletion(efmigrate);
app.WithChildRelationship(efmigrate);

// Add a seed-data command
app.WithDataPopulation();

builder.Build().Run();

#region Extension Methods

public static class ExtMethods
{
    extension(IDistributedApplicationBuilder builder)
    {
        public IResourceBuilder<ExecutableResource> AddEfMigrate(IResourceBuilder<ProjectResource> app, IResourceBuilder<IResourceWithConnectionString> database)
        {
            var projectDirectory = Path.GetDirectoryName(app.Resource.GetProjectMetadata().ProjectPath)!;

            var efmigrate = builder.AddExecutable($"ef-migrate-{app.Resource.Name}", "dotnet", projectDirectory)
                .WithArgs("ef")
                .WithArgs("database")
                .WithArgs("update")
                .WithArgs("--no-build")
                .WithArgs("--connection")
                .WithArgs(database.Resource)
                .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
                .WaitFor(database)
                .WithReference(database);

            efmigrate.WithPipelineStepFactory(factoryContext =>
            {
                var step = new PipelineStep
                {
                    Name = $"ef-migration-bundle-{app.Resource.Name}",
                    RequiredBySteps = ["deploy"],
                    Action = async context =>
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = "dotnet",
                            RedirectStandardError = true,
                            RedirectStandardOutput = true,
                            WorkingDirectory = projectDirectory
                        };
                        // dotnet ef migrations bundle --self-contained -r linux-x64
                        psi = psi.WithArgs("ef", "migrations", "bundle", "--self-contained", "-r", "linux-x64");

                        await psi.ExecuteAsync(context.Logger, context.CancellationToken);
                    }
                };

                return [step];
            });

            return efmigrate;
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

        public IResourceBuilder<ProjectResource> WithDataPopulation()
        {
            return resourceBuilder.WithCommand("seed-data", "Seed the database with fake data using Bogus", async context =>
            {
                await SeedDatabaseAsync(resourceBuilder, context);
                return new ExecuteCommandResult { Success = true };
            });

            static async Task SeedDatabaseAsync(IResourceBuilder<ProjectResource> app, ExecuteCommandContext context)
            {
                var cancellationToken = context.CancellationToken;
                var logger = context.ServiceProvider.GetRequiredService<ResourceLoggerService>().GetLogger(app.Resource);

                logger.LogInformation("🌱 Starting database seeding with Bogus...");

                // Wait a bit for the app to be fully ready
                using var httpClient = new HttpClient();

                // Get the actual endpoint URL dynamically
                var httpEndpoint = app.GetEndpoint("http");
                var baseUrl = await httpEndpoint.GetValueAsync(cancellationToken);

                // Create a Faker for Person data - using object initializer approach
                var personFaker = new Faker();

                logger.LogInformation($"📊 Generating 50 fake people. Starting to seed...");
                logger.LogInformation("🔗 Using endpoint: {baseUrl}", baseUrl);

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
                            logger.LogInformation("✅ Created: {FirstName} {LastName} ({Email})", firstName, lastName, email);
                        }
                        else
                        {
                            errorCount++;
                            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                            logger.LogError("❌ Failed to create {FirstName} {LastName}: {StatusCode} - {ErrorContent}", firstName, lastName, response.StatusCode, errorContent);
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        logger.LogError("💥 Exception creating person {Index}: {Message}", i, ex.Message);
                    }

                    // Small delay between requests
                    await Task.Delay(50, cancellationToken);
                }

                logger.LogInformation("🎉 Seeding complete! Created {SuccessCount} people, {ErrorCount} errors.", successCount, errorCount);
            }
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

    extension(ProcessStartInfo psi)
    {
        public ProcessStartInfo WithArgs(params string[] args)
        {
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
            return psi;
        }

        // Exec with logs

        public Task<int> ExecuteAsync(ILogger logger, CancellationToken cancellationToken = default)
        {
            var tcs = new TaskCompletionSource<int>();

            var process = new Process
            {
                StartInfo = psi,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    logger.LogDebug(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    logger.LogDebug(e.Data);
                }
            };

            process.Exited += (sender, e) =>
            {
                tcs.SetResult(process.ExitCode);
                process.Dispose();
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            cancellationToken.Register(() =>
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            });

            return tcs.Task;
        }
    }
}

#endregion