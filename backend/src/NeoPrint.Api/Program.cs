// Composition root for the NeoPrint API.
//
// This is the skeleton established with the solution layout. Configuration
// (OpenBao), persistence, messaging, authentication and the hubs are wired in
// as the rest of step 1 lands.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapControllers();

app.Run();

/// <summary>
/// Exposed so that <c>WebApplicationFactory</c> in the integration tests can
/// bootstrap this application.
/// </summary>
public partial class Program;
