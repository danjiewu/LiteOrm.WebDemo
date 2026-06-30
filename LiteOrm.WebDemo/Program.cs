using LiteOrm;
using LiteOrm.Remote.Server;
using LiteOrm.Service;
using LiteOrm.SqlToExpr;
using LiteOrm.WebDemo.Data;
using LiteOrm.WebDemo.Endpoints;
using LiteOrm.WebDemo.Services;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Host.RegisterLiteOrm();
builder.Services.AddRemoteService(config => config.ServiceTypeResolver = new DefaultServiceTypeResolver("LiteOrm.WebDemo.Services", "LiteOrm.WebDemo.Models"));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<SqlConversionService>();
builder.Services.AddSingleton(_ =>
{
    var searchPaths = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "DocsContent"),
        Path.Combine(Directory.GetCurrentDirectory(), "LiteOrm.WebDemo", "DocsContent"),
        Path.Combine(Directory.GetCurrentDirectory(), "DocsContent"),
    };
    var path = searchPaths.FirstOrDefault(Directory.Exists) ?? searchPaths[0];
    return new DocsService(path);
});

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider);
}

app.MapDemoEndpoints();
app.MapRemoteInvokeEndpoint();
app.MapSqlToExprEndpoints();
app.MapExprQueryEndpoints();
app.MapDocsEndpoints();
app.MapControllers();

app.Run();
