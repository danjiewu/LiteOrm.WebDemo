# 第11篇：日志诊断与权限过滤

> ⏱ 阅读约 15 分钟

生产环境中，日志诊断和权限控制是保障系统可观测性和安全性的基石。LiteOrm 通过 AOP 拦截器在 Service 层提供了完善的日志、诊断和异常处理能力，同时通过 Expr 表达式层提供了灵活的权限过滤机制。

## 日志体系

LiteOrm 基于 `Microsoft.Extensions.Logging` 输出日志，与宿主应用的日志管道无缝集成。

### 启用日志

```csharp
// ASP.NET Core
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Host.RegisterLiteOrm();

// Console
var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .RegisterLiteOrm()
    .Build();
```

### ServiceLog 特性

`[ServiceLog]` 控制 Service 方法级别的日志输出：

```csharp
// 基础用法：默认 Information + Full
[ServiceLog]
public interface IUserService
{
    Task<User?> GetByIdAsync(int id);
}

// 自定义日志级别和格式
[ServiceLog(LogLevel = ServiceLogLevel.Debug, LogFormat = LogFormat.Args)]
public interface IOrderService
{
    Task<Order?> GetAsync(int id);
    Task<List<Order>> SearchAsync(string keyword);
}

// 方法级别覆盖
public interface IAccountService
{
    [ServiceLog(LogLevel = ServiceLogLevel.Warning, LogFormat = LogFormat.Full)]
    Task<bool> TransferAsync(long fromId, long toId, decimal amount);

    [ServiceLog(LogLevel = ServiceLogLevel.None)]  // 关闭日志
    Task<string> GetHealthAsync();
}
```

### LogLevel 与 LogFormat

| 参数 | 选项 | 说明 |
|------|------|------|
| LogLevel | `Trace / Debug / Information / Warning / Error / Critical / None` | 日志级别 |
| LogFormat | `None` | 不记录 |
| LogFormat | `Args` | 只记录入参 |
| LogFormat | `ReturnValue` | 只记录返回值 |
| LogFormat | `Full` | 记录入参和返回值 |

### Log 特性：敏感数据脱敏

`[Log]` 特性用于控制哪些数据可以进入日志：

```csharp
// 1. 方法参数脱敏
public interface IAuthService
{
    Task<LoginResult> LoginAsync(
        string userName,
        [Log(false)] string password  // 密码不进入日志
    );
}

// 2. 实体属性脱敏
[Table("Users")]
public class User : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("UserName")]
    public string UserName { get; set; } = string.Empty;

    [Column("PasswordHash")]
    [Log(false)]  // 密码哈希不进入日志
    public string PasswordHash { get; set; } = string.Empty;

    [Column("IdCardNo")]
    [Log(false)]  // 身份证号不进入日志
    public string IdCardNo { get; set; } = string.Empty;
}

// 3. 自定义复杂对象的日志表现
public class PaymentRequest : ILogable
{
    public string CardNo { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    public string ToLog()
    {
        // 仅显示卡号后4位
        var tail = CardNo.Length >= 4 ? CardNo[^4..] : CardNo;
        return $"CardNo:****{tail}, Amount:{Amount}";
    }
}
```

## 慢查询诊断

LiteOrm 内置慢查询检测，当调用耗时超过阈值时自动输出警告：

```csharp
// 配置慢查询阈值（默认1秒）
ServiceInvokeInterceptor.SlowQueryThreshold = TimeSpan.FromSeconds(1);

// 配置集合参数展开上限（防止日志过大）
ServiceInvokeInterceptor.MaxExpandedLogLength = 20;
```

当查询超过阈值时，日志中会输出 `<Slow>` 和 `<SlowSQL>` 标记，包含具体的方法名和 SQL 语句。

## 异常处理与 ExceptionHook

`[ExceptionHook]` 允许在 Service 方法抛出异常时插入自定义处理逻辑：

### 定义 Hook

```csharp
[AutoRegister(Lifetime.Scoped, typeof(IServiceExceptionHook))]
public class OrderExceptionHook : IServiceExceptionHook
{
    public void OnException(ServiceExceptionContext context)
    {
        // 可获取的信息：
        // context.Exception      - 原始异常
        // context.ServiceName    - 当前服务名
        // context.MethodName     - 当前方法名
        // context.Arguments      - 原始参数
        // context.SessionID      - 会话ID
        // context.SqlStack       - 当前SQL栈

        // 发送告警
        _alertService.SendAlert($"Order service error: {context.Exception.Message}");
    }
}
```

### 使用 Hook

```csharp
// 通知模式：只观察，不吞异常
[ExceptionHook(typeof(OrderExceptionHook), Mode = ServiceExceptionHookMode.Notify)]
public interface IOrderService
{
    Task SubmitAsync(long id);
}

// 处理模式：允许将异常转换为正常返回值
[ExceptionHook(typeof(FallbackHook), Mode = ServiceExceptionHookMode.Handle)]
public async Task<int> GetOrderCount()
{
    throw new InvalidOperationException("DB unavailable");
}

public class FallbackHook : IServiceExceptionHook
{
    public void OnException(ServiceExceptionContext context)
    {
        context.Handle(0);  // 异常时返回0，而不是抛出
    }
}
```

### Hook 模式对比

| 模式 | 含义 | 适用场景 |
|------|------|---------|
| `Notify` | 只通知，异常继续抛出 | 告警、埋点、补充日志 |
| `Handle` | 可以调用 `context.Handle()` 吞掉异常 | 降级返回、容错处理 |

## 权限过滤

权限过滤是保护数据安全的关键。LiteOrm 提供了三种过滤方式。

### 方式1：运行时 Expr 过滤（推荐）

```csharp
using static LiteOrm.Common.Expr;

public async Task<List<OrderView>> GetMyOrders(int userId)
{
    // 构建业务条件
    var filter = BuildBusinessFilter(request);

    // 叠加软删除
    filter &= Prop("IsDeleted") == false;

    // 叠加当前用户过滤
    filter &= Prop("CreatedByUserId") == userId;

    return await orderService.SearchAsync(
        From<OrderView>()
            .Where(filter)
            .OrderBy(Prop("CreatedTime").Desc())
            .Section(0, 20)
    );
}
```

### 方式2：Column.Constant（固定过滤）

```csharp
[Table("Departments")]
public class Department : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("State", Constant = 1)]  // 始终只查 State=1 的记录
    public int State { get; set; }
}

// 所有查询自动带上 State = 1 条件
// 包括 JOIN、EXISTS 子查询、UPDATE、DELETE
```

### 方式3：GenericSqlExpr（从用户上下文取值）

```csharp
using static LiteOrm.Common.Expr;

// 注册可复用的当前用户过滤
GenericSqlExpr.Register("CurrentUserFilter", (context, sqlBuilder, outputParams, _) =>
{
    var currentUser = UserContext.Current;
    string paramName = outputParams.Count.ToString();
    outputParams.Add(new(sqlBuilder.ToParamName(paramName), currentUser.Id));
    return $"{sqlBuilder.ToSqlName("CreatedByUserId")} = {sqlBuilder.ToSqlParam(paramName)}";
});

// 使用
var filter = BuildBusinessFilter(request)
    & (Prop("IsDeleted") == false)
    & Expr.Sql("CurrentUserFilter");
```

### 三种方式对比

| 方式 | 适用场景 | 优点 | 限制 |
|------|---------|------|------|
| 运行时 Expr | 当前用户、当前请求 | 直观、灵活 | 需要在查询入口统一拼装 |
| `Column.Constant` | 固定状态、固定分区 | 自动注入，全局生效 | 不适合动态值 |
| `GenericSqlExpr` | 可复用的过滤规则 | 封装性好，参数化安全 | 需要预注册 |

## SQL 注入防护

LiteOrm 从架构层面内置了多层防护：

| 层次 | 机制 | 说明 |
|------|------|------|
| 参数化 SQL | `outputParams` + 占位符 | 所有用户值以参数传递 |
| LIKE 转义 | 通配符转义 + `ESCAPE` | `%` 和 `_` 自动转义 |
| 表达式验证 | `ExprValidator` | 限制表达式类型和函数范围 |
| `ExprString` 自动参数化 | 插值字符串处理器 | 非 Expr 值自动参数化 |

```csharp
// 自动参数化
var user = await userService.SearchOneAsync(u => u.UserName == "O'Brien");
// SQL: SELECT * FROM Users WHERE UserName = @0
// 参数: @0 = "O'Brien"（安全）

// LIKE 自动转义
var results = await userService.SearchAsync(u => u.UserName.Contains("100%"));
// SQL: WHERE UserName LIKE @0 ESCAPE '/'
// 参数: @0 = "%100/%%"（%被转义）
```

## 生产环境配置清单

| 配置项 | 建议值 | 说明 |
|--------|--------|------|
| `ServiceLog` | 业务服务接口标记 `Information` | 关键操作可审计 |
| 敏感字段 | 全部标记 `[Log(false)]` | 密码、身份证、手机号等 |
| 慢查询阈值 | 1-3秒 | 根据业务SLA调整 |
| `ExprValidator` | `CreateQueryOnly()` + `AllowRegisted` | 前端查询必须验证 |
| 权限过滤 | 查询入口统一拼装 | 叠加在业务条件之后 |

## 与 EF Core 的日志诊断对比

| 能力 | LiteOrm | EF Core |
|------|---------|---------|
| 方法级日志 | `[ServiceLog]` AOP 特性 | 需手动 `ILogger` 打印 |
| 参数脱敏 | `[Log(false)]` 声明式 | 需手动处理 |
| 慢查询 | `SlowQueryThreshold` 配置 | `EnableSensitiveDataLogging` |
| 异常钩子 | `ExceptionHook` 内建 | 需自建拦截器 |
| SQL 注入防护 | 多层内建 | 参数化 + 需手动防 LIKE 注入 |

## 最佳实践

1. **日志放在 Service 边界**：不要在每个方法里零散打印，用 `[ServiceLog]` 统一管理
2. **敏感数据必须脱敏**：密码、密钥、身份证、手机号，一律 `[Log(false)]`
3. **开发阶段用 Debug + Full，生产用 Information**
4. **权限过滤在查询入口统一拼装**：不要散落在多个方法中
5. **前端提交的 Expr 必须通过验证器**
6. **不要用 `ConstFilter` 承载当前用户/租户**：它只适合固定不变的规则

## 下一篇预告

[第12篇：性能对比与总结](../series/12-performance-and-summary.md) — 完整的性能基准测试数据、与 EF Core/Dapper/SqlSugar/FreeSql 的多维度对比，以及 LiteOrm 的适用场景总结。