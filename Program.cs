var options = GatewayConfiguration.Load();
GatewayConfiguration.Validate(options);

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(options.ListenUrl);

var app = builder.Build();
var httpClient = ProxyHttp.CreateClient();

app.MapGet("/health", static () => Results.Text("ok", "text/plain"));

app.MapGet("/v1/models", async (HttpContext context) =>
    await ModelCatalogProxy.HandleAsync(context, options, httpClient));

app.MapPost("/v1/responses", async (HttpContext context) =>
    await ResponsesProxy.HandleAsync(context, options, httpClient));

app.Run();
