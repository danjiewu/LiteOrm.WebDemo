# 第8篇：CTE、窗口函数与高级查询

> ⏱ 阅读约 15 分钟

在实际业务中，简单的 CRUD 往往无法满足复杂的数据分析需求。LiteOrm 的 Expr 表达式系统支持 CTE（公共表表达式）、窗口函数、UNION 等高级 SQL 特性，让你在不手写原生 SQL 的情况下完成复杂查询。

## CTE（公共表表达式）

CTE 允许你定义临时结果集，在后续查询中复用。LiteOrm 通过 `SelectExpr.With(name)` 提供原生 CTE 支持。

### 基础 CTE

```csharp
using static LiteOrm.Common.Expr;

// 定义 CTE：成年用户
var cteDef = From<User>()
    .Where(Prop("Age") >= 18)
    .Select(
        Prop("Id").As("Id"),
        Prop("UserName").As("Name"),
        Prop("Age").As("Age")
    );

// 在 CTE 基础上查询
var query = cteDef.With("AdultUsers")
    .Where(Prop("Age") >= 25)
    .OrderBy(Prop("Name").Asc())
    .Select(Prop("Name"), Prop("Age"));

var result = await dataViewDAO.Search(query).GetResultAsync();
```

生成的 SQL：

```sql
WITH AdultUsers AS (
    SELECT Id, UserName AS Name, Age
    FROM Users
    WHERE Age >= 18
)
SELECT Name, Age
FROM AdultUsers
WHERE Age >= 25
ORDER BY Name ASC
```

### CTE 联合 UNION

同一个 CTE 可以在 UNION 两侧复用：

```csharp
using static LiteOrm.Common.Expr;

// 定义 CTE
var adultUsers = From<User>()
    .Where(Prop("Age") >= 18)
    .Select(
        Prop("UserName").As("Name"),
        Prop("Age").As("Age")
    )
    .With("AdultUsers");

// 使用 UNION ALL 合并两个年龄段
var query = adultUsers
    .Where(Prop("Age") < 30)
    .Select(Prop("Name"), Prop("Age"), Const("18-29").As("AgeGroup"))
    .UnionAll(
        adultUsers
            .Where(Prop("Age") >= 30)
            .Select(Prop("Name"), Prop("Age"), Const("30+").As("AgeGroup"))
    );

var result = await dataViewDAO.Search(query).GetResultAsync();
```

生成的 SQL：

```sql
WITH AdultUsers AS (
    SELECT UserName AS Name, Age
    FROM Users
    WHERE Age >= 18
)
SELECT Name, Age, '18-29' AS AgeGroup
FROM AdultUsers
WHERE Age < 30
UNION ALL
SELECT Name, Age, '30+' AS AgeGroup
FROM AdultUsers
WHERE Age >= 30
```

### CTE 校验规则

同别名 CTE 会进行校验：
- 定义完全相同时：自动去重，只保留第一个
- 定义不同时：抛出异常（防止歧义）

## 窗口函数

窗口函数允许在结果集的行子集上进行计算，而不会像 GROUP BY 那样合并行。LiteOrm 通过 `Func("聚合函数", ...).Over(...)` API 支持窗口函数。

### 基础窗口函数

```csharp
using static LiteOrm.Common.Expr;

// ROW_NUMBER() OVER (PARTITION BY DeptId ORDER BY Salary DESC)
var query = From<UserView>()
    .Select(
        Prop("Id"),
        Prop("UserName"),
        Prop("DeptId"),
        Prop("Salary"),
        Func("ROW_NUMBER")
            .Over(
                new ValueTypeExpr[] { Prop("DeptId") },
                Prop("Salary").Desc()
            )
            .As("SalaryRank")
    );

var result = await dataViewDAO.Search(query).GetResultAsync();
```

### 常用窗口函数

```csharp
using static LiteOrm.Common.Expr;

// 仅分区：SUM(Amount) OVER (PARTITION BY ProductId)
var productTotal = Func("SUM", Prop("Amount"))
    .Over(Prop("ProductId"))
    .As("ProductTotal");

// 分区 + 排序：RANK() OVER (PARTITION BY Region ORDER BY Amount DESC)
var regionalRank = Func("RANK")
    .Over(
        new ValueTypeExpr[] { Prop("Region") },
        Prop("Amount").Desc()
    )
    .As("RegionalRank");

// 全表排序：DENSE_RANK() OVER (ORDER BY Amount DESC)
var globalRank = Func("DENSE_RANK")
    .Over(
        Array.Empty<ValueTypeExpr>(),
        Prop("Amount").Desc()
    )
    .As("GlobalRank");

// 含 ROWS 帧的窗口：AVG(Amount) OVER (ORDER BY CreateTime ROWS BETWEEN 3 PRECEDING AND CURRENT ROW)
var movingAvg = Func("AVG", Prop("Amount"))
    .Over(
        Array.Empty<ValueTypeExpr>(),
        new OrderByItemExpr[] { Prop("CreateTime").Asc() },
        range: false,   // false = ROWS, true = RANGE
        begin: -3,      // 负数 = PRECEDING
        end: 0          // 0 = CURRENT ROW
    )
    .As("MovingAvg");
```

### Over 方法签名

```csharp
// 仅分区
FunctionExpr Over(this FunctionExpr func, params ValueTypeExpr[] partitionBy)

// 分区 + 排序
FunctionExpr Over(this FunctionExpr func, ValueTypeExpr[] partitionBy, params OrderByItemExpr[] orderBy)

// 分区 + 排序 + ROWS/RANGE 帧
// begin/end: 负数=PRECEDING, 0=CURRENT ROW, 正数=FOLLOWING, null=UNBOUNDED
FunctionExpr Over(this FunctionExpr func, ValueTypeExpr[] partitionBy, OrderByItemExpr[] orderBy, bool range, int? begin, int? end)
```

### 完整示例：销售排行榜

```csharp
using static LiteOrm.Common.Expr;

var query = From<SalesRecordView>()
    .Select(
        Prop("Region"),
        Prop("Year"),
        Prop("Month"),
        Prop("Amount"),
        Func("RANK")
            .Over(
                new ValueTypeExpr[] { Prop("Region") },
                Prop("Amount").Desc()
            )
            .As("RegionalRank"),
        Func("DENSE_RANK")
            .Over(
                Array.Empty<ValueTypeExpr>(),
                Prop("Amount").Desc()
            )
            .As("GlobalRank")
    )
    .Where(Prop("Year") == 2025)
    .OrderBy(Prop("Region").Asc(), Prop("Amount").Desc());

var result = await dataViewDAO.Search(query).GetResultAsync();
```

生成的 SQL 类似：

```sql
SELECT Region, Year, Month, Amount,
       RANK() OVER (PARTITION BY Region ORDER BY Amount DESC) AS RegionalRank,
       DENSE_RANK() OVER (ORDER BY Amount DESC) AS GlobalRank
FROM SalesRecords
WHERE Year = 2025
ORDER BY Region ASC, Amount DESC
```

### 扩展窗口函数（Lambda 风格）

如果需要通过 Lambda 使用窗口函数（如 `s.Amount.SumOver(...)`），需要实现自定义扩展方法并注册处理器。LiteOrm 的 Demo 项目中提供了 `WindowFunctionExtensions` 参考实现，核心思路是将 C# 方法调用转为 `FunctionExpr("Over", ...)`。

## 高级选择与投影

### CASE WHEN

CASE WHEN 通过 `Expr.Case()` 或 `Expr.If()` 静态方法构建：

```csharp
using static LiteOrm.Common.Expr;

// 多分支：Expr.Case 静态方法（元组数组形式）
var query = From<UserView>()
    .Select(
        Prop("Id"),
        Prop("UserName"),
        Expr.Case(
            (Prop("Age") < 18, Const("未成年")),
            (Prop("Age") < 60, Const("成年")),
            elseExpr: Const("老年")
        ).As("AgeGroup")
    );

// 单条件：Expr.If 快捷方式
var vipTag = Expr.If(Prop("Amount") > 1000, Const("VIP"), Const("Normal")).As("VipTag");

// 参数展开形式（params 元组）
var level = Expr.Case(
    (Prop("Score") >= 90, Const("A")),
    (Prop("Score") >= 80, Const("B")),
    (Prop("Score") >= 60, Const("C")),
    Const("D")  // 最后一个无匹配 LogicExpr 则作为 ELSE
).As("Level");
```

### 表达式计算

```csharp
using static LiteOrm.Common.Expr;

// 在 SELECT 中做计算
var query = From<UserView>()
    .Select(
        Prop("Id"),
        Prop("UserName"),
        // 计算全名（字符串拼接请用 Concat，避免手写 Expr 的 `+` 生成不兼容 SQL）
        Prop("FirstName")
            .Concat(" ")
            .Concat(Prop("LastName"))
            .As("FullName"),
        // 计算税后工资
        (Prop("Salary") * Const(0.8m)).As("AfterTaxSalary"),
        // 条件计算
        Expr.If(Prop("Salary") > 10000, Prop("Salary") * Const(0.7m), Prop("Salary") * Const(0.8m))
            .As("NetSalary")
    );
```

## 聚合查询

### 基础聚合

```csharp
using static LiteOrm.Common.Expr;

// SELECT 中使用聚合函数
var query = From<UserView>()
    .GroupBy(Prop("DeptId"))
    .Select(
        Prop("DeptId"),
        Prop("Id").Count().As("UserCount"),
        Prop("Age").Avg().As("AvgAge"),
        Prop("Age").Max().As("MaxAge"),
        Prop("Age").Min().As("MinAge"),
        Prop("Salary").Sum().As("TotalSalary")
    );
```

### HAVING 过滤

```csharp
using static LiteOrm.Common.Expr;

var query = From<UserView>()
    .GroupBy(Prop("DeptId"))
    .Select(
        Prop("DeptId"),
        Prop("Id").Count().As("UserCount")
    )
    .Having(Prop("UserCount") > 5);  // 只显示人数大于5的部门
```

## UNION 查询

```csharp
using static LiteOrm.Common.Expr;

// UNION ALL：合并活跃用户和VIP用户
var activeUsers = From<UserView>()
    .Where(Prop("Status") == 1)
    .Select(Prop("Id"), Prop("UserName"), Const("Active").As("Type"));

var vipUsers = From<UserView>()
    .Where(Prop("IsVip") == true)
    .Select(Prop("Id"), Prop("UserName"), Const("VIP").As("Type"));

var query = activeUsers.UnionAll(vipUsers)
    .OrderBy(Prop("UserName").Asc());
```

## 与 EF Core 的高级查询对比

| 能力 | LiteOrm | EF Core |
|------|---------|---------|
| CTE | `SelectExpr.With(name)` 原生支持 | `FromSqlRaw` 手写 SQL |
| 窗口函数 | Expr 扩展支持 | `FromSqlRaw` 手写 SQL |
| UNION | `UnionAll()` 链式 API | `Concat().Union()` |
| CASE WHEN | `Expr.If` / `Expr.Case` 静态方法 | 三元表达式转换 |
| 复杂聚合 | 链式 API | LINQ GroupBy + Select |

EF Core 对于一些高级 SQL 特性（CTE、窗口函数）需要退回到手写原生 SQL，而 LiteOrm 通过 Expr 系统保持了统一的编程体验。

## 最佳实践

1. **CTE 适合复杂的分层查询**：先定义 CTE 再在其上查询，比嵌套子查询更清晰
2. **窗口函数适合排名和累计计算**：避免了自连接或复杂的子查询
3. **聚合查询用 DataViewDAO**：返回 `DataTable` 比强类型实体更灵活
4. **UNION 注意去重**：`UnionAll` 不去重（性能更好），`Union` 去重
5. **复杂查询注意索引**：窗口函数的 PARTITION BY 和 ORDER BY 列应有索引

## 下一篇预告

[第9篇：扩展性](../series/09-extensibility.md) — 自定义表达式扩展、SQL 函数注册、多数据库方言适配。