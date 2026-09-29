# 第3篇：Lambda 查询与 CRUD 操作

> ⏱ 阅读约 18 分钟

LiteOrm 提供了完整、类型安全的 Lambda 表达式查询能力。相比手写 SQL 或字符串拼接，Lambda 表达式在编译时就能发现字段名错误、类型不匹配等问题，并且享受 IDE 的智能提示和重构支持。

## 实体与服务回顾

假设我们有以下实体和服务定义（基于前两篇的配置）：

```csharp
[Table("Users")]
public class User : ObjectBase
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [Column("UserName")]
    public string UserName { get; set; } = string.Empty;

    [Column("Email")]
    public string Email { get; set; } = string.Empty;

    [Column("Age")]
    public int Age { get; set; }

    [Column("Status")]
    public int Status { get; set; }

    [Column("CreateTime")]
    public DateTime? CreateTime { get; set; }
}

public class UserView : User { }

public interface IUserService :
    IEntityServiceAsync<User>,
    IEntityViewServiceAsync<UserView>
{ }

public class UserService : EntityService<User, UserView>, IUserService { }
```

## CRUD 速览

LiteOrm 的 Service 层提供了同步和异步两套 API。**推荐始终使用异步版本**。

| 操作 | 异步方法 | 说明 |
|------|---------|------|
| 插入 | `InsertAsync(T)` | 插入单条实体 |
| 批量插入 | `BatchInsertAsync(IEnumerable<T>)` | 批量插入（一次网络往返） |
| 更新 | `UpdateAsync(T)` | 按主键更新实体 |
| 批量更新 | `BatchUpdateAsync(IEnumerable<T>)` | 批量更新 |
| 删除 | `DeleteAsync(T)` | 按实体删除 |
| 按ID删除 | `DeleteIDAsync(object id)` | 按主键值删除 |
| 批量删除 | `BatchDeleteAsync(IEnumerable<T>)` | 批量删除 |
| UpdateOrInsert | `UpdateOrInsertAsync(T)` | 存在则更新，否则插入 |

### 插入

```csharp
// 单条插入
var user = new User
{
    UserName = "john",
    Email = "john@example.com",
    Age = 28,
    Status = 1,
    CreateTime = DateTime.Now
};
await userService.InsertAsync(user);
// user.Id 会自动回填（自增主键）
Console.WriteLine($"新用户ID：{user.Id}");

// 批量插入（显著快于循环单条插入）
var users = Enumerable.Range(1, 1000).Select(i => new User
{
    UserName = $"user{i}",
    Email = $"user{i}@example.com",
    Age = 18 + i % 50,
    Status = 1,
    CreateTime = DateTime.Now
}).ToList();

await userService.BatchInsertAsync(users);
```

### 更新

```csharp
// 按主键更新（需先查询出实体）
var user = await userService.GetObjectAsync(1);
user.Email = "newemail@example.com";
user.Age = 30;
await userService.UpdateAsync(user);

// 批量更新
var users = await userService.SearchAsync(u => u.Status == 0);
foreach (var u in users)
{
    u.Status = 1;
}
await userService.BatchUpdateAsync(users);
```

### 删除

```csharp
// 按主键删除
await userService.DeleteIDAsync(5);

// 按实体删除
var user = await userService.GetObjectAsync(6);
await userService.DeleteAsync(user);

// 批量删除
var inactiveUsers = await userService.SearchAsync(u => u.Status == 0);
await userService.BatchDeleteAsync(inactiveUsers);
```

### UpdateOrInsert（存在则更新，否则插入）

```csharp
var user = new User
{
    Id = 1,    // 如果 Id=1 存在则更新，否则插入
    UserName = "john_updated",
    Email = "john_new@example.com",
    Age = 29,
    Status = 1
};
await userService.UpdateOrInsertAsync(user);
```

> 对比 EF Core：LiteOrm 的 UpdateOrInsert 在 MySQL 下自动使用 `ON DUPLICATE KEY UPDATE`，在 PostgreSQL 下使用 `ON CONFLICT ... DO UPDATE`，无需手动写 SQL。

## Lambda 查询

### 基础查询

```csharp
// 查询所有
var all = await userService.SearchAsync();

// 单条件
var adults = await userService.SearchAsync(u => u.Age >= 18);

// 多条件（AND）
var activeAdults = await userService.SearchAsync(
    u => u.Age >= 18 && u.Status == 1
);

// OR 条件
var special = await userService.SearchAsync(
    u => u.Age < 18 || u.Age > 60
);

// 查询单条（建议确保条件唯一）
var john = await userService.SearchOneAsync(u => u.UserName == "john");

// 按主键查询
var user = await userService.GetObjectAsync(1);
```

### 字符串函数

```csharp
// StartsWith → LIKE 'xxx%'
var aUsers = await userService.SearchAsync(u => u.UserName.StartsWith("A"));

// EndsWith → LIKE '%xxx'
var gmailUsers = await userService.SearchAsync(u => u.Email.EndsWith("@gmail.com"));

// Contains → LIKE '%xxx%'
var testUsers = await userService.SearchAsync(u => u.Email.Contains("test"));

// 组合使用
var result = await userService.SearchAsync(
    u => u.UserName.StartsWith("A") && u.Email.Contains("@gmail.com")
);
```

这些都自动转换为参数化的 LIKE 查询，并且通配符 `%` 和 `_` 会自动转义防止注入。

### 日期函数

```csharp
// DateTime.Now → CURRENT_TIMESTAMP
var newUsers = await userService.SearchAsync(u => u.CreateTime > DateTime.Now.AddDays(-7));

// DateTime.Today → CURRENT_DATE
var todayUsers = await userService.SearchAsync(u => u.CreateTime >= DateTime.Today);

// AddDays → DATE_ADD
var expireSoon = await userService.SearchAsync(
    u => u.CreateTime.AddDays(30) < DateTime.Now
);
```

### 集合操作（IN）

```csharp
var ids = new List<int> { 1, 2, 3, 5, 8 };

// List.Contains → SQL IN
var users = await userService.SearchAsync(u => ids.Contains(u.Id));

// 数组也支持
int[] statusList = { 1, 2 };
var activeOrPending = await userService.SearchAsync(u => statusList.Contains(u.Status));
```

### NULL 判断

```csharp
// 判断 NULL——自动转换为 IS NULL
var noTime = await userService.SearchAsync(u => u.CreateTime == null);

// 判断 NOT NULL
var hasTime = await userService.SearchAsync(u => u.CreateTime != null);

// 不要担心 SQL 中 = NULL 的问题，LiteOrm 会自动处理
```

## 排序

```csharp
// 单列升序
var sorted = await userService.SearchAsync(
    q => q.Where(u => u.Age >= 18).OrderBy(u => u.Age)
);

// 单列降序
var newest = await userService.SearchAsync(
    q => q.Where(u => u.Status == 1).OrderByDescending(u => u.CreateTime)
);

// 多列排序
var multiSort = await userService.SearchAsync(
    q => q.Where(u => u.Age >= 18)
          .OrderByDescending(u => u.Status)
          .OrderBy(u => u.Age)
          .OrderByDescending(u => u.CreateTime)
);
```

## 分页

```csharp
// 基础分页：Skip + Take
var page1 = await userService.SearchAsync(
    q => q.Where(u => u.Age >= 18)
          .OrderByDescending(u => u.CreateTime)
          .Skip(0).Take(20)   // 第一页，每页20条
);

var page2 = await userService.SearchAsync(
    q => q.Where(u => u.Age >= 18)
          .OrderByDescending(u => u.CreateTime)
          .Skip(20).Take(20)  // 第二页
);

// 带总数统计的分页
var count = await userService.CountAsync(u => u.Age >= 18);
var pages = (count + 19) / 20;  // 总页数
```

> 性能提示：大偏移量分页（如 `Skip(10000).Take(20)`）效率较低。建议使用游标分页：

```csharp
// 游标分页（基于ID，利用索引）
var lastId = 10000;
var page = await userService.SearchAsync(
    q => q.Where(u => u.Id > lastId && u.Age >= 18)
          .OrderBy(u => u.Id)
          .Take(20)
);
```

## 统计与判断

```csharp
// 计数
int totalUsers = await userService.CountAsync();
int activeUsers = await userService.CountAsync(u => u.Status == 1);
int adultUsers = await userService.CountAsync(u => u.Age >= 18);

// 存在性判断（比 Count > 0 更高效）
bool hasAdmin = await userService.ExistsAsync(u => u.UserName == "admin");
bool hasInactive = await userService.ExistsAsync(u => u.Status == 0);

// 聚合查询
// 通过 DataViewDAO 可以获取聚合结果
```

## 同步 vs 异步 API

LiteOrm 同时提供同步和异步 API：

| 同步 | 异步（推荐） |
|------|-------------|
| `Insert(T)` | `InsertAsync(T)` |
| `Update(T)` | `UpdateAsync(T)` |
| `Delete(T)` | `DeleteAsync(T)` |
| `Search(expr)` | `SearchAsync(expr)` |
| `Count(expr)` | `CountAsync(expr)` |
| `GetObject(id)` | `GetObjectAsync(id)` |

**推荐始终使用异步 API**，特别是在 ASP.NET Core 中，异步方法可以释放线程处理其他请求。

## 与其他 ORM 的 CRUD 对比

### 插入对比

```csharp
// LiteOrm
var user = new User { UserName = "john", Age = 25 };
await userService.InsertAsync(user);

// EF Core
var user = new User { UserName = "john", Age = 25 };
dbContext.Users.Add(user);
await dbContext.SaveChangesAsync();

// Dapper
var sql = "INSERT INTO Users (UserName, Age) VALUES (@Name, @Age); SELECT LAST_INSERT_ID();";
var id = await connection.ExecuteScalarAsync<int>(sql, new { Name = "john", Age = 25 });
```

### 查询对比

```csharp
// LiteOrm - Lambda 自动翻译
var users = await userService.SearchAsync(u => u.Age >= 18 && u.Status == 1);

// EF Core - Lambda 自动翻译（类似）
var users = await dbContext.Users.Where(u => u.Age >= 18 && u.Status == 1).ToListAsync();

// Dapper - 手写 SQL
var users = await connection.QueryAsync<User>(
    "SELECT * FROM Users WHERE Age >= @Age AND Status = @Status",
    new { Age = 18, Status = 1 }
);
```

### 批量操作对比

```csharp
// LiteOrm - 原生批量方法
await userService.BatchInsertAsync(users);
await userService.BatchUpdateAsync(users);
await userService.BatchDeleteAsync(users);

// EF Core - 逐条跟踪后统一提交
dbContext.Users.AddRange(users);
await dbContext.SaveChangesAsync();  // 仍然是逐条发送

// Dapper - 需要手动构造批量SQL或多次执行
```

## 选择投影（Select）

如果只需要部分字段，可以通过 `ObjectViewDAO.SearchAs<T>()` 进行投影查询：

```csharp
using static LiteOrm.Common.Expr;

var result = await userViewDAO.SearchAs<UserView>(
    From<UserView>()
        .Where(Prop("Age") > 18)
        .Select(Prop("Id"), Prop("UserName"), Prop("Email"))
);
```

这比查询全部字段再在内存中裁剪高效得多。

## 流式查询（大数据量场景）

对于需要处理大量数据且不希望一次性加载到内存的场景，LiteOrm 支持异步流：

```csharp
using static LiteOrm.Common.Expr;

await foreach (var user in userViewDAO.Search(Prop("Age") >= 18))
{
    // 逐条处理，内存友好
    ProcessUser(user);
}
```

适合日志导出、报表遍历、后台批处理等场景。

## 与 EF Core 的深度对比

| 维度 | LiteOrm | EF Core |
|------|---------|---------|
| **查询方式** | Lambda / Expr / ExprString 三种 | Lambda + LINQ |
| **批量插入** | `BatchInsertAsync`，一次网络往返 | `AddRange` + `SaveChanges`，仍是逐条 |
| **批量更新** | `BatchUpdateAsync`，原生支持 | 需逐条跟踪或使用 `ExecuteUpdate` |
| **批量删除** | `BatchDeleteAsync`，原生支持 | 需逐条跟踪或使用 `ExecuteDelete` |
| **UpdateOrInsert** | 原生 `UpdateOrInsertAsync` | 需手动判断或扩展 |
| **流式查询** | `IAsyncEnumerable` 原生支持 | EF Core 5+ 支持 `AsAsyncEnumerable` |
| **变更追踪** | 无自动追踪（更轻量） | 自动追踪（Update时方便但开销大） |
| **投影查询** | `SearchAs<T>()` | `Select()` LINQ |

LiteOrm 不维护变更追踪上下文，这意味着每次 Update 需要先查出来再更新，但换来的是更低的内存开销和更可预测的性能。

## 最佳实践

1. **始终使用异步API**：尤其在Web应用中
2. **批量操作优于循环单条**：`BatchInsertAsync` 比循环 `InsertAsync` 快10倍以上
3. **使用流式查询处理大数据**：`await foreach` 优于 `ToList()` + 循环
4. **分页务必配合排序**：无排序的分页结果不可预测
5. **大偏移量改用游标分页**：`WHERE Id > lastId` 代替 `Skip(10000)`
6. **判断存在用 ExistsAsync**：比 `CountAsync > 0` 高效
7. **需要部分字段时用投影查询**：比全字段查询再裁剪高效

## 下一篇预告

[第4篇：Expr 表达式系统](../series/04-expr-system.md) — 深入 LiteOrm 最具特色的 Expr 表达式系统，掌握动态查询构建、链式API、子查询和 Lambda/Expr 混合编程。