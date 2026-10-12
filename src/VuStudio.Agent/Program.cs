var builder = WebApplication.CreateSlimBuilder(args);
var app = builder.Build();
app.MapGet("/api/v1/info", () => new { name = Environment.MachineName });
app.Run();
