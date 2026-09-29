# 第7篇：动态分表与分表路由

> ⏱ 阅读约 12 分钟

当单表数据量达到千万甚至亿级别时，分表是必不可少的优化手段。LiteOrm 通过 `IArged` 接口和 `TableArgs` 机制，提供了原生、优雅的动态分表支持。

## 与其他 ORM 的分表对比

| 维度 | LiteOrm | EF Core | Dapper |
|------|---------|---------|--------|
| 分表方式 | `IArged` 接口 + 表名占位符 | 无原生支持 | 手动拼接表名 |
| 路由逻辑 | 实体属性驱动 | 需第三方库 | 手动实现 |
| 自动化程度 | 插入/查询自动路由 | 无 | 全手动 |
| 跨表查询 | TableArgs 显式指定 | 不支持 | 手动 UNION |
| 多维度分表 | 原生支持 `{0}_{1}` 占位符 | 不支持 | 手动拼接 |

## 基本概念

LiteOrm 的分表机制基于两个核心元素：

1. **表名占位符**：`[Table("Log_{0}")]` 中的 `{0}` 占位符
2. **TableArgs**：替换占位符的运行时参数数组

当执行 SQL 时，`{0}` 会被 `TableArgs[0]` 替换，`{1}` 会被 `TableArgs[1]` 替换，以此类推。

## 入门：按月分表

最常见的分表场景是按时间维度（年、月、日）分表：

```csharp
[Table("Log_{0}")]  // {0} 占位符将被 TableArgs 替换
public class Log : ObjectBase, IArged
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [Column("Content")]
    public string Content { get; set; } = string.Empty;

    [Column("CreateTime")]
    public DateTime CreateTime { get; set; }

    // IArged 接口：自动提供 TableArgs
    string[] IArged.TableArgs => new[] { CreateTime.ToString("yyyyMM") };
}
```

### 插入自动路由

```csharp
// 插入 2024年1月的日志 → 自动写入 Log_202401 表
var log1 = new Log
{
    Content = "用户登录",
    CreateTime = new DateTime(2024, 1, 15)
};
await logService.InsertAsync(log1);

// 插入 2024年2月的日志 → 自动写入 Log_202402 表
var log2 = new Log
{
    Content = "订单创建",
    CreateTime = new DateTime(2024, 2, 20)
};
await logService.InsertAsync(log2);
```

### 查询自动路由

```csharp
// 查询特定月份的数据
var janLogs = await logService.SearchAsync(
    tableArgs: new[] { "202401" }  // 查询 Log_202401 表
);

// 查询当月数据
var currentMonth = DateTime.Now.ToString("yyyyMM");
var thisMonthLogs = await logService.SearchAsync(
    u => u.Content.Contains("错误"),
    tableArgs: new[] { currentMonth }
);
```

## 多维度分表

LiteOrm 支持多维度分表，例如同时按区域和年份分表：

```csharp
[Table("Sales_{0}_{1}")]  // 两个占位符：区域、年份
public class SalesRecord : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("Region")]
    public string Region { get; set; } = string.Empty;  // US, CN, EU...

    [Column("Year")]
    public int Year { get; set; }

    [Column("Amount")]
    public decimal Amount { get; set; }

    // 自动路由到 Sales_US_2025, Sales_CN_2025 等
    string[] IArged.TableArgs => new[] { Region, Year.ToString() };
}
```

使用示例：

```csharp
// 插入到 Sales_US_2025
var sale = new SalesRecord
{
    Region = "US",
    Year = 2025,
    Amount = 10000
};
await salesService.InsertAsync(sale);

// 查询 Sales_CN_2024
var chinaSales = await salesService.SearchAsync(
    tableArgs: new[] { "CN", "2024" }
);
```

## TableArgs 的传递机制

`TableArgs` 的实际传递链路如下，理解它对排查分表路由问题至关重要：

### 源头设置

| 设置方式 | 说明 |
|---------|------|
| `IArged.TableArgs`（实体实现） | 插入/更新时由实体自动提供 |
| `WithArgs(...)`（DAO 层） | 显式为查询指定 TableArgs |
| `From<T>(tableArgs: ...)`（Expr 层） | 在表达式中指定 |
| `CreateSqlBuildContext` 重写 | DAO 层注入运行时参数（如租户路由） |

### 传递与覆盖规则

1. **DAO 入口**：`WithArgs()` 克隆自身并设置 `TableArgs`，传入 `SqlBuildContext`
2. **作用域继承**：`BeginScope()`（如进入子查询）时会从父作用域**继承** `TableArgs`
3. **表达式覆盖**：如果 `TableExpr`（即 `From<T>(tableArgs:...)`）显式设置了 `TableArgs`，在 SQL 生成阶段会**覆盖** `SqlBuildContext` 中的 `TableArgs`——这是最高优先级
4. **ForeignExpr 局部覆盖**：外键子查询（`EXISTS` 等）进入新作用域时继承父级 `TableArgs`，然后被自身显式的 `TableArgs` 覆盖

```csharp
// 方式1：DAO 层 WithArgs / tableArgs 参数显式指定
var logs = await logService.SearchAsync(
    tableArgs: new[] { "202401" }  // 查询 2024年1月
);

// 方式2：IArged 自动提供（插入时自动使用）
var log = new Log { CreateTime = DateTime.Now };
await logService.InsertAsync(log);  // 自动路由到对应月份

// 方式3：Expr 层 From<T>() 中指定（最高优先级，会覆盖上下文）
using static LiteOrm.Common.Expr;
var query = From<LogView>(tableArgs: new[] { "202401" })
    .Where(Prop("Content").Contains("error"));
var result = await logViewDAO.Search(query).ToListAsync();
```

### TableArgs 的值限制

`TableArgs` 的每个参数值会经过正则校验 `^[a-zA-Z0-9_]*$`，只允许**字母、数字和下划线**。传入非法字符（如 `-`、`.`、空格）会抛出 `ArgumentException`。

```csharp
// ❌ 错误：包含非法字符
tableArgs: new[] { "2024-01" }     // '-' 非法
tableArgs: new[] { "tenant a" }    // 空格非法

// ✅ 正确：仅字母、数字、下划线
tableArgs: new[] { "202401" }
tableArgs: new[] { "tenant_a" }
```

## 分表查询的注意事项

### 跨表查询

LiteOrm 不支持一次查询自动跨越多张分表。如果需要跨表查询，需要分别查询后合并：

```csharp
// 查询最近3个月的数据（需要3次查询）
var months = new[] { "202401", "202402", "202403" };
var allLogs = new List<LogView>();

foreach (var month in months)
{
    var logs = await logService.SearchAsync(
        tableArgs: new[] { month }
    );
    allLogs.AddRange(logs);
}
```

### 关联查询中的分表

当分表实体参与关联查询（JOIN）时，`TableArgs` 通过 `SqlBuildContext.BeginScope()` 从父作用域继承。但如果 `TableExpr` 自身显式指定了 `TableArgs`，则会覆盖继承值：

```csharp
using static LiteOrm.Common.Expr;

// DAO 入口指定 TableArgs
var dao = salesViewDAO.WithArgs("US", "2025");

// 查询时 TableArgs 会沿作用域链传递
var query = From<SalesRecordView>()
    .Where(Prop("Amount") >= 10000)
    .OrderBy(Prop("Amount").Desc());

var result = await dao.Search(query).ToListAsync();
```

> 如果某个 `From<T>(tableArgs: ...)` 显式指定了自己的 `TableArgs`，会覆盖上下文中继承的值，可能绕过租户/分片边界，需谨慎使用。

## 分表 + 分库

LiteOrm 支持分表与分库协同工作：

```csharp
// 按租户分库
[Table("Orders_{0}", DataSource = "MainDb")]  // 租户在 MainDb 中
public class TenantOrder : ObjectBase
{
    // ...
}

// 日志按租户分表，但存在日志专用库
[Table("Logs_{0}_{1}", DataSource = "LogDb")]
public class TenantLog : ObjectBase, IArged
{
    [Column("TenantCode")]
    public string TenantCode { get; set; } = string.Empty;

    [Column("CreateTime")]
    public DateTime CreateTime { get; set; }

    string[] IArged.TableArgs => new[] { TenantCode, CreateTime.ToString("yyyyMM") };
}
```

## 运行时动态路由

对于不实现 `IArged` 的实体，或者需要在运行时动态决定路由的场景：

```csharp
// 在 DAO 层重写 CreateSqlBuildContext 实现动态路由
public class CustomLogViewDAO : ObjectViewDAO<Log>
{
    private readonly ITenantProvider _tenantProvider;

    public CustomLogViewDAO(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    public override SqlBuildContext CreateSqlBuildContext(bool initTable = false)
    {
        var context = base.CreateSqlBuildContext(initTable);
        // 动态注入当前租户的路由参数
        context.TableArgs = new[] { _tenantProvider.CurrentTenantCode };
        return context;
    }
}
```

这种方式适合多租户场景，租户路由信息从请求上下文中获取，DAO 层自动注入。

## 与其他 ORM 的深度对比

### 分表能力对比

| 能力 | LiteOrm | EF Core | SqlSugar | FreeSql |
|------|---------|---------|----------|---------|
| 声明式分表 | `IArged` + `{0}` | 无 | 内置分表 | 内置分表 |
| 自动化路由 | 插入/查询自动 | 手动 | 自动 | 自动 |
| 多维度分表 | `{0}_{1}` 原生 | 无 | 支持 | 支持 |
| 分库+分表 | 原生支持 | 需多DbContext | 支持 | 支持 |
| 代码侵入性 | 低（接口+特性） | 高 | 中 | 中 |

### 示例对比

```csharp
// === LiteOrm ===
[Table("Log_{0}")]
public class Log : ObjectBase, IArged
{
    string[] IArged.TableArgs => new[] { CreateTime.ToString("yyyyMM") };
}
await logService.InsertAsync(log); // 自动路由

// === SqlSugar ===
[SugarTable("Log_{year}{month}")]
public class Log { }
db.Insertable(log).SplitTable().ExecuteCommand();

// === EF Core ===
// 无原生分表支持，需要手动拼接表名或使用第三方库
var tableName = $"Log_{DateTime.Now:yyyyMM}";
var sql = $"INSERT INTO {tableName} ...";
```

## 最佳实践

1. **分表字段参与 IArged**：让分表路由字段在实体上可见，便于调试
2. **分表名使用有意义的前缀**：`Log_202401` 而非 `202401`
3. **跨表查询时注意性能**：多个分表查询需要分别执行，注意总耗时
4. **分表 + 分库配合使用**：大租户独立库，小租户共享库+分表
5. **TableArgs 参数需要验证**：确保传入的参数合法，避免表名注入
6. **做好分表管理**：定期创建未来月份的表，定期归档历史数据

## 下一篇预告

[第8篇：CTE、窗口函数与高级查询](../series/08-advanced-queries.md) — 探索公共表表达式（CTE）、窗口函数、UNION 等高级查询能力。