# 第4篇：Expr 表达式系统

> ⏱ 阅读约 20 分钟

如果说 Lambda 是 LiteOrm 的"静态类型"查询方式，那么 **Expr 表达式系统**就是它的"动态灵魂"。这是 LiteOrm 区别于其他 .NET ORM 最核心的创新之一。

## 为什么需要 Expr？

Lambda 表达式虽然类型安全，但有一个致命限制：**无法在运行时动态构建**。考虑以下场景：

```csharp
// 用户在前端勾选了多个筛选条件，后端需要动态拼接 WHERE 子句
// 如果只有 Lambda，你只能写死所有可能的组合，或者手动拼接 SQL 字符串
```

Expr 解决了这个问题。它通过**表达式树**的形式，让你在运行时像搭积木一样构建查询条件，而且可以被序列化为 JSON 在前后端传递。

## 与 Lambda 的定位对比

| 特性 | Lambda | Expr |
|------|--------|------|
| 类型安全 | 编译时检查 | 运行时（属性名是字符串） |
| 动态构建 | 不支持 | 原生支持 |
| 可序列化 | 不可 | 可序列化为 JSON |
| 前后端传递 | 不可 | 可通过 JSON 传递 |
| 代码可读性 | 高 | 中等 |
| 灵活度 | 低 | 极高 |

**最佳实践：固定条件用 Lambda，动态条件用 Expr，两者可以混合使用。**

## 快速入门：静态引入

```csharp
using static LiteOrm.Common.Expr;
```

这行代码是使用 Expr 的标配，它引入了 `Prop()`、`Const()`、`From<T>()`、`Lambda<T>()` 等静态工厂方法。

## 核心概念：表达式类型

LiteOrm 的 Expr 系统包含以下核心表达式类型：

| 类型 | 说明 | 示例 |
|------|------|------|
| `PropertyExpr` | 属性引用 | `Prop("Age")` |
| `ValueExpr` | 常量值 | `new ValueExpr(18)` 或 `Const(18)` |
| `LogicBinaryExpr` | 逻辑比较 | `Prop("Age") >= 18` |
| `AndExpr` / `OrExpr` | 逻辑组合 | `expr1 & expr2` 或 `expr1 \| expr2` |
| `NotExpr` | 逻辑取反 | `!expr` |
| `FunctionExpr` | 函数调用 | `new FunctionExpr("YEAR", ...)` |
| `ValueSet` | 值集合（IN） | `Prop("Id").In(1, 2, 3)` |
| `SelectExpr` | SELECT 查询 | `From<T>().Select(...)` |
| `WhereExpr` | WHERE 条件 | `From<T>().Where(expr)` |
| `OrderByExpr` | 排序 | `From<T>().OrderBy(...)` |
| `SectionExpr` | 分页 | `From<T>().Section(0, 20)` |
| `ForeignExpr` | 外键关联 | `ExistsRelated<T>(...)` |
| `GenericSqlExpr` | 自定义SQL | `Expr.Sql("key")` |

## 基础查询构建

### 属性引用与比较

```csharp
using static LiteOrm.Common.Expr;

// Prop("属性名") 引用实体属性
var ageExpr = Prop("Age");           // 属性引用
var nameExpr = Prop("UserName");     // 属性引用

// 比较操作（运算符重载）
var isAdult = Prop("Age") >= 18;
var isAdmin = Prop("UserName") == "admin";
var isActive = Prop("Status") == 1;
var notDeleted = Prop("IsDeleted") == false;
var hasEmail = Prop("Email") != null;

// 组合条件
var filter = (Prop("Age") >= 18) & (Prop("Status") == 1);
// 生成 SQL: WHERE Age >= 18 AND Status = 1

// OR 条件
var special = (Prop("Age") < 18) | (Prop("Age") > 60);
// 生成 SQL: WHERE Age < 18 OR Age > 60

// 复杂组合
var complex = (Prop("Age") >= 18 & Prop("Status") == 1)
            | (Prop("UserName") == "admin");
// 生成 SQL: WHERE (Age >= 18 AND Status = 1) OR UserName = 'admin'
```

#### 字符串拼接：不要用 `+`，用 `.Concat(...)`

在手写 Expr 时，`ValueTypeExpr` 的 `+` 是“加法”语义，最终 SQL 可能生成 `+`，对字符串拼接并不跨数据库可靠（甚至会直接报错）。

推荐显式使用 concat：

```csharp
using static LiteOrm.Common.Expr;

// ✅ 跨数据库字符串拼接：由 SqlBuilder.BuildConcatSql(...) 适配为 CONCAT(...) 或 ||
var fullName = Prop("FirstName")
    .Concat(" ")
    .Concat(Prop("LastName"));

// ❌ 不推荐：手写 Expr 的 + 可能生成 SQL '+'
var risky = Prop("FirstName") + " " + Prop("LastName");
```

> 注意：在 **Lambda** 场景下，C# 的字符串 `+` 会在解析阶段被转换为 concat；但手写 Expr 时请显式使用 `.Concat(...)`。

### 使用 Expr 查询

```csharp
using static LiteOrm.Common.Expr;

// 方式一：直接传入 LogicExpr
var expr = Prop("Age") >= 18 & Prop("Status") == 1;
var users = await userService.SearchAsync(expr);

// 方式二：使用链式 From<T>() API
var query = From<UserView>()
    .Where(Prop("Age") >= 18 & Prop("Status") == 1)
    .OrderBy(Prop("CreateTime").Desc())
    .Section(0, 20);
var users = await userService.SearchAsync(query);
```

## 动态条件构建

Expr 的真正威力在于运行时动态组合：

```csharp
using static LiteOrm.Common.Expr;

public async Task<List<UserView>> SearchUsers(string? keyword, int? minAge, int? maxAge, int? status)
{
    LogicExpr? filter = null;

    // 按关键词搜索
    if (!string.IsNullOrEmpty(keyword))
    {
        filter &= Prop("UserName").Contains(keyword)
                | Prop("Email").Contains(keyword);
    }

    // 年龄范围
    if (minAge.HasValue)
        filter &= Prop("Age") >= minAge.Value;

    if (maxAge.HasValue)
        filter &= Prop("Age") <= maxAge.Value;

    // 状态筛选
    if (status.HasValue)
        filter &= Prop("Status") == status.Value;

    // 构建查询
    var query = From<UserView>()
        .Where(filter)  // Where 接受 null，无过滤条件时不追加 WHERE 子句
        .OrderBy(Prop("CreateTime").Desc());

    return await userService.SearchAsync(query);
}
```

这种模式在后台管理系统的列表筛选、报表查询等场景中极为常见。

## 常用操作

### IN 查询

```csharp
using static LiteOrm.Common.Expr;

// 单个值
var expr = Prop("Id").In(1, 2, 3, 5, 8);

// 数组
int[] ids = { 1, 2, 3 };
var expr2 = Prop("Id").In(ids);

// 列表
var idList = new List<int> { 10, 20, 30 };
var expr3 = Prop("Id").In(idList);

// 生成 SQL: WHERE Id IN (1, 2, 3, 5, 8)
```

### LIKE 查询

```csharp
using static LiteOrm.Common.Expr;

// Contains → LIKE '%xxx%'
var like = Prop("UserName").Contains("admin");

// StartsWith → LIKE 'xxx%'
var startsWith = Prop("UserName").StartsWith("A");

// EndsWith → LIKE '%xxx'
var endsWith = Prop("Email").EndsWith("@gmail.com");

// 组合
var filter = Prop("UserName").Contains("test")
           & Prop("Email").EndsWith("@example.com");
```

### NULL 判断

```csharp
using static LiteOrm.Common.Expr;

// IS NULL
var isNull = Prop("Email") == null;

// IS NOT NULL
var isNotNull = Prop("Email") != null;

// 或者使用显式方法
var isNull2 = Prop("Email").IsNull();
var isNotNull2 = Prop("Email").IsNotNull();
```

### 范围查询

```csharp
using static LiteOrm.Common.Expr;

// 日期范围
var start = new DateTime(2024, 1, 1);
var end = new DateTime(2024, 12, 31);
var dateRange = (Prop("CreateTime") >= start) & (Prop("CreateTime") <= end);

// 数值范围
var ageRange = (Prop("Age") >= 18) & (Prop("Age") <= 60);
```

### 取反

```csharp
using static LiteOrm.Common.Expr;

// NOT 取反
var notAdmin = !(Prop("UserName") == "admin");
// 等价于
var notAdmin2 = Prop("UserName") != "admin";
```

## 链式 API：From<T>()

`From<T>()` 是 LiteOrm 的链式查询构建器，它可以逐段构建完整的 SELECT 语句：

```csharp
using static LiteOrm.Common.Expr;

// 完整查询链
var query = From<UserView>()           // FROM Users
    .Where(Prop("Age") >= 18)           // WHERE Age >= 18
    .OrderBy(Prop("CreateTime").Asc())  // ORDER BY CreateTime ASC
    .Section(0, 20);                    // LIMIT 20 OFFSET 0

var users = await userService.SearchAsync(query);
```

### 分组查询

```csharp
using static LiteOrm.Common.Expr;

var query = From<UserView>()
    .Where(Prop("Age") >= 18)
    .GroupBy(Prop("Status"))
    .Having(Prop("Count") > 5)
    .OrderBy(Prop("Status").Asc());
```

### 指定返回列

```csharp
using static LiteOrm.Common.Expr;

// 只选择需要的列
var query = From<UserView>()
    .Where(Prop("Age") >= 18)
    .Select(
        Prop("Id").As("Id"),
        Prop("UserName").As("Name"),
        Prop("Age").As("Age")
    );
```

## 子查询与 EXISTS

### EXISTS 子查询

```csharp
using static LiteOrm.Common.Expr;

// 查询有订单的用户
var hasOrders = Exists<Order>(o => o.UserId == u.Id);
var result = await userService.SearchAsync(
    q => q.Where(u => hasOrders.To<bool>())
);

// 或者用 Expr 写法
var hasOrderExpr = ExistsRelated<Order>(
    Prop("Status") != "Completed"
);
var users = await userService.SearchAsync(hasOrderExpr);
```

### 子查询

```csharp
using static LiteOrm.Common.Expr;

// 查询年龄大于平均年龄的用户
var avgAgeSubQuery = From<UserView>()
    .Select(Prop("Age").Avg().As("AvgAge"));

// 更多子查询能力请参考第8篇 CTE 与高级查询
```

## Lambda 与 Expr 的混合使用

这是 LiteOrm 最灵活的特性之一。你可以在 Lambda 中嵌入 Expr，也可以将 Lambda 转为 Expr。

### 在 Lambda 中嵌入 Expr

```csharp
using static LiteOrm.Common.Expr;

// 动态构建 Expr 条件
LogicExpr? filter = null;
if (!string.IsNullOrEmpty(keyword))
    filter &= Prop("UserName").Contains(keyword);
if (minAge.HasValue)
    filter &= Prop("Age") >= minAge.Value;

// 在 Lambda 中使用 To<bool>() 嵌入 Expr
var users = await userService.SearchAsync(
    u => u.Status == 1 && filter.To<bool>()  // 混合！
);
```

`To<bool>()` 是 LiteOrm 的 Lambda 解析器识别的特殊标记，在解析阶段会将 Expr 嵌入到 Lambda 的表达式树中。

### 将 Lambda 转为 Expr

```csharp
using static LiteOrm.Common.Expr;

// 固定条件用 Lambda 表达
var baseCondition = Lambda<User>(u => u.Status == 1 && u.Age >= 18);

// 动态条件用 Expr 追加
LogicExpr? extraFilter = null;
if (deptId.HasValue)
    extraFilter &= Prop("DeptId") == deptId.Value;

// 合并
var combined = baseCondition & extraFilter;

var users = await userService.SearchAsync(combined);
```

## ExprString：参数化字符串查询

在 DAO 层，LiteOrm 提供了 `ExprString`（基于 C# 插值字符串处理器），让你可以在需要手写部分 SQL 时仍然保持参数化安全：

```csharp
using static LiteOrm.Common.Expr;

// 在 ObjectViewDAO 中使用
int minAge = 18;
string keyword = "john";

// ExprString 混合：Expr 片段 + 参数化值 + 字面SQL
var users = await userViewDAO.Search(
    $"WHERE {Prop("Age")} > {minAge} AND {Prop("UserName")} LIKE {'%' + keyword + '%'}"
).ToListAsync();

// 在 DataViewDAO 中使用完整 SQL
var result = await dataViewDAO.Search(
    $"SELECT Id, UserName, Email FROM Users WHERE {Prop("Age")} > {minAge}"
).GetResultAsync();
```

`ExprString` 的安全机制：
- `{Prop("Age")}` 等 Expr 对象 → 走完整的表达式树处理，参数化安全
- `{minAge}` 等普通值 → 自动生成参数占位符 `@0`，值加入参数列表
- `"WHERE"`, `"AND"` 等字面量 → 开发者硬编码的 SQL 结构，直接追加

**重要**：`ExprString` 只在 DAO 方法中生效（`Search(...)`、`Count(...)` 等接受 `ExprString` 参数的方法）。普通的 `$"..."` 插值字符串生成的是普通 `string`，不会自动参数化。

## 与其他 ORM 的动态查询对比

### LiteOrm Expr

```csharp
using static LiteOrm.Common.Expr;
LogicExpr? filter = null;
filter &= Prop("Age") >= 18;
filter &= Prop("Status") == 1;
var users = await userService.SearchAsync(filter);
```

### EF Core 动态查询

```csharp
// EF Core 需要手动构建 Expression 树
var parameter = Expression.Parameter(typeof(User), "u");
var ageProp = Expression.Property(parameter, "Age");
var ageConst = Expression.Constant(18);
var ageFilter = Expression.GreaterThanOrEqual(ageProp, ageConst);
// ... 繁琐的 Expression 树构建
```

### SqlSugar 动态查询

```csharp
// SqlSugar 使用自己的表达式构建
var exp = Expressionable.Create<User>();
exp.And(u => u.Age >= 18);
exp.And(u => u.Status == 1);
var users = await db.Queryable<User>().Where(exp.ToExpression()).ToListAsync();
```

LiteOrm 的 Expr 系统更加直观和简洁，而且可以被序列化为 JSON 在前后端传递。

## Expr 的 JSON 序列化

Expr 可以被序列化为 JSON，这是 LiteOrm 前端集成能力的基础：

```csharp
using static LiteOrm.Common.Expr;

// 序列化
var expr = Prop("Age") >= 18 & Prop("Status") == 1;
string json = ExprJsonConvert.Serialize(expr);
// 输出: {"$":"&","Left":{"$":">=","Left":{"#":"Age"},"Right":{"@":18}},"Right":{"$":"==","Left":{"#":"Status"},"Right":{"@":1}}}

// 反序列化
var restored = ExprJsonConvert.Deserialize<Expr>(json);
var users = await userService.SearchAsync(restored);
```

> 前端 Expr 查询的详细用法将在第10篇深入讲解。

## 最佳实践

1. **固定条件用 Lambda，动态条件用 Expr**：保持代码可读性和灵活性
2. **使用 `using static LiteOrm.Common.Expr`**：简化代码
3. **动态构建时，`filter` 初始化为 `null`**：`null & expr = expr` 的性质让链式追加非常方便
4. **`Prop()` 字符串使用 `nameof()` 增强重构安全性**：`Prop(nameof(User.Age))` 比 `Prop("Age")` 更安全
5. **ExprString 只在 DAO 层使用**：Service 层使用 Lambda 或 Expr
6. **生产环境务必启用 ExprValidator**：验证 Expr 类型和函数范围（详见第11篇）
7. **动态构建复杂 Expr 后使用 CycleDetector 自检**：`CycleDetector.HasCycle(expr)` 可在调试/测试阶段快速发现 Source 链回环等引用错误

## 下一篇预告

[第5篇：自动关联查询](../series/05-associations.md) — 通过特性声明式实现 JOIN 查询，告别手写 SQL 关联。