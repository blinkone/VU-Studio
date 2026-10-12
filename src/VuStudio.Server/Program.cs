var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "VU Studio");
app.Run();
