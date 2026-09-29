# 第2篇：实体映射与数据源配置

> ⏱ 阅读约 18 分钟

在上一篇中我们完成了 LiteOrm 的安装和第一个示例。本篇深入讲解实体映射的核心概念——Table/Column 特性、ObjectBase 基类以及多数据源配置。

## Table 特性

`[Table]` 特性用于标记实体类对应的数据库表，定义表级元数据。

### 基础用法

```csharp
[Table("Users")]
public class User : ObjectBase
{
    // ...
}
```

`[Table]` 特性支持以下参数：

| 参数 | 类型 | 说明 |
|------|------|------|
| `TableName` | `string` | 数据库表名 |
| `DataSource` | `string` | 指定使用的数据源名称，用于多数据源场景 |

### 表名占位符

表名支持 `{n}` 占位符，配合分表功能使用：

```csharp
// 按月份分表：Log_202401, Log_202402, ...
[Table("Log_{0}")]
public class Log : ObjectBase, IArged
{
    [Column("CreateTime")]
    public DateTime CreateTime { get; set; }

    string[] IArged.TableArgs => new[] { CreateTime.ToString("yyyyMM") };
}

// 多维度分表：Sales_US_2025, Sales_CN_2025, ...
[Table("Sales_{0}_{1}")]
public class Sales : ObjectBase
{
    // TableArgs = ["US", "2025"]
}
```

> 分表功能的详细用法将在第7篇专门讲解。

### 与其他 ORM 对比

| 特性 | LiteOrm | EF Core | Dapper |
|------|---------|---------|--------|
| 表名映射 | `[Table("name")]` | `[Table("name")]` | 手动指定 |
| 表名占位符 | 原生 `{0}` 占位符 | 不支持 | 手动拼接 |
| 多数据源 | `DataSource` 属性 | DbContext 指定 | 连接字符串指定 |

## Column 特性

`[Column]` 特性用于映射实体属性到数据库列，定义了最细粒度的列级配置。

### Column 参数一览

```csharp
[Table("Users")]
public class User : ObjectBase
{
    [Column("Id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [Column("UserName", Length = 50, AllowNull = false)]
    public string UserName { get; set; } = string.Empty;

    [Column("Age", DefaultValue = "0")]
    public int Age { get; set; }

    [Column("Salary", DbType = System.Data.DbType.Decimal)]
    public decimal Salary { get; set; }

    [Column("CreateTime", ColumnMode = ColumnMode.Final)]  // 只允许Insert和Read，不参与Update
    public DateTime CreateTime { get; set; }

    [Column("Status", Constant = 1)]  // 始终过滤 Status = 1 的记录
    public int Status { get; set; }

    [Column("Email", IsUnique = true)]  // 唯一约束
    public string Email { get; set; } = string.Empty;
}
```

完整参数说明：

| 参数 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| `ColumnName` | `string` | 属性名 | 数据库列名 |
| `IsPrimaryKey` | `bool` | `false` | 是否主键 |
| `IsIdentity` | `bool` | `false` | 是否自增列 |
| `IdentityIncreasement` | `int` | `1` | 自增列的增量值 |
| `IdentityExpression` | `string` | `null` | 自增列的表达式（如序列名称） |
| `IsTimestamp` | `bool` | `false` | 是否为时间戳列 |
| `IsIndex` | `bool` | `false` | 是否应创建索引 |
| `IsUnique` | `bool` | `false` | 是否具有唯一约束 |
| `Length` | `int` | `0` | 列长度 |
| `DbType` | `System.Data.DbType` | `Object` | 数据库列类型 |
| `AllowNull` | `bool` | `true` | 是否允许为空 |
| `DefaultValue` | `string` | `null` | 默认值（常量值或数据库函数表达式） |
| `Constant` | `object` | `null` | 全局固定筛选值（ConstFilter） |
| `ColumnMode` | `ColumnMode` | `Full` | 列操作模式，控制读/写/更新的参与范围 |

### Column.Constant —— 全局固定过滤

这是 LiteOrm 区别于其他 ORM 的一个特色功能。当某个列的值在业务意义上始终固定时，可以使用 `Constant`：

```csharp
public class Department
{
    [Column("State", Constant = 1)]  // 始终只查询启用的部门
    public int State { get; set; }
}
```

设置 `Constant = 1` 后，涉及该表的查询与写入会自动追加 `State = 1`：主表进 `WHERE`，关联查询进 `JOIN ... ON`，`UPDATE` / `DELETE` 进 `WHERE`，`EXISTS` 子查询里的目标表也会带上。DAO 按主键读取（`GetObject`、`ExistsKey`）走的是模型自带的 `From` 片段，关联表的条件不在这条路径上。

**适用场景**：固定状态过滤、软删除标记、固定分区

**不适用场景**：当前用户过滤、当前租户过滤（这些属于运行时上下文，应使用 Expr 动态条件）

> 对比 EF Core 的 Global Query Filter：EF Core 通过 `HasQueryFilter()` 实现类似效果，但需要在 `OnModelCreating` 中手动配置。LiteOrm 直接在实体声明，更加内聚。

### ColumnMode —— 列操作模式

`ColumnMode` 是一个 `[Flags]` 枚举，用于控制列在 INSERT / UPDATE / SELECT 操作中的参与范围：

| 值 | 组合 | 说明 |
|------|------|------|
| `Full` | `Read \| Update \| Insert` | 所有操作均参与（默认） |
| `Read` | `Read` | 仅读取，不参与写入 |
| `Write` | `Insert \| Update` | 仅写入，不参与读取 |
| `Final` | `Insert \| Read` | 允许 INSERT 和 Read，Update 时不可更改 |
| `None` | 无 | 不参与任何操作 |

```csharp
// CreateTime 在插入时写入，之后不可更新
[Column("CreateTime", ColumnMode = ColumnMode.Final)]
public DateTime CreateTime { get; set; }

// 计算列：仅从数据库读取，不写入
[Column("FullName", ColumnMode = ColumnMode.Read)]
public string FullName { get; set; } = string.Empty;
```

## ObjectBase 基类

所有 LiteOrm 实体都应继承 `ObjectBase`，它提供了以下基础能力：

### 1. 对象复制与克隆

```csharp
var user = await userService.GetObjectAsync(1);
var copy = (User)user.Clone();  // 浅克隆（MemberwiseClone）

// 从另一个 ObjectBase 对象复制属性
var target = new User();
target.CopyFrom(user);  // 将 user 的属性值复制到 target
```

### 2. 属性索引器

```csharp
var user = new User();
user["UserName"] = "john";       // 通过字符串名设置属性
var name = user["UserName"];     // 通过字符串名获取属性
```

### 3. ILogable 日志脱敏

`ObjectBase` 实现了 `ILogable` 接口。当实体被记入日志时，会自动调用 `ToLog()` 方法。结合属性上的 `[Log(false)]` 可以控制敏感字段不进入日志：

```csharp
[Table("Users")]
public class User : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public int Id { get; set; }

    [Column("UserName")]
    public string UserName { get; set; } = string.Empty;

    [Column("PasswordHash")]
    [Log(false)]  // 日志中不记录此字段
    public string PasswordHash { get; set; } = string.Empty;
}
```

## 多数据源配置

LiteOrm 原生支持多数据库、多连接字符串。这在读写分离、多租户数据库等场景非常实用。

### 配置多数据源

```json
{
  "LiteOrm": {
    "Default": "MainDb",
    "DataSources": [
      {
        "Name": "MainDb",
        "ConnectionString": "Server=192.168.1.10;Database=MainDb;...",
        "Provider": "MySqlConnector.MySqlConnection, MySqlConnector",
        "PoolSize": 16,
        "MaxPoolSize": 100
      },
      {
        "Name": "ReadDb",
        "ConnectionString": "Server=192.168.1.11;Database=MainDb;...",
        "Provider": "MySqlConnector.MySqlConnection, MySqlConnector",
        "PoolSize": 8
      },
      {
        "Name": "LogDb",
        "ConnectionString": "Server=192.168.1.20;Database=LogDb;...",
        "Provider": "Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient"
      }
    ]
  }
}
```

### 实体指定数据源

```csharp
// 用户表在主库
[Table("Users")]
public class User : ObjectBase
{
    // ...
}

// 日志表在独立的日志库
[Table("OperationLogs", DataSource = "LogDb")]
public class OperationLog : ObjectBase
{
    [Column("Id", IsPrimaryKey = true)]
    public long Id { get; set; }

    [Column("Content")]
    public string Content { get; set; } = string.Empty;
}
```

这样，对 `User` 的所有操作自动走 `MainDb`，对 `OperationLog` 的自动走 `LogDb`，无需在业务代码中手动切换。

### 运行时切换数据源

对于需要运行时动态切换数据源的场景（如多租户、读写分离），可以继承 DAO 并重写 `DataSource` 属性：

```csharp
// 自定义 DAO：根据当前租户或上下文动态决定数据源
public class TenantUserViewDAO : ObjectViewDAO<UserView>
{
    private readonly ITenantProvider _tenantProvider;

    public TenantUserViewDAO(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    protected override string DataSource
    {
        get
        {
            // 根据租户上下文返回对应的数据源名称
            return _tenantProvider.IsVip
                ? "VipDb"
                : "DefaultConnection";
        }
    }
}
```

`DAOBase` 中的 `DataSource` 是 `protected virtual` 属性，默认返回 `TableDefinition.DataSource`（即 `[Table]` 特性中声明的值）。重写它即可实现动态路由。

### 只读库（ReadOnlyConfigs）与读写分离

如果你希望实现“写入走主库、查询走只读副本”，不需要额外配置一个独立的 `ReadDb` 数据源；更推荐把只读副本挂在主库数据源（本文示例使用 `main`）的 `ReadOnlyConfigs` 下。

```json
{
  "LiteOrm": {
    "Default": "main",
    "DataSources": [
      {
        "Name": "main",
        "ConnectionString": "Server=192.168.1.10;Database=MainDb;...",
        "Provider": "MySqlConnector.MySqlConnection, MySqlConnector",
        "PoolSize": 16,
        "MaxPoolSize": 100,
        "ReadOnlyConfigs": [
          {
            "ConnectionString": "Server=192.168.1.11;Database=MainDb;...",
            "PoolSize": 8
          }
        ]
      }
    ]
  }
}
```

只读库至少需要 `ConnectionString`；连接池相关字段（如 `PoolSize`、`MaxPoolSize`、`KeepAliveDuration` 等）未填写时会自动继承主库配置。字段完整定义请参考：[配置项速查](../reference/configuration-reference.md#readonlyconfigs)。

**运行时路由规则（关键语义）**：

- **查询类 DAO / ViewService 优先走只读池**：例如 `ObjectViewDAO<T>` / `DataViewDAO<T>`（它们属于 `IsView` 模式）在获取连接时会优先请求只读连接。
- **同一 Session 会缓存 RO 连接**：同一个 `Session` 内会缓存 `main:RO` 与 `main:RW` 两类连接；只读池的 round-robin 只发生在“首次获取 RO 连接”时，后续查询会复用该 RO 连接。
- **事务中强制走主库**：一旦开启事务，即使是查询也会强制使用 `main:RW`（避免读到复制延迟造成的不一致）。
- **未配置只读池会自动回落主库**：`ReadOnlyConfigs` 为空时，读请求会回落到主库连接，避免额外创建第二条连接。

**实践建议**：

- 只读库适合“允许读到略旧数据”的列表/报表等查询。
- 如果业务需要“读己之写（read-your-writes）”，建议把相关读写放进同一事务，或明确走主库连接。

更深入的读写分离示例与注意事项，可参考：[分表与 TableArgs（含 8.5 读写分离）](../advanced-topics/sharding-and-tableargs.md#85-读写分离)。

### 连接池配置

```json
{
  "PoolSize": 16,         // 连接池缓存的最大连接数
  "MaxPoolSize": 100,     // 最大并发连接数
  "KeepAliveDuration": "00:10:00"  // 连接保活时长
}
```

| 场景 | 建议配置 |
|------|---------|
| 小并发 | `PoolSize=5, MaxPoolSize=20` |
| 中等并发 | `PoolSize=16, MaxPoolSize=100` |
| 大并发 | `PoolSize=50, MaxPoolSize=500` |

### 与 EF Core 多数据源对比

| 维度 | LiteOrm | EF Core |
|------|---------|---------|
| 配置方式 | JSON 配置 + `DataSource` | 多个 `DbContext` + DI 注册 |
| 实体级指定 | `[Table(DataSource = "...")]` | 放入对应的 `DbContext` |
| 运行时切换 | 重写 DAO 的 `DataSource` 属性 | `DbContextFactory` 创建指定上下文 |
| 连接池 | 内建配置 | 依赖 ADO.NET 连接池 |

LiteOrm 的方式更加声明式和集中化——配置在 JSON 中，实体通过特性关联，运行时一行代码切换。

## 实体主键类型

LiteOrm 支持多种主键类型：

```csharp
// int 自增主键（最常见）
[Column("Id", IsPrimaryKey = true, IsIdentity = true)]
public int Id { get; set; }

// long 自增主键
[Column("Id", IsPrimaryKey = true, IsIdentity = true)]
public long Id { get; set; }

// Guid 主键
[Column("Id", IsPrimaryKey = true)]
public Guid Id { get; set; }

// string 主键（需自行赋值）
[Column("Code", IsPrimaryKey = true)]
public string Code { get; set; } = string.Empty;
```

> 对于 `Guid` 主键，插入时不会自动生成。建议在构造函数中赋值 `Id = Guid.NewGuid()` 或在插入前手动赋值。

## 最佳实践

1. **实体和表名对应**：使用有意义的表名，实体名建议与表名保持一致或转换（如 `UserProfile` → `UserProfiles`）
2. **主键约定**：总是显式声明主键，不要依赖默认行为
3. **Constant 谨慎使用**：只用于真正固定不变的条件，不要用于当前用户/当前请求的过滤
4. **多数据源命名规范**：使用有意义的名称如 `MainDb`、`ReadDb`、`LogDb` 而非 `Db1`、`Db2`
5. **连接池**：生产环境务必根据并发量合理配置 `PoolSize` 和 `MaxPoolSize`

## 下一篇预告

[第3篇：Lambda 查询与 CRUD 操作](../series/03-lambda-crud.md) — 全面掌握增删改查、排序分页、批量操作和异步流式查询。