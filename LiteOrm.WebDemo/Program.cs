using LiteOrm.Remote.Server;
using LiteOrm.DependencyInjection;
using LiteOrm.SqlToExpr;
using LiteOrm.WebDemo.Data;
using LiteOrm.WebDemo.Endpoints;
using LiteOrm.WebDemo.Services;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Host.RegisterLiteOrm();
builder.Services.AddRemoteServer()
.AddMemoryCache()
.AddSingleton<SqlConversionService>()
.AddSingleton(_ =>
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

// 文档链接以 .md 形式对外，实际交给 link.html 的文档区渲染，避免浏览器里直接看到裸 Markdown
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        await next();
        return;
    }

    var requestPath = context.Request.Path.Value;
    if (string.IsNullOrEmpty(requestPath))
    {
        await next();
        return;
    }

    // docs.html 已合并进 link.html，旧地址按文档深链重定向
    if (requestPath.Equals("/docs.html", StringComparison.OrdinalIgnoreCase))
    {
        var query = context.Request.Query;
        var legacyPath = query["path"].ToString();
        var legacyLang = string.Equals(query["lang"].ToString(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        context.Response.Redirect(string.IsNullOrEmpty(legacyPath)
            ? "/link.html"
            : BuildDocsUrl(legacyPath, legacyLang));
        return;
    }

    if (!requestPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var docPath = Uri.UnescapeDataString(requestPath).Replace('\\', '/').Trim('/');
    docPath = docPath[..^3];
    var lang = "zh";
    if (docPath.EndsWith(".en", StringComparison.OrdinalIgnoreCase))
    {
        docPath = docPath[..^3];
        lang = "en";
    }

    if (docPath.Length == 0)
    {
        await next();
        return;
    }

    context.Response.Redirect(BuildDocsUrl(docPath, lang));
});

static string BuildDocsUrl(string docPath, string lang)
    => $"/{(lang == "en" ? "link.en.html" : "link.html")}#/docs?path={Uri.EscapeDataString(docPath)}";

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
