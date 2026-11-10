# 第14篇：复杂查询实例

> ⏱ 阅读约 15 分钟

后台列表页是最容易把 ORM 用「重」的地方：一个订单列表，要按部门树过滤、只看最近 7 天、支持三个可选筛选项、点表头排序、分页并显示总数，还要能把当前条件存下来下次接着用。按传统写法，这些需求会被拆成 DTO、Controller 参数、手拼 SQL、两套 count/page 查询。

LiteOrm 的做法是：**把整条查询收进一份 `Expr` JSON**，或者用 Lambda 构造一个 `IQueryable` 直接传给 `Search` / `SearchAsync`。前者适合前端动态组装，后者适合服务端固定条件，两条路径最终生成同一棵 `Expr` 树、同一份 SQL。

本文用一条完整的订单查询把这几件事串起来。

## 一、需求与模型

需求本身很普通：

- 只查指定部门及其所有下级部门的订单，不能先把部门 Id 列表查到内存再拼 `IN`。
- 只查最近 7 天创建的订单，「最近」既可以是相对当前时间的规则，也可以是固定时间窗口。
- 支持状态、最低金额、订单号关键字三个可选条件，条件不存在时 SQL 里不应出现对应片段。
- 列表可按创建时间、金额、状态或订单号排序，方向可选。
- 分页时用同一组条件同时给出总数与当前页数据。
- 查询条件可以保存，之后重新加载继续使用。

模型只需要两张表：

```csharp
[Table("Departments")]
public class Department
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    [Column("ParentId")] public int? ParentId { get; set; }
    [Column("Name")] public string Name { get; set; } = "";
}

[Table("Orders")]
public class SaleOrder
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true)] public int Id { get; set; }
    [Column("OrderNo")] public string OrderNo { get; set; } = "";
    [Column("DeptId")] public int DeptId { get; set; }
    [Column("Status")] public int Status { get; set; }
    [Column("Amount")] public decimal Amount { get; set; }
    [Column("CreatedTime")] public DateTime CreatedTime { get; set; }
}
```

## 二、一份 JSON 就是整条查询

先看完整形态。部门树、最近 7 天、三个可选条件、排序与分页全部在同一份 JSON 里，不需要 `$table`，也不需要 `$select`——表与查询列由 Service 的实体类型补齐：

```json
{
  "$section": {
    "$orderby": {
      "$where": null,
      "Where": {
        "$and": [
          { "$": "in", "Left": { "#": "DeptId" },
            "Right": { "$": "sql", "Key": "DeptSubTree", "Arg": { "@": 1001 } } },
          { "$": ">=", "Left": { "#": "CreatedTime" },
            "Right": { "$": "func", "AddDays": [ { "$": "func", "Now": [] }, { "@": -7 } ] } },
          { "$": "==", "Left": { "#": "Status" }, "Right": { "@": 1 } },
          { "$": ">=", "Left": { "#": "Amount" }, "Right": { "@": 100 } },
          { "$": "contains", "Left": { "#": "OrderNo" }, "Right": { "@": "SO2026" } }
        ]
      }
    },
    "OrderBys": [
      { "Field": { "#": "Amount" }, "Asc": false },
      { "Field": { "#": "Id" }, "Asc": false }
    ]
  },
  "Skip": 20,
  "Take": 20
}
```

反序列化一次，计数取其中的条件，当页使用整条查询：

```csharp
Expr query = JsonSerializer.Deserialize<Expr>(queryJson)!;
LogicExpr? filter = GetFilter(query);

int total = await orderService.CountAsync(filter, cancellationToken: ct);
List<SaleOrder> items = await orderService.SearchAsync(query, cancellationToken: ct);
```

`GetFilter` 沿着 `Source` 链找到第一个 `WhereExpr`：

```csharp
static LogicExpr? GetFilter(Expr? query)
{
    if (query is LogicExpr logic) return logic;

    for (Expr? node = query; node is not null; node = (node as SqlSegment)?.Source)
    {
        if (node is WhereExpr where) return where.Where;
    }
    return null;
}
```

几个关键点：

- `$` 开头的键表示操作符或片段类型，`#` 是列，`@` 是参数；参数最终进入 `context.OutputParams`，不会拼进 SQL 文本。
- 片段按层嵌套：`$section` 包住 `$orderby`，`$orderby` 包住 `$where`；每层的内层片段放在 `$标记` 对应的键上，自己的属性（`Skip` / `Take`、`OrderBys`、`Where`）与标记同级。
- 可选条件不存在时，`$and` 数组里就没有对应节点，SQL 自然不会多出条件。
- 计数只拿 `filter`，当页拿整条 `query`，排序分页不会进入 `COUNT(1)`。

## 三、部门树：一个片段，方言自适应

需求是「指定部门及其所有下级部门」。最直接的想法是先递归查出 Id 列表，再拼 `IN (...)`，但这要多一次查询，也把 Id 数量带进了 SQL。更好的做法是把子树做成一个片段，直接放进 `IN` 的右边：

```csharp
public static class DeptSubTree
{
    public static void Register() =>
        GenericSqlExpr.Register("DeptSubTree", (context, arg) =>
        {
            ValueTypeExpr rootId = arg!.AsValue();
            ISqlBuilder builder = context.SqlBuilder;

            // Oracle 使用层级查询
            if (builder is OracleBuilder)
            {
                string table = builder.ToSqlName(
                    TableInfoProvider.Instance.GetTableDefinition(typeof(Department)).Name!);
                string id = builder.ToSqlName(nameof(Department.Id));
                string parentId = builder.ToSqlName(nameof(Department.ParentId));
                return $"(SELECT {id} FROM {table} START WITH {id} = {rootId.ToSql(context)} " +
                       $"CONNECT BY PRIOR {id} = {parentId})";
            }

            // 其它方言使用递归 CTE
            using (context.BeginScope())
                return $"({BuildRecursiveQuery(rootId).ToSql(context)})";
        });

    static SelectExpr BuildRecursiveQuery(ValueTypeExpr rootId)
    {
        // 锚点：部门自身
        var anchor = Expr.From(typeof(Department))
            .Where(Expr.Prop(nameof(Department.Id)) == rootId)
            .Select(new SelectItemExpr(Expr.Prop(nameof(Department.Id)), nameof(Department.Id)));

        // 递归项：下级部门
        var selfRef = new CommonTableExpr { Alias = "DeptTree" };
        var recursive = Expr.From(typeof(Department))
            .Join(selfRef, Expr.Prop(nameof(Department.ParentId)) == Expr.Prop("DeptTree", nameof(Department.Id)))
            .Select(new SelectItemExpr(Expr.Prop(nameof(Department.Id)), nameof(Department.Id)));

        return anchor.UnionAll(recursive)
            .With("DeptTree")
            .Select(new SelectItemExpr(Expr.Prop(nameof(Department.Id)), nameof(Department.Id)));
    }
}
```

条件里只需引用这个 key：

```csharp
LogicExpr InDeptTree(int deptId)
    => Expr.Prop(nameof(SaleOrder.DeptId)).In(Expr.Sql("DeptSubTree", deptId).AsValue());
```

SQLite / MySQL 8 / PostgreSQL 渲染成递归 CTE：

```sql
"T0"."DeptId" IN (WITH RECURSIVE "DeptTree" AS (SELECT "T1"."Id"
    FROM "Departments" "T1"
    WHERE "T1"."Id" = @0
    UNION ALL SELECT "T2"."Id"
      FROM "Departments" "T2"
      LEFT JOIN "DeptTree" ON "T2"."ParentId" = "DeptTree"."Id")
SELECT "Id"
  FROM "DeptTree")
-- @0 = 1001
```

Oracle 则换成层级查询：

```sql
"T0"."DEPTID" IN (SELECT "ID" FROM "DEPARTMENTS" START WITH "ID" = :0 CONNECT BY PRIOR "ID" = "PARENTID")
-- :0 = 1001
```

JSON 里对应的是 `$sql` 节点：

```json
{ "$": "in", "Left": { "#": "DeptId" },
  "Right": { "$": "sql", "Key": "DeptSubTree", "Arg": { "@": 1001 } } }
```

要点：

- `AsValue()` 不能省：`GenericSqlExpr` 本身是 `LogicExpr`，要放到 `IN` 的右边必须先转成值表达式。
- 方言差异集中在一个注册点：默认实现递归 CTE，Oracle 分支单独输出 `CONNECT BY`。
- 片段只负责「向下找部门」这一种语义；向上找父链、按层聚合应另写 key，不要把多个语义塞进同一个片段。

如果更习惯函数体系，也可以注册成 `FunctionExpr`，通过 `SqlBuilder.RegisterFunctionSqlHandler` 注册默认实现、再按方言覆盖，效果相同。

业务代码里还可以把它写成 Lambda 扩展方法 `o.InDeptSubTree(deptId)`，用 `LambdaExprConverter.RegisterMethodHandler` 把方法调用翻译成同一个片段：

```csharp
public static class OrderExtensions
{
    // 只为让 Lambda 编译通过，永远不会在内存中执行
    public static bool InDeptSubTree(this SaleOrder order, int deptId) => false;
}

LambdaExprConverter.RegisterMethodHandler(
    typeof(OrderExtensions), nameof(OrderExtensions.InDeptSubTree),
    (node, converter) =>
    {
        Expr deptId = converter.Convert(node.Arguments[1]);
        return Expr.Prop(nameof(SaleOrder.DeptId)).In(Expr.Sql("DeptSubTree", deptId).AsValue());
    });
```

## 四、「最近 7 天」：规则还是事实

同一句「最近 7 天」有两种写法，保存后的含义完全不同。

**函数写法**每次执行都以数据库当前时间重新计算：

```json
{ "$": ">=", "Left": { "#": "CreatedTime" },
  "Right": { "$": "func", "AddDays": [ { "$": "func", "Now": [] }, { "@": -7 } ] } }
```

```sql
"T0"."CreatedTime" >= DATE(datetime('now', 'localtime'), CAST(@0 AS TEXT)||' days')
-- @0 = -7
```

**参数写法**在生成 JSON 时就把时间点固定下来：

```json
{ "$": ">=", "Left": { "#": "CreatedTime" },
  "Right": { "$datetime": "2026-10-18T13:53:02.2826566+08:00" } }
```

```sql
"T0"."CreatedTime" >= @0
-- @0 = 2026/10/18 13:53:02
```

判断标准很简单：

- 实时看板、常驻筛选器：用函数。保存的是「最近 7 天」这条规则，下周打开依然表示最近 7 天。
- 对账、导出、审计快照：用参数。保存的是「10-18 以来」这个事实，过多久重跑结果都一样。

用 Lambda 或 `Expr` 构造时，`DateTime.Now` 会被解析成 `Now()` 函数，只有显式常量才会成为参数；要冻结窗口，就在 Lambda 外先把时间算好，再用闭包变量捕获。

## 五、排序与分页：同一份 JSON 的两层

排序和分页不是额外的请求参数，而是查询 JSON 的两个片段层。最外层是分页片段 `$section`，它的源是排序片段 `$orderby`，排序片段的源才是条件片段 `$where`：

```json
{
  "$section": {
    "$orderby": {
      "$where": null,
      "Where": { "$and": [ ... ] }
    },
    "OrderBys": [
      { "Field": { "#": "Amount" }, "Asc": false },
      { "Field": { "#": "Id" }, "Asc": false }
    ]
  },
  "Skip": 20,
  "Take": 20
}
```

渲染结果：

```sql
ORDER BY "T0"."Amount" DESC, "T0"."Id" DESC
LIMIT 20 OFFSET 20
```

而计数 SQL 只拿到 `filter`，没有排序和分页：

```sql
SELECT Count(1)
FROM "Orders" "T0"
WHERE "T0"."DeptId" IN (WITH RECURSIVE "DeptTree" AS (
        SELECT "T1"."Id" FROM "Departments" "T1" WHERE "T1"."Id" = @0
        UNION ALL
        SELECT "T2"."Id" FROM "Departments" "T2"
          LEFT JOIN "DeptTree" ON "T2"."ParentId" = "DeptTree"."Id")
    SELECT "Id" FROM "DeptTree")
 AND "T0"."CreatedTime" >= DATE(datetime('now', 'localtime'), CAST(@1 AS TEXT)||' days')
-- @0 = 1001, @1 = -7
```

当页 SQL 拿到整条 `query`，会追加三个筛选参数以及排序和分页：

```sql
SELECT "T0"."Id", "T0"."OrderNo", "T0"."DeptId", "T0"."Status", "T0"."Amount", "T0"."CreatedTime"
FROM "Orders" "T0"
WHERE "T0"."DeptId" IN (...)
 AND "T0"."CreatedTime" >= DATE(datetime('now', 'localtime'), CAST(@1 AS TEXT)||' days')
 AND "T0"."Status" = @2 AND "T0"."Amount" >= @3 AND "T0"."OrderNo" LIKE @4
ORDER BY "T0"."Amount" DESC, "T0"."Id" DESC
LIMIT 20 OFFSET 20
-- @0 = 1001, @1 = -7, @2 = 1, @3 = 100, @4 = %SO2026%
```

两个细节值得记住：

- **排序必须有唯一列兜底**（这里是 `Id`），否则翻页时可能出现重复行或漏行。
- **排序字段直接写在 JSON 里**，查询方法不需要再做一次白名单映射；接口对外时统一用 `ExprValidator` 限制可引用的字段范围，排序字段与筛选字段在同一处校验。

## 六、条件保存与重载

列表接口收到的 JSON 同时包含条件、排序和分页，但需要长期保存的只有条件：排序和分页属于「本次浏览」，应该跟随界面状态，而不是被固化进历史记录。

```csharp
// 保存：只存条件
store.Save(userId, JsonSerializer.Serialize<Expr>(GetFilter(query)));

// 重载：把条件放回当前查询
query = SetFilter(query, (LogicExpr?)JsonSerializer.Deserialize<Expr>(store.Load(userId)));
```

`SetFilter` 找到查询里的 `WhereExpr` 并替换其中的条件：

```csharp
static Expr SetFilter(Expr query, LogicExpr? filter)
{
    if (query is LogicExpr) return filter!;

    for (SqlSegment? node = query as SqlSegment; node is not null; node = node.Source)
    {
        if (node is WhereExpr where) { where.Where = filter; return query; }
    }
    throw new ArgumentException("查询 JSON 中没有 $where 节点。");
}
```

条件按原样序列化再装载，渲染出的 SQL 与保存前逐字相同。重载后还能继续叠加新条件：

```csharp
query = SetFilter(query, saved & (Expr.Prop(nameof(SaleOrder.Amount)) >= 500));
```

注意 `DeptSubTree` 这类片段的处理器必须在当前进程注册过，否则重载得到的条件在生成 SQL 时找不到 key，子查询会被渲染成空片段。

## 七、Lambda 入口：构造 Query 对象后传入

条件全部来自服务端时，可以跳过 JSON，用 Lambda 构造 `IQueryable<SaleOrder>`，再把构造好的 Query 对象交给 `SearchAsync` / `Search` 的 `IQueryable<T>` 重载解析。下面的方法可以放在同一个 Service 类里，`_orders` 是 `EntityService<SaleOrder>`：

```csharp
public sealed record OrderPage(int Total, List<SaleOrder> Items);

public sealed record OrderCriteria(
    int DeptId, int? Status, decimal? MinAmount, string? Keyword, int Skip, int Take);

public IQueryable<SaleOrder> BuildQuery(OrderCriteria criteria)
{
    IQueryable<SaleOrder> query = Expr.From<SaleOrder>()
        .Where(o => o.InDeptSubTree(criteria.DeptId))
        .Where(o => o.CreatedTime >= DateTime.Now.AddDays(-7));

    // 可选条件按需追加；重复 Where 会在解析时合并为同一个条件
    if (criteria.Status.HasValue)
        query = query.Where(o => o.Status == criteria.Status.Value);
    if (criteria.MinAmount.HasValue)
        query = query.Where(o => o.Amount >= criteria.MinAmount.Value);
    if (!string.IsNullOrWhiteSpace(criteria.Keyword))
        query = query.Where(o => o.OrderNo.Contains(criteria.Keyword));

    return query
        .OrderByDescending(o => o.Amount)
        .ThenByDescending(o => o.Id)
        .Skip(criteria.Skip)
        .Take(criteria.Take);
}

public async Task<OrderPage> QueryByLambdaAsync(OrderCriteria criteria, CancellationToken ct = default)
{
    IQueryable<SaleOrder> query = BuildQuery(criteria);

    // 把构造好的 Query 对象解析成 Expr；仅拼表达式，不需要执行器
    Expr queryExpr = query.GetQueryExpr();
    LogicExpr? filter = GetFilter(queryExpr);

    int total = await _orders.CountAsync(filter, cancellationToken: ct);
    List<SaleOrder> items = await _orders.SearchAsync(query, cancellationToken: ct);

    return new OrderPage(total, items);
}
```

`Expr.From<SaleOrder>()` 返回 `QuerySource<SaleOrder>`，实现 `IQueryable<T>`，所以 `Where` / `OrderByDescending` / `Skip` / `Take` 都是标准 LINQ 算子。传入 `SearchAsync(IQueryable<T>)` 时，框架用同一个 `LambdaExprConverter` 解析成 `Expr`，后续 SQL 生成、参数绑定与 JSON 入口完全一致。

几个要点：

- 构造阶段不执行查询：`Expr.From<T>()` 返回的查询源没有绑定执行器，直接枚举会抛 `InvalidOperationException`。
- 重复 `Where` 会原地合并成一个条件，因此 `GetFilter(queryExpr)` 拿到的是完整条件，计数与当页仍然同源。
- `DateTime.Now.AddDays(-7)` 在解析阶段变成 `Now()` / `AddDays` 函数，等价于第四节中的函数写法。
- 排序列来自前端时，映射成固定的 `switch` 分支，不要让用户字符串直接进入查询。
- 字段名由编译期成员访问确定，不需要字段白名单；JSON 入口适合前端动态组装，两者可以按调用方混用。

## 八、与常见 ORM 的对照

| 场景 | LiteOrm 的做法 | 常见 ORM 的额外工作 |
| --- | --- | --- |
| 查询条件传入 | 前端 Expr JSON 直接反序列化 | 为每类查询定义 DTO，再映射成查询 |
| 排序与分页传入 | 同一份 JSON 的 `$orderby` / `$section` 层 | 分别接收排序和分页参数，再拼进查询 |
| 部门树过滤 | 一个片段生成方言 `CONNECT BY` 或递归 CTE | 先递归查 Id 列表，再拼 `IN`；或写原生 SQL |
| 同一条件 count + 当页 | 从同一个 `Expr` 提取 `Where` 用于计数 | 手写两套 SQL 或两次表达式树 |
| 表头动态排序 | `OrderBys` 直接写在查询 JSON 里 | 大量 `switch` 或字符串拼接 |
| 条件保存 | 只保存提取出的条件 JSON | 另建 DTO 转换层 |
| Lambda / Expr | 两种入口共用同一棵表达式树 | 通常只能选一种 |

## 最佳实践

1. **条件统一放在 `$and` 里**：可选条件缺失就不写节点，SQL 自动少一段。
2. **方言差异集中到一个片段**：部门树这类能力注册一次，默认实现加方言覆盖，不要在业务代码里判断数据库。
3. **排序必须有唯一列兜底**：`Amount` 之后补 `Id`，翻页才不会重复或漏行。
4. **计数只取 `filter`**：当页使用整条 `query`，两者来自同一次反序列化，条件永远一致。
5. **只保存条件**：排序和分页属于界面状态；相对时间用函数，绝对时间用参数。
6. **JSON 入口必须校验**：用 `ExprValidator` 限制字段与操作符，不能把任意 JSON 直接送进 SQL 生成。
7. **按调用方选择入口**：前端动态条件走 JSON，服务端固定条件走 Lambda Query 对象，两者最终生成同一份 SQL。

## 延伸阅读

- [复杂查询实例（完整参考）](../typical-applications/complex-queries.md)
- [查询总览](../core-usage/query-overview.md)
- [Lambda 查询指南](../core-usage/lambda-guide.md)
- [Expr JSON 序列化格式](../extensibility/expr-serialization.md)
- [前端原生 Expr 查询](../extensibility/frontend-native-expr.md)
- [CTE 指南](../core-usage/cte-guide.md)
- [自定义分页](../advanced-topics/custom-paging.md)
