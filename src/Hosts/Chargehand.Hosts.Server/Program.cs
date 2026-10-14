using Chargehand.Kernel;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, KernelJsonContext.Default));

var app = builder.Build();
app.MapGet("/healthz", () => TypedResults.Ok(HostHealth.Ok));
app.Run();
