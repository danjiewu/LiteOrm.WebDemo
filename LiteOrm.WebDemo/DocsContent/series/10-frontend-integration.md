# 第10篇：前端集成

> ⏱ 阅读约 20 分钟

LiteOrm 的 Expr 表达式系统一个独特优势是：**Expr 可以被序列化为 JSON，从而在前端和后端之间双向传递**。这让前端可以动态构造查询条件，后端安全解析并执行。

## 三种前端集成方式

| 方式 | 复杂度 | 灵活性 | 适用场景 |
|------|--------|--------|---------|
| **QueryString** | 低 | 低 | 简单筛选，如搜索框、状态筛选 |
| **Expr JSON 分页查询** | 中 | 高 | 高级查询构建器、可视化筛选面板 |
| **泛型 Controller（示例）** | 中 | 中 | 标准 CRUD 管理后台（参考 WebDemo 实现） |

## 方式1：QueryString 查询

QueryString 是最简单的前端查询方式，适合简单的筛选条件。

### 后端定义

```csharp
[ApiController]
[Route("api/users")]
public class UsersController : ControllerBase
{
    private readonly IUserViewService _userViewService;

    [HttpGet("query")]
    public async Task<IActionResult> Query(
        [FromQuery] string? keyword,
        [FromQuery] int? minAge,
        [FromQuery] int? maxAge,
        [FromQuery] int? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        using static LiteOrm.Common.Expr;

        LogicExpr? filter = null;

        if (!string.IsNullOrEmpty(keyword))
        {
            filter &= Prop("UserName").Contains(keyword)
                    | Prop("Email").Contains(keyword);
        }

        if (minAge.HasValue)
            filter &= Prop("Age") >= minAge.Value;

        if (maxAge.HasValue)
            filter &= Prop("Age") <= maxAge.Value;

        if (status.HasValue)
            filter &= Prop("Status") == status.Value;

        var query = From<UserView>()
            .Where(filter)
            .OrderBy(Prop("Id").Desc())
            .Section((page - 1) * pageSize, pageSize);

        var items = await _userViewService.SearchAsync(query);
        var total = await _userViewService.CountAsync(filter);

        return Ok(new { total, page, pageSize, items });
    }
}
```

### 前端调用

```javascript
// GET /api/users/query?keyword=john&minAge=18&status=1&page=1&pageSize=20
const params = new URLSearchParams({
    keyword: 'john',
    minAge: 18,
    status: 1,
    page: 1,
    pageSize: 20
});

const response = await fetch(`/api/users/query?${params}`);
const result = await response.json();
// { total: 100, page: 1, pageSize: 20, items: [...] }
```

### QueryString 的优缺点

优点：实现简单，前端只需拼接 URL 参数
缺点：查询维度有限，无法表达复杂条件（如 OR、嵌套条件）

## 方式2：原生 Expr JSON 查询

这是 LiteOrm 最强大的前端集成方式。前端可以构造完整的 Expr JSON，后端直接反序列化执行。

### Expr JSON 格式

LiteOrm 支持两种序列化模式：

**简洁模式**（更紧凑）：

```json
{
  "$": "&",
  "Left": { "$": ">=", "Left": {"#": "Age"}, "Right": {"@": 18} },
  "Right": { "$": "==", "Left": {"#": "Status"}, "Right": {"@": 1} }
}
```

对应的 C# 表达式：`Prop("Age") >= 18 & Prop("Status") == 1`

**JSON 符号速查**：

| 符号 | 含义 | 对应 C# |
|------|------|---------|
| `"#"` | 属性引用 | `Prop("xxx")` |
| `"@"` | 常量值 | `Const(xxx)` 或直接值 |
| `"$"` | 操作符 | `&` `\|` `==` `>=` 等 |
| `"$in"` | IN 操作 | `.In(1,2,3)` |
| `"$contains"` | LIKE | `.Contains("xxx")` |
| `"$section"` | 分页 | `.Section(0, 20)` |
| `"$orderby"` | 排序 | `.OrderBy(...)` |
| `"$where"` | 条件 | `.Where(...)` |

### 后端 API

```csharp
[ApiController]
[Route("api/users")]
public class UsersController : ControllerBase
{
    [HttpPost("query/expr")]
    public async Task<IActionResult> ExprQuery([FromBody] JsonElement exprJson)
    {
        // 1. 反序列化 Expr
        Expr? expr;
        try
        {
            expr = exprJson.Deserialize<Expr>();
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "Invalid Expr JSON", detail = ex.Message });
        }

        if (expr == null)
            return BadRequest(new { error = "Empty request body" });

        // 2. 安全验证
        var validator = ExprValidatorGroup.Create(
            ExprValidator.CreateQueryOnly(),      // 只允许查询类型
            FunctionExprValidator.AllowRegisted    // 只允许已注册的函数
        );

        if (!validator.VisitAll(expr))
        {
            return BadRequest(new
            {
                error = "Expr contains disallowed operations",
                failedType = validator.FailedExpr?.GetType().Name
            });
        }

        // 3. 提取分页参数
        int skip = 0, take = 20;
        if (expr is SectionExpr section)
        {
            skip = Math.Max(0, section.Skip);
            take = Math.Clamp(section.Take, 1, 100);
        }
        else
        {
            // 非分页查询，自动包装
            expr = From<UserView>().Where(expr as LogicExpr).Section(skip, take);
        }

        // 4. 执行查询
        var countExpr = ExtractFilter(expr);
        var total = await _userViewService.CountAsync(countExpr);
        var items = await _userViewService.SearchAsync(expr);

        return Ok(new { skip, take, total, items });
    }

    private static Expr? ExtractFilter(Expr? expr) => expr switch
    {
        SectionExpr s => ExtractFilter(s.Source),
        OrderByExpr o => ExtractFilter(o.Source),
        WhereExpr w => w.Where,
        LogicExpr l => l,
        _ => null
    };
}
```

### 前端调用示例

```javascript
// 构造查询：Age >= 18 AND Status == 1，分页 0-20
const expr = {
    "$section": {
        "$orderby": {
            "$where": {
                "$": "&",
                "Left": { "$": ">=", "Left": {"#": "Age"}, "Right": {"@": 18} },
                "Right": { "$": "==", "Left": {"#": "Status"}, "Right": {"@": 1} }
            },
            "OrderBys": [
                { "Field": {"#": "CreateTime"}, "Asc": false }
            ]
        }
    },
    "Skip": 0,
    "Take": 20
};

const response = await fetch('/api/users/query/expr', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(expr)
});

const result = await response.json();
// { skip: 0, take: 20, total: 156, items: [...] }
```

### 前端查询构建器

在实际项目中，可以封装一个前端查询构建器：

```javascript
// 前端 Expr 构建器（简化版）
class ExprBuilder {
    static prop(name) { return { "#": name }; }
    static const(val) { return { "@": val }; }

    static eq(left, right) { return { "$": "==", Left: left, Right: right }; }
    static gte(left, right) { return { "$": ">=", Left: left, Right: right }; }
    static lte(left, right) { return { "$": "<=", Left: left, Right: right }; }
    static contains(prop, val) { return { "$": "contains", Left: prop, Right: { "@": val } }; }
    static in(prop, vals) { return { "$in": vals.map(v => ({ "@": v })), Left: prop }; }
    static and(...exprs) { return exprs.reduce((a, b) => ({ "$": "&", Left: a, Right: b })); }
    static or(...exprs) { return exprs.reduce((a, b) => ({ "$": "|", Left: a, Right: b })); }

    static query(where, orderBy, skip, take) {
        return {
            "$section": {
                "$orderby": { "$where": { Where: where }, OrderBys: orderBy }
            },
            Skip: skip,
            Take: take
        };
    }
}

// 使用构建器
const filter = ExprBuilder.and(
    ExprBuilder.gte(ExprBuilder.prop("Age"), ExprBuilder.const(18)),
    ExprBuilder.eq(ExprBuilder.prop("Status"), ExprBuilder.const(1))
);

const query = ExprBuilder.query(
    filter,
    [{ Field: ExprBuilder.prop("CreateTime"), Asc: false }],
    0, 20
);
```

## 方式3：泛型 Controller（示例参考）

对于标准 CRUD 管理后台，可以借鉴 LiteOrm 的 WebDemo 中提供的 `EntityControllerBase<T, TView>` 泛型基类示例。该基类位于 `LiteOrm.WebDemo.Controllers`，并非框架内建组件，而是作为参考实现供开发者自行集成使用。

```csharp
// 定义泛型基类（参考 WebDemo 中的 EntityControllerBase）
[ApiController]
[Route("api/[controller]")]
public abstract class EntityControllerBase<T, TView> : ControllerBase
    where T : class
    where TView : class
{
    protected IEntityServiceAsync<T> EntityService =>
        HttpContext.RequestServices.GetRequiredService<IEntityServiceAsync<T>>();
    protected IEntityViewServiceAsync<TView> ViewService =>
        HttpContext.RequestServices.GetRequiredService<IEntityViewServiceAsync<TView>>();

    [HttpGet("{id}")]
    public virtual async Task<TView?> GetById(object id) =>
        await ViewService.GetObjectAsync(id);

    [HttpGet]
    public virtual async Task<List<TView>> List() =>
        await ViewService.SearchAsync();

    [HttpPost]
    public virtual async Task<bool> Create(T entity) =>
        await EntityService.InsertAsync(entity);

    [HttpPut]
    public virtual async Task<bool> Update(T entity) =>
        await EntityService.UpdateAsync(entity);

    [HttpDelete("{id}")]
    public virtual async Task<bool> Delete(object id) =>
        await EntityService.DeleteIDAsync(id);

    [HttpPost("page")]
    public virtual async Task<IActionResult> PageQuery([FromBody] JsonElement exprJson)
    {
        // 内置分页查询（类似上面的 ExprQuery 实现）
        // ...
    }
}

// 具体实体 Controller —— 一行代码！
public class UsersController : EntityControllerBase<User, UserView> { }
public class OrdersController : EntityControllerBase<Order, OrderView> { }
public class ProductsController : EntityControllerBase<Product, ProductView> { }
```

这自动为每个实体生成以下 API：

| 方法 | 路由 | 说明 |
|------|------|------|
| GET | `/api/users` | 列表查询 |
| GET | `/api/users/{id}` | 按ID查询 |
| POST | `/api/users` | 创建 |
| PUT | `/api/users` | 更新 |
| DELETE | `/api/users/{id}` | 删除 |
| POST | `/api/users/page` | 分页查询（接受 Expr JSON） |

> `EntityControllerBase` 位于 `LiteOrm.WebDemo` 项目中，是一个完整的参考示例。你可以直接复制到自己的项目中使用，也可以根据业务需求定制（如增加权限过滤、QueryString 查询等）。

### 动态 Controller 生成

当实体数量很多时，甚至可以通过 `System.Reflection.Emit` 在运行时动态生成 Controller：

```csharp
var dynamicAssembly = BuildDynamicControllers("MyApp");
builder.Services.AddControllers()
    .AddApplicationPart(dynamicAssembly);
```

## 安全注意事项

前端提交 Expr JSON 时必须做好安全防护：

1. **启用 ExprValidator**：`CreateQueryOnly()` 限制只能执行查询操作
2. **启用函数策略**：`FunctionExprValidator.AllowRegisted` 限制只能调用已注册的函数
3. **限制分页大小**：`take` 设置上限（如 100），防止大查询拖垮数据库
4. **叠加权限过滤**：在 Expr 基础上追加当前用户/租户的过滤条件
5. **字段白名单**（可选）：限制只能查询特定列

```csharp
// 安全过滤链
var expr = DeserializeExpr(json);

// 1. 追加用户权限过滤
if (!IsAdmin(currentUser))
    expr &= Prop("CreatedByUserId") == currentUser.Id;

// 2. 追加软删除过滤
expr &= Prop("IsDeleted") == false;

// 3. 安全验证
if (!validator.VisitAll(expr))
    throw new UnauthorizedAccessException();

// 4. 执行查询
var result = await service.SearchAsync(expr);
```

## 与其他 ORM 的前端集成对比

| 能力 | LiteOrm | EF Core | SqlSugar | FreeSql |
|------|---------|---------|----------|---------|
| 前端查询协议 | Expr JSON（内置） | 无 | 无 | 无 |
| QueryString | 手动构建 | 手动构建 | 手动构建 | 手动构建 |
| 泛型控制器 | WebDemo 提供示例基类 | 需自建 | 需自建 | 需自建 |
| 动态Controller | WebDemo 提供示例 | 需自建 | 需自建 | 需自建 |
| 安全验证 | ExprValidator 内建 | 需自建 | 需自建 | 需自建 |

LiteOrm 在 Expr JSON 序列化和安全验证方面有内建方案，泛型 Controller 和动态生成则通过 WebDemo 提供参考示例。

## 最佳实践

1. **简单筛选用 QueryString**：实现快，URL 可分享
2. **复杂筛选用 Expr JSON**：支持可视化查询构建器
3. **管理后台可参考 WebDemo 的泛型 Controller 示例**：大幅减少重复代码
4. **安全验证不可省略**：前端提交的 Expr 必须经过验证器
5. **权限过滤在后端叠加**：不要依赖前端传来的条件作为权限边界

## 下一篇预告

[第11篇：日志诊断与权限过滤](../series/11-logging-and-permissions.md) — ServiceLog、慢查询诊断、ExceptionHook、权限过滤和数据安全。