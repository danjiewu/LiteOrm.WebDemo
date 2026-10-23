# 第5篇：自动关联查询

> ⏱ 阅读约 15 分钟

关联查询（JOIN）是 ORM 的核心能力之一。LiteOrm 提供了独特的**声明式关联**机制——通过特性注解声明实体间的关系，查询时自动生成 JOIN 语句，无需手写 SQL 关联逻辑。

## 与其他 ORM 的关联方式对比

| 维度 | LiteOrm | EF Core | Dapper |
|------|---------|---------|--------|
| 关联定义 | `[ForeignType]` + `[ForeignColumn]` 特性 | Navigation Property + Fluent API | 无（手动写SQL） |
| JOIN 触发 | 查询 View 类型时自动 JOIN | `.Include()` 显式声明 | 手动 JOIN |
| 关联条件 | `[TableJoin]` 特性声明 | Fluent API `HasForeignKey` | 手动 ON |
| 深度关联 | 多级 View 自动级联 | `.ThenInclude()` 链式 | 手动多次 JOIN |
| N+1 问题 | 自动 JOIN 避免 | `.Include()` 避免，否则 N+1 | 手动控制 |

## 核心特性

### 1. ForeignType —— 声明外键关联

在实体中标记外键属性引用的目标类型：

```csharp
[Table("Orders")]
public class Order : ObjectBase
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [Column("UserId")]
    [ForeignType(typeof(User))]  // 声明外键关联到 User 表
    public int UserId { get; set; }

    [Column("ProductId")]
    [ForeignType(typeof(Product))]  // 声明外键关联到 Product 表
    public int ProductId { get; set; }

    [Column("Amount")]
    public decimal Amount { get; set; }

    [Column("CreateTime")]
    public DateTime CreateTime { get; set; }
}
```

### 2. ForeignColumn —— 在视图中引用外表字段

在视图模型中声明需要关联查询的外部字段：

```csharp
// 订单视图——包含关联的用户名和产品名
public class OrderView : Order
{
    [ForeignColumn(typeof(User), Property = "UserName")]
    public string UserName { get; set; } = string.Empty;

    [ForeignColumn(typeof(User), Property = "Email")]
    public string UserEmail { get; set; } = string.Empty;

    [ForeignColumn(typeof(Product), Property = "ProductName")]
    public string ProductName { get; set; } = string.Empty;
}
```

查询时只需指定 `OrderView` 类型，LiteOrm 自动生成 JOIN：

```csharp
// 查询订单，自动 JOIN User 和 Product 表
var orders = await orderService.SearchAsync<OrderView>();

// 等价于手写 SQL：
// SELECT o.*, u.UserName, u.Email, p.ProductName
// FROM Orders o
// LEFT JOIN Users u ON o.UserId = u.Id
// LEFT JOIN Products p ON o.ProductId = p.Id
```

### 3. TableJoin —— 自定义关联条件

默认情况下，`ForeignType` 使用被引用表的主键作为关联列。如果需要自定义关联条件，可以使用 `[TableJoin]`：

```csharp
[Table("Orders")]
[TableJoin(typeof(User), "Id", "UserId")]   // 指定 ON 条件
[TableJoin(typeof(Product), "Id", "ProductId")]
public class Order : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("UserId")]
    public int UserId { get; set; }

    [Column("ProductId")]
    public int ProductId { get; set; }
}
```

`[TableJoin]` 参数：
- 第一个参数：关联的目标实体类型
- 第二个参数：目标表的关联列（通常是主键）
- 第三个参数：当前表的关联列（外键列）

### 4. AutoExpand —— 自动展开多级关联

`AutoExpand` 配置在 `[ForeignType]` 或 `[TableJoin]` 特性上。当一个表被作为外表引用时，若其 `ForeignType` 声明了 `AutoExpand = true`，则它自身引用的外表也会被自动引入 JOIN：

```csharp
// 实体上配置 AutoExpand
[Table("Products")]
public class Product : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("CategoryId")]
    [ForeignType(typeof(Category), AutoExpand = true)]  // AutoExpand 在 ForeignType 上
    public int CategoryId { get; set; }

    [Column("ProductName")]
    public string ProductName { get; set; } = string.Empty;
}

// 视图中引用 Product 的 CategoryName 即可自动展开二级关联
public class OrderView : Order
{
    [ForeignColumn(typeof(User), Property = "UserName")]
    public string UserName { get; set; } = string.Empty;

    [ForeignColumn(typeof(Product), Property = "ProductName")]
    public string ProductName { get; set; } = string.Empty;

    [ForeignColumn(typeof(Product), Property = "CategoryName")]
    public string CategoryName { get; set; } = string.Empty;
}
```

当 Product 本身也定义了 `[ForeignType(typeof(Category))]` 和 `[TableJoin]` 时，`[AutoExpand]` 会自动展开二级关联，生成：

```sql
SELECT o.*, u.UserName, p.ProductName, c.CategoryName
FROM Orders o
LEFT JOIN Users u ON o.UserId = u.Id
LEFT JOIN Products p ON o.ProductId = p.Id
LEFT JOIN Categories c ON p.CategoryId = c.Id
```

## 完整示例

假设我们有以下实体模型：

```csharp
// 用户
[Table("Users")]
public class User : ObjectBase
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [Column("UserName")]
    public string UserName { get; set; } = string.Empty;

    [Column("DeptId")]
    [ForeignType(typeof(Department))]
    public int DeptId { get; set; }
}

// 部门
[Table("Departments")]
public class Department : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("DeptName")]
    public string DeptName { get; set; } = string.Empty;
}

// 订单
[Table("Orders")]
public class Order : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("UserId")]
    [ForeignType(typeof(User))]
    public int UserId { get; set; }

    [Column("Amount")]
    public decimal Amount { get; set; }
}
```

### 定义多级视图

```csharp
// 用户视图——包含部门名
public class UserView : User
{
    [ForeignColumn(typeof(Department), Property = "DeptName")]
    public string DeptName { get; set; } = string.Empty;
}

// 订单视图——包含用户信息和部门信息
public class OrderView : Order
{
    [ForeignColumn(typeof(User), Property = "UserName")]
    public string UserName { get; set; } = string.Empty;

    [ForeignColumn(typeof(User), Property = "DeptName")]
    public string DeptName { get; set; } = string.Empty;
}
```

### 定义服务

```csharp
public interface IOrderService :
    IEntityServiceAsync<Order>,
    IEntityViewServiceAsync<OrderView>
{ }

public class OrderService : EntityService<Order, OrderView>, IOrderService { }
```

### 查询

```csharp
// 按关联字段筛选
var orders = await orderService.SearchAsync<OrderView>(
    o => o.UserName == "john"  // 自动 JOIN User 表
);

// 按关联字段排序
var sorted = await orderService.SearchAsync<OrderView>(
    q => q.Where(o => o.Amount > 100)
          .OrderBy(o => o.UserName)
);

// 按二级关联字段筛选
var deptOrders = await orderService.SearchAsync<OrderView>(
    o => o.DeptName == "IT"  // 自动 JOIN User + Department
);
```

## EXISTS 关联查询

除了 JOIN 关联，LiteOrm 还支持 `EXISTS` 子查询，用于检查关联数据的存在性：

```csharp
using static LiteOrm.Common.Expr;

// 查询有订单的用户（EXISTS 子查询）
var usersWithOrders = await userService.SearchAsync(
    q => q.Where(u => Exists<Order>(o => o.UserId == u.Id))
);

// 查询有未完成订单的用户
var usersWithOpenOrders = await userService.SearchAsync(
    q => q.Where(u => Exists<Order>(
        o => o.UserId == u.Id && o.Status != "Completed"
    ))
);

// 使用 ExistsRelated（前提是已在模型中声明了 ForeignType）
var hasOpenOrder = ExistsRelated<Order>(
    Prop("Status") != "Completed"
);
var activeUsers = await userService.SearchAsync(
    u => u.IsActive == true && hasOpenOrder.To<bool>()
);
```

## N+1 问题与解决方案

N+1 查询是 ORM 中常见的性能陷阱。LiteOrm 通过自动 JOIN 从根源上避免了这个问题。

### 错误做法（N+1）

```csharp
// 先查询所有订单（1次查询）
var orders = await orderService.SearchAsync();

// 再逐条查询关联用户（N次查询）
foreach (var order in orders)
{
    var user = await userService.GetObjectAsync(order.UserId);  // N+1!
    Console.WriteLine($"{order.Id}: {user?.UserName}");
}
```

### 正确做法（1次查询）

```csharp
// 使用视图类型，自动 JOIN（1次查询）
var orders = await orderService.SearchAsync<OrderView>();
foreach (var order in orders)
{
    Console.WriteLine($"{order.Id}: {order.UserName}");  // 已在结果中
}
```

## 与 EF Core 的 Include 对比

```csharp
// LiteOrm：自动 JOIN（基于 View 类型）
public class OrderView : Order
{
    [ForeignColumn(typeof(User), Property = "UserName")]
    public string UserName { get; set; }
}
var orders = await orderService.SearchAsync<OrderView>();

// EF Core：显式 Include 链
var orders = await dbContext.Orders
    .Include(o => o.User)
    .ThenInclude(u => u.Department)
    .ToListAsync();
```

| 维度 | LiteOrm | EF Core |
|------|---------|---------|
| 关联声明 | 特性 + View 类型 | 导航属性 + Fluent API |
| 触发方式 | 查询 View 类型即自动 JOIN | 显式 `.Include()` |
| 返回类型 | 扁平的 View 类型 | 嵌套的导航属性对象 |
| 深度关联 | 多级 View 自动级联 | `.ThenInclude()` 链式调用 |
| 灵活性 | 不同 View 可以包含不同关联字段 | 需要显式控制 Include 路径 |

LiteOrm 的方式更偏向"声明式"——定义好 View 类型，查询时自动 JOIN。EF Core 的方式更偏向"命令式"——每次查询都要显式声明 Include 路径。

## 最佳实践

1. **实体只定义数据列**：`[ForeignType]` 放在实体上，声明外键关系
2. **视图定义关联字段**：`[ForeignColumn]` 放在 View 上，声明需要查询的外部字段
3. **不同场景用不同 View**：列表页用轻量 View（只含必要关联字段），详情页用完整 View
4. **避免 N+1**：始终使用 View 类型进行关联查询，不要手动循环查询
5. **合理使用 TableJoin**：当默认关联（主键=外键）不满足时，使用 `[TableJoin]` 自定义
6. **多级关联谨慎使用 AutoExpand**：过深的关联可能产生大量 JOIN，影响性能

## 下一篇预告

[第6篇：事务管理](../series/06-transactions.md) — 掌握 LiteOrm 的声明式事务和手动事务，理解 AOP 事务管理的原理和最佳实践。